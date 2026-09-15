using Craftimizer.Simulator;
using Craftimizer.Simulator.Actions;
using ECommons.DalamudServices;
using Artisan.GameInterop;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArtisanCondition = Artisan.CraftingLogic.CraftData.Condition;
using ArtisanSolver = Artisan.CraftingLogic.Solver;
using CraftimizerAction = Craftimizer.Simulator.Actions.ActionType;
using CraftimizerCoreSolver = Craftimizer.Solver.Solver;
using CraftimizerSolverAlgorithm = Craftimizer.Solver.SolverAlgorithm;
using CraftimizerSolverConfig = Craftimizer.Solver.SolverConfig;
using Skills = Artisan.RawInformation.Character.Skills;

namespace Artisan.CraftingLogic.Solvers;

public sealed class CraftimizerSolverDefinition : ISolverDefinition
{
    public IEnumerable<ISolverDefinition.Desc> Flavours(CraftState craft)
    {
        // Priority 0 keeps every existing Artisan default unchanged. ICE or the
        // recipe UI must explicitly select this solver.
        yield return new(this, 0, 0, "Craftimizer Recipe Solver",
            craft.ConditionFlags == 0 ? "配方沒有可用的製作狀態資料" : "");
    }

    public ArtisanSolver Create(CraftState craft, int flavour) => new CraftimizerSolver(craft);
}

/// <summary>
/// Adapts the API13/net9 Craftimizer 2.11 solver core to Artisan. Craftimizer
/// never executes an action; it only calculates one recommendation at a time.
/// Artisan remains responsible for crafting state, action execution, retries,
/// Endurance, lists, IPC, and mission flow.
/// </summary>
public sealed class CraftimizerSolver : ArtisanSolver, IAsyncSolver
{
    private static readonly TimeSpan RecommendationTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan PreviewTimeout = TimeSpan.FromSeconds(5);
    private readonly ArtisanSolver _fallback;
    private readonly CraftState _initialCraft;
    private int _lastObservedStepIndex;
    private Skills _lastObservedAction = Skills.None;
    private ActionProc _lastComboState;
    private string? _fallbackOnlyReason;

    public CraftimizerSolver(CraftState craft)
    {
        _initialCraft = craft with { };
        _fallback = craft.CraftExpert ? new ExpertSolver() : new StandardSolver(false);
    }

    public override ArtisanSolver Clone() => new CraftimizerSolver(_initialCraft);

    // Offline Artisan simulations are synchronous. Use the existing safe
    // Artisan solver there; live crafting is routed through SolveAsync.
    public override Recommendation Solve(CraftState craft, StepState step) =>
        _fallback.Solve(craft, step) with { Comment = "預覽模擬使用 Artisan 安全備援" };

