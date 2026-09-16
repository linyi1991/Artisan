using Artisan.Autocraft;
using Artisan.CraftingLogic.Solvers;
using Artisan.GameInterop;
using Artisan.RawInformation;
using Artisan.RawInformation.Character;
using ECommons.DalamudServices;
using ECommons.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Artisan.CraftingLogic;

// monitors crafting state changes and provides recommendation based on assigned solver algorithm
// TODO: toasts etc should be moved outside - this should provide events instead
public static class CraftingProcessor
{
    public static Solver.Recommendation NextRec => _nextRec;
    public static SolverRef ActiveSolver = new("");

    public delegate void SolverStartedDelegate(Lumina.Excel.Sheets.Recipe recipe, SolverRef solver, CraftState craft, StepState initialStep);
    public static event SolverStartedDelegate? SolverStarted;

    public delegate void SolverFailedDelegate(Lumina.Excel.Sheets.Recipe recipe, string reason);
    public static event SolverFailedDelegate? SolverFailed; // craft started, but solver couldn't

    public delegate void SolverFinishedDelegate(Lumina.Excel.Sheets.Recipe recipe, SolverRef solver, CraftState craft, StepState finalStep);
    public static event SolverFinishedDelegate? SolverFinished;

    public delegate void RecommendationReadyDelegate(Lumina.Excel.Sheets.Recipe recipe, SolverRef solver, CraftState craft, StepState step, Solver.Recommendation recommendation);
    public static event RecommendationReadyDelegate? RecommendationReady;

    public static List<ISolverDefinition> SolverDefinitions = new();
    private static Solver? _activeSolver; // solver for current or expected crafting session
    private static uint? _expectedRecipe; // non-null and equal to recipe id if we've requested start of a specific craft (with a specific solver) and are waiting for it to start
    private static Solver.Recommendation _nextRec;
    private static CancellationTokenSource? _pendingRecommendationCancellation;
    private static Task<Solver.Recommendation>? _pendingRecommendation;
    private static long _pendingRecommendationStarted;
    private static Lumina.Excel.Sheets.Recipe _pendingRecipe;
    private static CraftState? _pendingCraft;
    private static StepState? _pendingStep;

    public static void Setup()
    {
        SolverDefinitions.Add(new StandardSolverDefinition());
        SolverDefinitions.Add(new ProgressOnlySolverDefinition());
        SolverDefinitions.Add(new ExpertSolverDefinition());
        SolverDefinitions.Add(new MacroSolverDefinition());
        SolverDefinitions.Add(new ScriptSolverDefinition());
        SolverDefinitions.Add(new RaphaelSolverDefintion());
        SolverDefinitions.Add(new CraftimizerSolverDefinition());

        Crafting.CraftStarted += OnCraftStarted;
        Crafting.CraftAdvanced += OnCraftAdvanced;
        Crafting.CraftFinished += OnCraftFinished;
    }

    public static void Dispose()
    {
        CancelPendingRecommendation();
        Crafting.CraftStarted -= OnCraftStarted;
        Crafting.CraftAdvanced -= OnCraftAdvanced;
        Crafting.CraftFinished -= OnCraftFinished;
    }

    public static void Update()
    {
        var pending = _pendingRecommendation;
        if (pending == null)
            return;
        if (!pending.IsCompleted)
        {
            // Cancellation is cooperative; the UI must not wait indefinitely
            // for an MCTS worker to acknowledge it. Recovery stays on framework.
            if (_activeSolver is CraftimizerSolver solver && _pendingCraft is { } waitingCraft &&
                _pendingStep is { } waitingStep &&
                Stopwatch.GetElapsedTime(_pendingRecommendationStarted).TotalSeconds >= 3)
            {
                var waitingRecipe = _pendingRecipe;
                CancelPendingRecommendation();
                PublishRecommendation(waitingRecipe, waitingCraft, waitingStep,
                    solver.RecoverTimedOutRecommendation(waitingCraft, waitingStep));
            }
            return;
        }

        var recipe = _pendingRecipe;
        var craft = _pendingCraft;
        var step = _pendingStep;
        _pendingRecommendation = null;
        _pendingCraft = null;
        _pendingStep = null;
        _pendingRecommendationCancellation?.Dispose();
        _pendingRecommendationCancellation = null;

        if (pending.IsCanceled || craft == null || step == null || _activeSolver == null)
            return;

        Solver.Recommendation recommendation;
        try
        {
            recommendation = pending.GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            Svc.Log.Error(e, "Asynchronous crafting solver failed");
            SolverFailed?.Invoke(recipe, $"Asynchronous solver failed: {e.Message}");
            return;
        }

        PublishRecommendation(recipe, craft, step, recommendation);
    }

