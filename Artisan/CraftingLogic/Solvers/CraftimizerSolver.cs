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
using ArtisanConditionFlags = Artisan.CraftingLogic.CraftData.ConditionFlags;
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
    private const int MaxCachedPlans = 32;
    private static readonly TimeSpan RecommendationTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PreviewTimeout = TimeSpan.FromSeconds(5);
    private static readonly ValidatedCraftPlanCache ValidatedPlans = new(MaxCachedPlans);
    // Preview and live decisions share one worker allowance, not one each.
    private static readonly SemaphoreSlim SearchGate = new(1, 1);
    private readonly ArtisanSolver _fallback;
    private bool _reportedCosmicPolicy;
    private readonly CraftState _initialCraft;
    private int _lastObservedStepIndex;
    private Skills _lastObservedAction = Skills.None;
    private ActionProc _lastComboState;
    private string? _fallbackOnlyReason;
    private bool _reportedCachedPlan;

    private StepState? _fallbackStep;
    private Recommendation _fallbackRecommendation;

    public CraftimizerSolver(CraftState craft)
    {
        _initialCraft = craft with { };
        _fallback = craft.CraftExpert ? new ExpertSolver() : new StandardSolver(false);
    }

    public override ArtisanSolver Clone() => new CraftimizerSolver(_initialCraft);

    public static bool SupportsValidatedPlan(CraftState craft) => !craft.CraftExpert && !craft.IsCosmic;

    private static int PlanTarget(CraftState craft) => craft.CraftCollectible
        ? P.Config.SolverCollectibleMode switch
        {
            1 => craft.CraftQualityMin1,
            2 => craft.CraftQualityMin2,
            _ => craft.CraftQualityMin3,
        }
        : craft.CraftHQ ? craft.CraftQualityMax : craft.CraftRequiredQuality;

    public static bool HasValidatedPlan(CraftState craft, out int actionCount)
    {
        actionCount = 0;
        return SupportsValidatedPlan(craft) && ValidatedPlans.Contains(craft, PlanTarget(craft), out actionCount);
    }

    public static bool TryGetValidatedPlan(CraftState craft, int targetQuality, out IReadOnlyList<Skills> plan)
    {
        plan = Array.Empty<Skills>();
        return SupportsValidatedPlan(craft) && ValidatedPlans.TryGetPlan(craft, targetQuality, out plan);
    }

    public static void CacheValidatedPlan(CraftState craft, IReadOnlyList<Skills> plan, int targetQuality)
    {
        if (!SupportsValidatedPlan(craft) || plan.Count == 0)
            return;
        var step = Simulator.CreateInitial(craft, craft.InitialQuality);
        var before = new List<StepState>(plan.Count);
        foreach (var action in plan)
        {
            if (Simulator.Status(craft, step) != Simulator.CraftStatus.InProgress ||
                Simulator.GetSuccessRate(step, action) < 1.0)
                return;
            before.Add(step);
            var (result, next) = Simulator.Execute(craft, step, action, 0, 1);
            if (result != Simulator.ExecuteResult.Succeeded)
                return;
            step = next;
        }
        if (step.Progress < craft.CraftProgress || step.Quality < targetQuality)
            return;
        ValidatedPlans.Store(craft, targetQuality, plan, before);
        Svc.Log.Information(
            $"[Craftimizer Plan Cache] Stored recipe {craft.RecipeId}, startingQuality={craft.InitialQuality}, " +
            $"target={targetQuality}, actions={plan.Count}; validated state replay, no repeat MCTS");
    }

    // Offline Artisan simulations are synchronous. Use the existing safe
    // Artisan solver there; live crafting is routed through SolveAsync.
    public override Recommendation Solve(CraftState craft, StepState step) =>
        SolveFallback(craft, step, GetComboState(step)) with { Comment = "預覽模擬使用 Artisan 輕量求解" };

    private Recommendation SolveFallback(CraftState craft, StepState step, ActionProc comboState)
    {
        // A watchdog must reuse this step's recommendation rather than mutate
        // the stateful Standard/Expert solver a second time.
        if (_fallbackStep == step)
            return _fallbackRecommendation;
        var normalized = step.PrevComboAction == Skills.StandardTouch && comboState != ActionProc.AdvancedTouch
            ? step with { PrevComboAction = Skills.None } : step;
        _fallbackRecommendation = _fallback.Solve(craft, normalized);
        _fallbackStep = step with { };
        return _fallbackRecommendation;
    }

    public Task<Recommendation> SolveAsync(CraftState craft, StepState step, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var comboState = GetComboState(step);

        // Material Miracle is a Cosmic-only duty action that Craftimizer does
        // not expose. Keep Artisan's proven policy for deciding when to press
        // it, then let Craftimizer solve the resulting expert conditions.
        // The live game only discounts Advanced Touch after the complete
        // Basic -> Standard chain. Older Artisan treats every Standard Touch
        // as a combo starter, so clear that stale marker for fallback solving.
        var artisanRecommendation = SolveFallback(craft, step, comboState);
        if (_fallbackOnlyReason != null)
            return Task.FromResult(artisanRecommendation with
            {
                Comment = $"本次製作已停用 Craftimizer，改用 Artisan 備援；原因：{LocalizeFallbackReason(_fallbackOnlyReason)}",
            });

        // Cosmic conditions and timed duty actions need live state, not a fixed
        // rotation. Use Artisan's established dynamic policy without allocating
        // a fresh MCTS forest on every action (especially costly on MacBook Air).
        if (craft.IsCosmic)
        {
            if (!_reportedCosmicPolicy)
            {
                _reportedCosmicPolicy = true;
                Svc.Log.Information($"[Craftimizer Resource Policy] Cosmic recipe {craft.RecipeId}: Artisan dynamic solver; no live MCTS workers");
            }
            return Task.FromResult(artisanRecommendation with { Comment = "宇宙製作：Artisan 低資源動態求解" });
        }

        if (craft.MissionHasMaterialMiracle && P.Config.UseMaterialMiracle &&
            artisanRecommendation.Action == Skills.MaterialMiracle)
        {
            Svc.Log.Debug($"[Craftimizer Solver] Step {step.Index}: Artisan selected Material Miracle; preserving the Cosmic duty action");
            return Task.FromResult(artisanRecommendation with { Comment = "宇宙製作橋接：保留「素材奇蹟」技能" });
        }

        // Standard bulk crafts are deterministic enough to replay the complete
        // plan that was already simulated and validated by the Allagan IPC
        // preflight. Never start a fresh MCTS tree for every live action: doing
        // that multiplies allocations by steps * requested quantity and can
        // push Wine into swap or an exit-code-137 kill. Expert/Cosmic crafts
        // retain the existing state-aware path below.
        if (SupportsValidatedPlan(craft))
        {
            if (!ValidatedPlans.TryGetAction(craft, PlanTarget(craft), step, out var cachedAction, out var planLength))
                return Task.FromResult(FallbackForCraft(step, artisanRecommendation,
                    "cached plan inputs or live state did not match"));

            if (Simulator.CannotUseAction(craft, step, cachedAction, out var cachedReason))
                return Task.FromResult(FallbackForCraft(step, artisanRecommendation,
                    $"cached action {cachedAction} was unusable: {cachedReason}"));

            if (!_reportedCachedPlan)
            {
                _reportedCachedPlan = true;
                Svc.Log.Information(
                    $"[Craftimizer Plan Cache] Replaying validated recipe {craft.RecipeId} plan ({planLength} actions); no live MCTS solves");
            }
            return Task.FromResult(new Recommendation(cachedAction,
                $"Craftimizer 2.11 安全快取；步驟 {step.Index}/{planLength}"));
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
        // A cancelled search may still be unwinding. Never queue or overlap a
        // second forest; hold this gate until the worker has actually finished.
        if (!SearchGate.Wait(0))
            return Task.FromResult(FallbackForCraft(step, artisanRecommendation, "previous search still running"));
        return Task.Run(async () =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return await CalculateRecommendation(craft, step, inputState, artisanRecommendation, cancellationToken).ConfigureAwait(false);
            }
            finally { SearchGate.Release(); }
        });
    }

    /// <summary>
    /// Produces one complete, bounded preview rotation with a single Craftimizer
    /// core invocation. This is deliberately separate from live crafting, where
    /// the solver recalculates after each action using the current game state.
    /// </summary>
    public Task<IReadOnlyList<Skills>?> SolvePreviewPlanAsync(
        CraftState craft,
        StepState step,
        CancellationToken cancellationToken,
        int maxTimeMs = 1500,
        int maxIterations = 100_000) =>
        Task.Run(async () =>
        {
            await SearchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await CalculatePreviewPlanAsync(craft, step, cancellationToken,
                    maxTimeMs, maxIterations).ConfigureAwait(false);
            }
            finally { SearchGate.Release(); }
        }, cancellationToken);

    private async Task<IReadOnlyList<Skills>?> CalculatePreviewPlanAsync(
        CraftState craft,
        StepState step,
        CancellationToken cancellationToken, int maxTimeMs, int maxIterations)
    {
        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PreviewTimeout);

        var inputState = BuildSimulationState(craft, step, 0, ActionProc.None);
        var config = CreatePreviewSolverConfig(craft, inputState) with
        {
            MaxTimeMs = Math.Clamp(maxTimeMs, 1, 1500),
            Iterations = Math.Clamp(maxIterations, 1, 100_000),
            MaxIterations = Math.Clamp(maxIterations, 1, 100_000),
        };
        using var solver = new CraftimizerCoreSolver(config, inputState) { Token = timeout.Token };
        solver.OnLog += message => Svc.Log.Verbose($"[Craftimizer Preview] {message}");
        solver.OnWarn += message => Svc.Log.Warning($"[Craftimizer Preview] {message}");

        try
        {
            Svc.Log.Information(
                $"[Craftimizer Preview] Starting one bounded solve: maxTime={config.MaxTimeMs}ms, " +
                $"threads={config.MaxThreadCount}, maxSteps={config.MaxStepCount}, iterations={config.MaxIterations}");
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
                $"with {mapped.Count} actions, iterations={solver.SearchIterations}");
            return mapped;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Svc.Log.Warning($"[Craftimizer Preview] Timed out after {stopwatch.ElapsedMilliseconds}ms");
            return null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Svc.Log.Warning(e, "[Craftimizer Preview] Bounded comparison failed; preserving the validated baseline");
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

    public Recommendation RecoverTimedOutRecommendation(CraftState craft, StepState step)
    {
        return FallbackForCraft(step, SolveFallback(craft, step, GetComboState(step)), "timed out");
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
        if (reason == "cached plan inputs or live state did not match")
            return "快取輸入或目前製作狀態不同";
        if (reason == "previous search still running")
            return "上一個搜尋尚未結束";
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

        const int threads = 1;
        const int forks = 2;
        var config = CraftimizerSolverConfig.SynthHelperDefault with
        {
            // Craftimizer 2.11 concentrates a bounded wall-clock budget on
            // the best next action instead of producing a stale full macro.
            Algorithm = CraftimizerSolverAlgorithm.NextActionForked,
            MaxTimeMs = 750,
            Iterations = 100_000,
            MaxIterations = 100_000,
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