    public Task<Recommendation> SolveAsync(CraftState craft, StepState step, CancellationToken cancellationToken)
    {
        var comboState = GetComboState(step);

        // Material Miracle is a Cosmic-only duty action that Craftimizer does
        // not expose. Keep Artisan's proven policy for deciding when to press
        // it, then let Craftimizer solve the resulting expert conditions.
        // The live game only discounts Advanced Touch after the complete
        // Basic -> Standard chain. Older Artisan treats every Standard Touch
        // as a combo starter, so clear that stale marker for fallback solving.
        var fallbackStep = step.PrevComboAction == Skills.StandardTouch && comboState != ActionProc.AdvancedTouch
            ? step with { PrevComboAction = Skills.None }
            : step;
        var artisanRecommendation = _fallback.Solve(craft, fallbackStep);
        if (_fallbackOnlyReason != null)
            return Task.FromResult(artisanRecommendation with
            {
                Comment = $"本次製作已停用 Craftimizer，改用 Artisan 備援；原因：{LocalizeFallbackReason(_fallbackOnlyReason)}",
            });

        if (craft.MissionHasMaterialMiracle && P.Config.UseMaterialMiracle &&
            artisanRecommendation.Action == Skills.MaterialMiracle)
        {
            Svc.Log.Debug($"[Craftimizer Solver] Step {step.Index}: Artisan selected Material Miracle; preserving the Cosmic duty action");
            return Task.FromResult(artisanRecommendation with { Comment = "宇宙製作橋接：保留「素材奇蹟」技能" });
        }

        var materialMiracleSeconds = step.MaterialMiracleActive
            ? Crafting.MaterialMiracleRemainingSeconds()
            : 0;
        var inputState = BuildSimulationState(craft, step, materialMiracleSeconds, comboState);
        Svc.Log.Debug(
            $"[Craftimizer Solver] Step {step.Index}: cosmic={craft.IsCosmic}, " +
            $"progress={step.Progress}/{craft.CraftProgress}, quality={step.Quality}/{craft.CraftQualityMax}, " +
            $"durability={step.Durability}, cp={step.RemainingCP}, condition={step.Condition}, " +
            $"materialMiracle={materialMiracleSeconds:0.0}s, maxSteps={inputState.ActionCount + 48}");
        return Task.Run(
            () => CalculateRecommendation(craft, step, inputState, artisanRecommendation, cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// Produces one complete, bounded preview rotation with a single Craftimizer
    /// core invocation. This is deliberately separate from live crafting, where
    /// the solver recalculates after each action using the current game state.
    /// </summary>
    public Task<IReadOnlyList<Skills>?> SolvePreviewPlanAsync(
        CraftState craft,
        StepState step,
        CancellationToken cancellationToken) =>
        Task.Run(() => CalculatePreviewPlanAsync(craft, step, cancellationToken), cancellationToken);

    private async Task<IReadOnlyList<Skills>?> CalculatePreviewPlanAsync(
        CraftState craft,
        StepState step,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PreviewTimeout);

        var inputState = BuildSimulationState(craft, step, 0, ActionProc.None);
        var config = CreatePreviewSolverConfig(craft, inputState);
        using var solver = new CraftimizerCoreSolver(config, inputState) { Token = timeout.Token };
        solver.OnLog += message => Svc.Log.Verbose($"[Craftimizer Preview] {message}");
        solver.OnWarn += message => Svc.Log.Warning($"[Craftimizer Preview] {message}");

        try
        {
            Svc.Log.Information(
                $"[Craftimizer Preview] Starting one bounded solve: maxTime={config.MaxTimeMs}ms, " +
                $"threads={config.MaxThreadCount}, maxSteps={config.MaxStepCount}");
            solver.Start();
            var solution = await solver.GetSafeTask().ConfigureAwait(false);
            if (solution == null || solution.Value.Actions.Count == 0)
                return null;

            var mapped = new List<Skills>(solution.Value.Actions.Count);
            foreach (var action in solution.Value.Actions)
            {
                var skill = MapAction(action);
                if (skill == Skills.None)
                {
                    Svc.Log.Warning($"[Craftimizer Preview] Could not map preview action {action}");
                    return null;
                }
                mapped.Add(skill);
            }

            Svc.Log.Information(
                $"[Craftimizer Preview] Completed one bounded solve in {stopwatch.ElapsedMilliseconds}ms " +
                $"with {mapped.Count} actions");
            return mapped;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Svc.Log.Warning($"[Craftimizer Preview] Timed out after {stopwatch.ElapsedMilliseconds}ms");
            return null;
        }
    }

    private async Task<Recommendation> CalculateRecommendation(
        CraftState craft,
        StepState step,
        SimulationState inputState,
        Recommendation artisanRecommendation,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RecommendationTimeout);

        var config = CreateSolverConfig(craft, inputState);
        using var solver = new CraftimizerCoreSolver(config, inputState) { Token = timeout.Token };
        solver.OnLog += message => Svc.Log.Verbose($"[Craftimizer Solver] {message}");
        solver.OnWarn += message => Svc.Log.Warning($"[Craftimizer Solver] {message}");

        try
        {
            solver.Start();
            var completedSolution = solver.GetSafeTask();
            var solution = await completedSolution.ConfigureAwait(false);
            if (solution == null || solution.Value.Actions.Count == 0)
                return FallbackForCraft(step, artisanRecommendation,
                    timeout.IsCancellationRequested ? "timed out" : "returned no solution");
            var action = solution.Value.Actions[0];

            cancellationToken.ThrowIfCancellationRequested();
            var mapped = MapAction(action);
            if (mapped == Skills.None)
                return FallbackForCraft(step, artisanRecommendation, $"could not map action {action}");
            if (Simulator.CannotUseAction(craft, step, mapped, out var reason))
                return FallbackForCraft(step, artisanRecommendation, $"recommended unusable action {action}: {reason}");

            Svc.Log.Debug($"[Craftimizer Solver] Step {step.Index}: {action} mapped to {mapped} in {stopwatch.ElapsedMilliseconds} ms");
            return new(mapped, $"Craftimizer 2.11；Next Action 計算耗時 {stopwatch.ElapsedMilliseconds} 毫秒");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return FallbackForCraft(step, artisanRecommendation, "timed out");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Svc.Log.Error(e, "[Craftimizer Solver] Calculation failed; using Artisan fallback");
            return FallbackForCraft(step, artisanRecommendation, e.Message);
        }
    }

    private Recommendation FallbackForCraft(StepState step, Recommendation artisanRecommendation, string reason)
    {
        _fallbackOnlyReason ??= reason;
        Svc.Log.Warning(
            $"[Craftimizer Solver] Step {step.Index}: {reason}; disabling Craftimizer for the remainder of this craft and using Artisan fallback");
        return artisanRecommendation with
        {
            Comment = $"Craftimizer 無法繼續，已改用 Artisan 備援；原因：{LocalizeFallbackReason(reason)}",
        };
    }

    private static string LocalizeFallbackReason(string reason)
    {
        if (reason == "timed out")
            return "計算逾時";
        if (reason == "returned no solution")
            return "找不到可行解";
        if (reason.StartsWith("could not map action ", StringComparison.Ordinal))
            return "無法對應建議技能";
        if (reason.StartsWith("recommended unusable action ", StringComparison.Ordinal))
            return "建議技能目前無法使用";
        return "計算發生錯誤";
    }

    private ActionProc GetComboState(StepState step)
    {
        if (_lastObservedStepIndex == step.Index)
            return _lastComboState;

        var actionBeforePrevious = _lastObservedStepIndex != step.Index
            ? _lastObservedAction
            : Skills.None;

        var combo = step.PrevComboAction switch
        {
            Skills.BasicTouch => ActionProc.UsedBasicTouch,
            Skills.Observe => ActionProc.AdvancedTouch,
            Skills.StandardTouch when actionBeforePrevious == Skills.BasicTouch => ActionProc.AdvancedTouch,
            _ => ActionProc.None,
        };

        _lastObservedStepIndex = step.Index;
        _lastObservedAction = step.PrevComboAction;
        _lastComboState = combo;

        return combo;
    }

    private static CraftimizerSolverConfig CreateSolverConfig(CraftState craft, in SimulationState inputState)
    {
        var pool = CraftimizerSolverConfig.RandomizedActionPool
            .Where(action => !CraftimizerSolverConfig.RiskyActions.Contains(action))
            .ToArray();

        var threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        var forks = Math.Clamp(Environment.ProcessorCount, 4, 8);
        var config = CraftimizerSolverConfig.SynthHelperDefault with
        {
            // Craftimizer 2.11 concentrates a bounded wall-clock budget on
            // the best next action instead of producing a stale full macro.
            Algorithm = CraftimizerSolverAlgorithm.NextActionForked,
            MaxTimeMs = 1800,
            Iterations = 1_000_000,
            MaxIterations = 1_000_000,
            MaxThreadCount = threads,
            ForkCount = forks,
            FurcatedActionCount = Math.Max(2, forks / 2),
            PruneActionCount = threads,
            ScreenBudgetPercent = 30,
            QualityTargetPercent = 100,
            QualityTargetToMaxCollectability = false,
            // MaxStepCount is an absolute action-count ceiling. The first
            // prototype incorrectly kept it at 40 even when resuming at step
            // 30+, making valid Cosmic solutions mathematically unreachable.
            MaxStepCount = inputState.ActionCount + 48,
            ActionPool = pool,
            StrictActions = true,
        };

        return craft.Specialist ? config : config.FilterSpecialistActions();
    }

    private static CraftimizerSolverConfig CreatePreviewSolverConfig(
        CraftState craft,
        in SimulationState inputState)
    {
        var pool = CraftimizerSolverConfig.DeterministicActionPool
            .Where(action => !CraftimizerSolverConfig.RiskyActions.Contains(action))
            .ToArray();
        var config = CraftimizerSolverConfig.SynthHelperDefault with
        {
            Algorithm = CraftimizerSolverAlgorithm.NextActionForked,
            // The complete offline preview gets exactly one small solver job.
            // Never multiply this budget by the requested craft quantity.
            MaxTimeMs = 1500,
            Iterations = 100_000,
            MaxIterations = 100_000,
            MaxThreadCount = 1,
            ForkCount = 2,
            FurcatedActionCount = 1,
            PruneActionCount = 1,
            ScreenBudgetPercent = 35,
            QualityTargetPercent = 100,
            QualityTargetToMaxCollectability = false,
            MaxStepCount = inputState.ActionCount + 48,
            ActionPool = pool,
            StrictActions = true,
        };

        return craft.Specialist ? config : config.FilterSpecialistActions();
    }

    private static SimulationState BuildSimulationState(
        CraftState craft,
        StepState step,
        float materialMiracleSeconds,
        ActionProc comboState)
    {
        var stats = new Craftimizer.Simulator.CharacterStats
        {
            Craftsmanship = craft.StatCraftsmanship,
            Control = craft.StatControl,
            CP = craft.StatCP,
            Level = craft.StatLevel,
            CanUseManipulation = craft.UnlockedManipulation,
            HasSplendorousBuff = craft.SplendorCosmic,
            IsSpecialist = craft.Specialist,
        };
        var recipe = new RecipeInfo
        {
            IsExpert = craft.CraftExpert,
            ClassJobLevel = craft.CraftLevel,
            ConditionsFlag = (ushort)craft.ConditionFlags,
            MaxDurability = craft.CraftDurability,
            MaxQuality = craft.CraftQualityMax,
            MaxProgress = craft.CraftProgress,
            QualityModifier = craft.CraftQualityModifier,
            QualityDivider = craft.CraftQualityDivider,
            ProgressModifier = craft.CraftProgressModifier,
            ProgressDivider = craft.CraftProgressDivider,
        };
        var input = new SimulationInput(stats, recipe, craft.InitialQuality);
        var wasteNot = ClampByte(step.WasteNotLeft);

        return new SimulationState(input)
        {
            ActionCount = Math.Max(0, step.Index - 1),
            StepCount = Math.Max(0, step.Index - 1),
            Progress = step.Progress,
            Quality = step.Quality,
            Durability = step.Durability,
            CP = step.RemainingCP,
            Condition = MapCondition(step.Condition),
            MaterialMiracleSecondsRemaining = Math.Max(0, materialMiracleSeconds),
            ActiveEffects = new Effects
            {
                InnerQuiet = ClampByte(step.IQStacks),
                WasteNot = wasteNot <= 4 ? wasteNot : (byte)0,
                WasteNot2 = wasteNot > 4 ? wasteNot : (byte)0,
                Veneration = ClampByte(step.VenerationLeft),
                GreatStrides = ClampByte(step.GreatStridesLeft),
                Innovation = ClampByte(step.InnovationLeft),
                FinalAppraisal = ClampByte(step.FinalAppraisalLeft),
                MuscleMemory = ClampByte(step.MuscleMemoryLeft),
                Manipulation = ClampByte(step.ManipulationLeft),
                Expedience = ClampByte(step.ExpedienceLeft),
                TrainedPerfection = step.TrainedPerfectionActive,
                HeartAndSoul = step.HeartAndSoulActive,
            },
            ActionStates = new ActionStates
            {
                // Advanced Touch receives its reduced CP cost only after the
                // complete Basic -> Standard chain (or Observe), not after an
                // isolated Standard Touch. Preserve one observed action of
                // history so the solver matches the live game cost.
                Combo = comboState,
                CarefulObservationCount = ClampByte(Math.Max(0, 3 - step.CarefulObservationLeft)),
                UsedHeartAndSoul = !step.HeartAndSoulAvailable,
                UsedQuickInnovation = !step.QuickInnoAvailable,
                UsedTrainedPerfection = !step.TrainedPerfectionAvailable,
            },
        };
    }

    private static Craftimizer.Simulator.Condition MapCondition(ArtisanCondition condition) => condition switch
    {
        ArtisanCondition.Normal => Craftimizer.Simulator.Condition.Normal,
        ArtisanCondition.Good => Craftimizer.Simulator.Condition.Good,
        ArtisanCondition.Excellent => Craftimizer.Simulator.Condition.Excellent,
        ArtisanCondition.Poor => Craftimizer.Simulator.Condition.Poor,
        ArtisanCondition.Centered => Craftimizer.Simulator.Condition.Centered,
        ArtisanCondition.Sturdy => Craftimizer.Simulator.Condition.Sturdy,
        ArtisanCondition.Pliant => Craftimizer.Simulator.Condition.Pliant,
        ArtisanCondition.Malleable => Craftimizer.Simulator.Condition.Malleable,
        ArtisanCondition.Primed => Craftimizer.Simulator.Condition.Primed,
        ArtisanCondition.GoodOmen => Craftimizer.Simulator.Condition.GoodOmen,
        ArtisanCondition.Robust => Craftimizer.Simulator.Condition.Robust,
        _ => Craftimizer.Simulator.Condition.Normal,
    };

    private static Skills MapAction(CraftimizerAction action)
    {
        var name = action == CraftimizerAction.TricksOfTheTrade ? nameof(Skills.TricksOfTrade) : action.ToString();
        return Enum.TryParse<Skills>(name, out var mapped) ? mapped : Skills.None;
    }

    private static byte ClampByte(int value) => (byte)Math.Clamp(value, byte.MinValue, byte.MaxValue);
}