    public static IEnumerable<ISolverDefinition.Desc> GetAvailableSolversForRecipe(CraftState craft, bool returnUnsupported, Type? skipSolver = null)
    {
        foreach (var solver in SolverDefinitions)
        {
            if (solver.GetType() == skipSolver)
                continue;

            foreach (var f in solver.Flavours(craft))
            {
                if (returnUnsupported || f.UnsupportedReason.Length == 0)
                {
                    yield return f;
                }
            }
            yield return default;
        }
    }

    public static ISolverDefinition.Desc? FindSolver(CraftState craft, string type, int flavour)
    {
        var solver = type.Length > 0 ? SolverDefinitions.Find(s => s.GetType().FullName == type) : null;
        if (solver == null)
            return null;

        foreach (var f in solver.Flavours(craft).Where(f => f.Flavour == flavour))
            return f;
        return null;
    }

    public static ISolverDefinition.Desc GetSolverForRecipe(RecipeConfig? recipeConfig, CraftState craft)
    {
        var configuredSolverType = recipeConfig?.CurrentSolverType ?? "";
        var configuredSolverFlavour = recipeConfig?.CurrentSolverFlavour ?? 0;

        var s = FindSolver(craft, configuredSolverType, configuredSolverFlavour);
        if (s != null)
            return s.Value;

        var s2 = GetAvailableSolversForRecipe(craft, false);
        if (s2.Count() > 0)
            return s2.MaxBy(x => x.Priority);

        return default;
    }

    private static void OnCraftStarted(Lumina.Excel.Sheets.Recipe recipe, CraftState craft, StepState initialStep, bool trial)
    {
        Svc.Log.Debug($"[CProc] OnCraftStarted #{recipe.RowId} '{recipe.ItemResult.Value.Name.ToDalamudString()}' (trial={trial}) (cosmic={craft.IsCosmic}) (IQ={craft.InitialQuality}) (PQ={craft.CraftProgress}/{craft.CraftQualityMax})");
        if (_expectedRecipe != null && _expectedRecipe.Value != recipe.RowId)
        {
            Svc.Log.Error($"Unexpected recipe started: expected {_expectedRecipe}, got {recipe.RowId}");
            _activeSolver = null; // something wrong has happened
            ActiveSolver = new("");
        }
        _expectedRecipe = null;
        // we don't want any solvers running with broken gear
        if (RepairManager.GetMinEquippedPercent() == 0)
        {
            SolverFailed?.Invoke(recipe, "You have broken gear");
            _activeSolver = null;
            ActiveSolver = new("");
            return;
        }

        if (_activeSolver == null)
        {
            // if we didn't provide an explicit solver, create one - but make sure if we have manually assigned one, it is actually supported
            var autoSolver = GetSolverForRecipe(P.Config.RecipeConfigs.GetValueOrDefault(recipe.RowId), craft);
            if (autoSolver.UnsupportedReason.Length > 0)
            {
                SolverFailed?.Invoke(recipe, autoSolver.UnsupportedReason);
                return;
            }
            _activeSolver = autoSolver.CreateSolver(craft);
            ActiveSolver = new(autoSolver.Name, _activeSolver);
        }

        if (_activeSolver is ICraftValidator validator)
        {
            Svc.Log.Information("Validation");
            var validation = validator.Validate(craft);
            if (!validation)
            {
                SolverFailed?.Invoke(recipe, "You have mismatched stats");
                _activeSolver = null;
                ActiveSolver = new("");
                return;
            }
        }

        SolverStarted?.Invoke(recipe, ActiveSolver, craft, initialStep);

        RequestRecommendation(recipe, craft, initialStep);
    }

    private static void OnCraftAdvanced(Lumina.Excel.Sheets.Recipe recipe, CraftState craft, StepState step)
    {
        Svc.Log.Debug($"[CProc] OnCraftAdvanced #{recipe.RowId} (solver={ActiveSolver.Name}): {step}");
        if (_activeSolver == null)
            return;
        if (_nextRec.Action != Skills.None && _nextRec.Action != step.PrevComboAction)
            Svc.Log.Warning($"Previous action was different from recommendation: recommended {_nextRec.Action}, used {step.PrevComboAction}");

        RequestRecommendation(recipe, craft, step);
    }

    private static void OnCraftFinished(Lumina.Excel.Sheets.Recipe recipe, CraftState craft, StepState finalStep, bool cancelled)
    {
        Svc.Log.Debug($"[CProc] OnCraftFinished #{recipe.RowId} (cancel={cancelled}, solver={ActiveSolver.Name}): {finalStep}");
        if (_activeSolver == null)
            return;
        if (!cancelled && _nextRec.Action != Skills.None && _nextRec.Action != finalStep.PrevComboAction)
            Svc.Log.Warning($"Previous action was different from recommendation: recommended {_nextRec.Action}, used {finalStep.PrevComboAction}");

        SolverFinished?.Invoke(recipe, ActiveSolver, craft, finalStep);
        CancelPendingRecommendation();
        _activeSolver = null;
        ActiveSolver = new("");
        _nextRec = new();
    }

    private static void RequestRecommendation(Lumina.Excel.Sheets.Recipe recipe, CraftState craft, StepState step)
    {
        CancelPendingRecommendation();
        _nextRec = new();

        if (_activeSolver is IAsyncSolver asyncSolver)
        {
            _pendingRecommendationCancellation = new();
            _pendingRecipe = recipe;
            _pendingCraft = craft with { };
            _pendingStep = step with { };
            _pendingRecommendationStarted = Stopwatch.GetTimestamp();
            _pendingRecommendation = asyncSolver.SolveAsync(
                _pendingCraft,
                _pendingStep,
                _pendingRecommendationCancellation.Token);
            Svc.Log.Debug($"[CProc] Waiting for asynchronous recommendation at step {step.Index}");
            return;
        }

        PublishRecommendation(recipe, craft, step, _activeSolver!.Solve(craft, step));
    }

    private static void PublishRecommendation(Lumina.Excel.Sheets.Recipe recipe, CraftState craft, StepState step, Solver.Recommendation recommendation)
    {
        _nextRec = recommendation;
        Svc.Log.Debug($"Next rec is: {_nextRec.Action}");
        if (Simulator.CannotUseAction(craft, step, _nextRec.Action, out string reason))
        {
            DuoLog.Error($"Unable to use {_nextRec.Action.NameOfAction()}: {reason}");
            return;
        }

        if (_nextRec.Action != Skills.None)
            RecommendationReady?.Invoke(recipe, ActiveSolver, craft, step, _nextRec);
    }

    private static void CancelPendingRecommendation()
    {
        // Observe late faults after detaching a cancelled worker. Its result is
        // never published into a different recipe/step.
        if (_pendingRecommendation is { } abandoned)
            _ = abandoned.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        _pendingRecommendationCancellation?.Cancel();
        _pendingRecommendationCancellation?.Dispose();
        _pendingRecommendationCancellation = null;
        _pendingRecommendation = null;
        _pendingCraft = null;
        _pendingStep = null;
    }
}
