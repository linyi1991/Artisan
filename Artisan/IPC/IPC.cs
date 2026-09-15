using Artisan.Autocraft;
using Artisan.CraftingLists;
using Artisan.CraftingLogic;
using Artisan.GameInterop;
using Artisan.RawInformation;
using Artisan.RawInformation.Character;
using Dalamud.Game.ClientState.Conditions;
using ECommons;
using ECommons.DalamudServices;
using ECommons.ExcelServices;
using ECommons.Logging;
using OtterGui;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Artisan.IPC
{
    internal static class IPC
    {
        private static bool stopCraftingRequest;
        private const string CraftimizerSolverName = "Craftimizer Recipe Solver";
        private static readonly ConcurrentDictionary<ushort, PredictionJob> CraftimizerPredictions = new();
        private static readonly Dictionary<uint, (string Type, int Flavour)> AllaganSolverRestore = new();
        private static bool allaganListObserved;

        private sealed record PredictionJob(CancellationTokenSource Cancellation, Task<string> Task);

        public static bool StopCraftingRequest
        {
            get => stopCraftingRequest;
            set
            {
                if (value)
                {
                    StopCrafting();
                }
                else
                {
                    if (!Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.WaitingForDutyFinder] && !Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BoundByDuty])
                        ResumeCrafting();
                }
                stopCraftingRequest = value;
            }
        }

        public static ArtisanMode CurrentMode;
        internal static void Init()
        {
            Svc.PluginInterface.GetIpcProvider<bool>("Artisan.GetEnduranceStatus").RegisterFunc(GetEnduranceStatus);
            Svc.PluginInterface.GetIpcProvider<bool, object>("Artisan.SetEnduranceStatus").RegisterAction(SetEnduranceStatus);

            Svc.PluginInterface.GetIpcProvider<bool>("Artisan.IsListRunning").RegisterFunc(IsListRunning);
            Svc.PluginInterface.GetIpcProvider<bool>("Artisan.IsListPaused").RegisterFunc(IsListPaused);
            Svc.PluginInterface.GetIpcProvider<bool, object>("Artisan.SetListPause").RegisterAction(SetListPause);

            Svc.PluginInterface.GetIpcProvider<bool>("Artisan.GetStopRequest").RegisterFunc(GetStopRequest);
            Svc.PluginInterface.GetIpcProvider<bool, object>("Artisan.SetStopRequest").RegisterAction(SetStopRequest);

            Svc.PluginInterface.GetIpcProvider<ushort, int, object>("Artisan.CraftItem").RegisterAction(CraftX);
            Svc.PluginInterface.GetIpcProvider<ushort, int, bool, object>("Artisan.PrepareAndCraft").RegisterAction(PrepareAndCraft);
            Svc.PluginInterface.GetIpcProvider<ushort, string>("Artisan.GetHqPrediction").RegisterFunc(GetHqPrediction);
            Svc.PluginInterface.GetIpcProvider<ushort, string>("Artisan.StartCraftimizerHqPrediction").RegisterFunc(StartCraftimizerHqPrediction);
            Svc.PluginInterface.GetIpcProvider<ushort, string>("Artisan.GetCraftimizerHqPrediction").RegisterFunc(GetCraftimizerHqPrediction);
            Svc.PluginInterface.GetIpcProvider<ushort, int, bool, object>("Artisan.PrepareAndCraftWithCraftimizer").RegisterAction(PrepareAndCraftWithCraftimizer);
            Svc.PluginInterface.GetIpcProvider<bool>("Artisan.IsBusy").RegisterFunc(IsBusy);
            Svc.PluginInterface.GetIpcProvider<uint, string, bool, object>("Artisan.ChangeSolver").RegisterAction(ChangeSolver);
            Svc.PluginInterface.GetIpcProvider<uint, object>("Artisan.SetTempSolverBackToNormal").RegisterAction(SetTempSolverBackToNormal);
        }

        internal static void Dispose()
        {
            Svc.PluginInterface.GetIpcProvider<bool>("Artisan.GetEnduranceStatus").UnregisterFunc();
            Svc.PluginInterface.GetIpcProvider<bool, object>("Artisan.SetEnduranceStatus").UnregisterAction();

            Svc.PluginInterface.GetIpcProvider<bool>("Artisan.IsListRunning").UnregisterFunc();
            Svc.PluginInterface.GetIpcProvider<bool>("Artisan.IsListPaused").UnregisterFunc();
            Svc.PluginInterface.GetIpcProvider<bool, object>("Artisan.SetListPause").UnregisterAction();

            Svc.PluginInterface.GetIpcProvider<bool>("Artisan.GetStopRequest").UnregisterFunc();
            Svc.PluginInterface.GetIpcProvider<bool, object>("Artisan.SetStopRequest").UnregisterAction();

            Svc.PluginInterface.GetIpcProvider<ushort, int, object>("Artisan.CraftItem").UnregisterAction();
            Svc.PluginInterface.GetIpcProvider<ushort, int, bool, object>("Artisan.PrepareAndCraft").UnregisterAction();
            Svc.PluginInterface.GetIpcProvider<ushort, string>("Artisan.GetHqPrediction").UnregisterFunc();
            Svc.PluginInterface.GetIpcProvider<ushort, string>("Artisan.StartCraftimizerHqPrediction").UnregisterFunc();
            Svc.PluginInterface.GetIpcProvider<ushort, string>("Artisan.GetCraftimizerHqPrediction").UnregisterFunc();
            Svc.PluginInterface.GetIpcProvider<ushort, int, bool, object>("Artisan.PrepareAndCraftWithCraftimizer").UnregisterAction();
            Svc.PluginInterface.GetIpcProvider<bool>("Artisan.IsBusy").UnregisterFunc();
            Svc.PluginInterface.GetIpcProvider<uint, string, bool, object>("Artisan.ChangeSolver").UnregisterAction();
            Svc.PluginInterface.GetIpcProvider<uint, object>("Artisan.SetTempSolverBackToNormal").UnregisterAction();
            foreach (var prediction in CraftimizerPredictions.Values)
                prediction.Cancellation.Cancel();
            CraftimizerPredictions.Clear();
            RestoreAllaganSolvers();
        }

        static bool GetEnduranceStatus()
        {
            return Endurance.Enable;
        }

        static void SetEnduranceStatus(bool s)
        {
            Endurance.ToggleEndurance(s);
        }

        static bool IsListRunning()
        {
            return CraftingListUI.Processing;
        }

        static bool IsListPaused()
        {
            return CraftingListUI.Processing && CraftingListFunctions.Paused;
        }

        static void SetListPause(bool s)
        {
            if (IsListPaused())
                CraftingListFunctions.Paused = s;
        }

        static bool GetStopRequest()
        {
            return StopCraftingRequest;
        }

        static void SetStopRequest(bool s)
        {
            if (s)
                DuoLog.Information("Artisan has been requested to stop by an external plugin.");
            else
                DuoLog.Information("Artisan has been requested to restart by an external plugin.");

            StopCraftingRequest = s;
        }

        public unsafe static void CraftX(ushort recipeId, int amount)
        {
            if (LuminaSheets.RecipeSheet!.TryGetFirst(x => x.Value.RowId == recipeId, out var recipe))
            {
                PreCrafting.Tasks.Add((() => PreCrafting.TaskSelectRecipe(recipe.Value), TimeSpan.FromMilliseconds(500)));
                P.TM.Enqueue(() => PreCrafting.Tasks.Count == 0);
                P.TM.DelayNext(100);
                P.TM.Enqueue(() =>
                {
                    Endurance.IPCOverride = true;
                    Endurance.RecipeID = recipeId;
                    P.Config.MaxQuantityMode = true;
                    P.Config.CraftX = amount;
                    P.Config.CraftingX = true;
                    Endurance.ToggleEndurance(true);
                });
            }
            else
            {
                throw new Exception("RecipeID not found.");
            }
        }

        public static void ChangeSolver(uint recipeId, string solverName, bool temporary)
        {
            if (!LuminaSheets.RecipeSheet.TryGetValue(recipeId, out var recipe))
                throw new ArgumentException($"Recipe {recipeId} was not found.", nameof(recipeId));

            if (!P.Config.RecipeConfigs.TryGetValue(recipeId, out var config))
                config = new();
            var job = (Job)((uint)Job.CRP + recipe.CraftType.RowId);
            var stats = CharacterStats.GetBaseStatsForClassHeuristic(job);
            var craft = Crafting.BuildCraftStateForRecipe(stats, job, recipe);
            var solver = CraftingProcessor.GetAvailableSolversForRecipe(craft, false)
                .FirstOrDefault(candidate => candidate.Name == solverName);
            if (solver == default)
                throw new ArgumentException($"Solver '{solverName}' is not available for recipe {recipeId}.", nameof(solverName));

            if (temporary)
            {
                config.TempSolverType = solver.Def.GetType().FullName!;
                config.TempSolverFlavour = solver.Flavour;
            }
            else
            {
                config.SolverType = solver.Def.GetType().FullName!;
                config.SolverFlavour = solver.Flavour;
            }

            P.Config.RecipeConfigs[recipeId] = config;
            if (!temporary)
                P.Config.Save();
            Svc.Log.Information($"IPC selected '{solver.Name}' for recipe {recipeId} (temporary={temporary})");
        }

        public static void SetTempSolverBackToNormal(uint recipeId)
        {
            if (!P.Config.RecipeConfigs.TryGetValue(recipeId, out var config))
                return;

            config.TempSolverType = "";
            config.TempSolverFlavour = -1;
            Svc.Log.Information($"IPC restored the configured solver for recipe {recipeId}");
        }

        /// <summary>
        /// User-triggered bridge for the private Allagan Tools build. Creates an ephemeral list,
        /// optionally expands subcrafts, retrieves missing materials through Artisan's existing
        /// retainer workflow, then starts the list (which already handles class changes).
        /// </summary>
        public static void PrepareAndCraft(ushort recipeId, int amount, bool includeSubcrafts)
        {
            if (amount <= 0)
                throw new ArgumentOutOfRangeException(nameof(amount));
            if (IsBusy() || RetainerInfo.TM.IsBusy)
                throw new InvalidOperationException("Artisan is currently busy.");
            if (!LuminaSheets.RecipeSheet!.TryGetFirst(x => x.Value.RowId == recipeId, out var recipe))
                throw new InvalidOperationException("RecipeID not found.");

            var hqPrediction = GetHqPrediction(recipeId);
            if (!hqPrediction.StartsWith("SAFE|", StringComparison.Ordinal))
            {
                var separator = hqPrediction.IndexOf('|');
                throw new InvalidOperationException(separator >= 0 ? hqPrediction[(separator + 1)..] : "無法保證 HQ。");
            }

            PrepareAndCraftCore(recipeId, amount, includeSubcrafts, false);
        }

        /// <summary>
        /// Starts the Craftimizer 2.11 preflight off the framework thread. Only a
        /// successful deterministic simulation is allowed to create the list.
        /// </summary>
        public static void PrepareAndCraftWithCraftimizer(ushort recipeId, int amount, bool includeSubcrafts)
        {
            if (amount <= 0)
                throw new ArgumentOutOfRangeException(nameof(amount));
            if (IsBusy() || RetainerInfo.TM.IsBusy)
                throw new InvalidOperationException("Artisan is currently busy.");

            var prediction = StartCraftimizerPrediction(recipeId, true);
            _ = prediction.ContinueWith(task =>
            {
                if (task.IsCanceled)
                    return;
                var result = task.IsFaulted
                    ? $"BLOCK|Craftimizer 2.11 HQ 預測失敗：{task.Exception?.GetBaseException().Message}"
                    : task.Result;
                if (!result.StartsWith("SAFE|", StringComparison.Ordinal))
                {
                    var separator = result.IndexOf('|');
                    DuoLog.Error(separator >= 0 ? result[(separator + 1)..] : result);
                    return;
                }

                Svc.Framework.RunOnTick(() =>
                {
                    try
                    {
                        if (IsBusy() || RetainerInfo.TM.IsBusy)
                            throw new InvalidOperationException("Artisan 在預測完成前已開始其他工作，已取消本次製作。");
                        var solverRecipeCount = PrepareAndCraftCore(recipeId, amount, includeSubcrafts, true);
                        DuoLog.Information($"Craftimizer 2.11 單次求解通過；已為 {solverRecipeCount} 個不同配方套用求解器，Artisan 將依序重複主配方 {amount} 次。");
                    }
                    catch (Exception ex)
                    {
                        ex.Log();
                        DuoLog.Error($"無法啟動 Craftimizer 製作：{ex.Message}");
                        RestoreAllaganSolvers();
                    }
                });
            }, TaskScheduler.Default);
        }

        private static int PrepareAndCraftCore(ushort recipeId, int amount, bool includeSubcrafts, bool preferCraftimizer)
        {
            if (!LuminaSheets.RecipeSheet!.TryGetFirst(x => x.Value.RowId == recipeId, out var recipe))
                throw new InvalidOperationException("RecipeID not found.");

            var list = new NewCraftingList
            {
                Name = $"Allagan Tools - {recipe.Value.ItemResult.Value.Name}",
            };
            if (includeSubcrafts)
                CraftingListUI.AddAllSubcrafts(recipe.Value, list, 1, amount);
            list.Recipes.Add(new ListItem { ID = recipeId, Quantity = amount });
            CraftingListUI.selectedList = list;

            Svc.Log.Information(
                $"Allagan craft request: recipe={recipeId}, crafts={amount}, yield={recipe.Value.AmountResult}, " +
                $"outputs={amount * recipe.Value.AmountResult}, includeSubcrafts={includeSubcrafts}, " +
                $"retainerIpcReady={RetainerInfo.ATools}");

            if (preferCraftimizer)
                ApplyCraftimizerToList(list);

            if (RetainerInfo.ATools)
            {
                RetainerInfo.RestockFromRetainers(list);
                RetainerInfo.TM.Enqueue(() => CraftingListUI.StartList(), "StartAllaganCraft");
            }
            else
            {
                CraftingListUI.StartList();
            }

            return list.Recipes.Select(item => item.ID).Distinct().Count();
        }

        private static void ApplyCraftimizerToList(NewCraftingList list)
        {
            RestoreAllaganSolvers();
            foreach (var id in list.Recipes.Select(item => item.ID).Distinct())
            {
                if (!P.Config.RecipeConfigs.TryGetValue(id, out var config))
                    config = new RecipeConfig();
                AllaganSolverRestore[id] = (config.TempSolverType, config.TempSolverFlavour);
                ChangeSolver(id, CraftimizerSolverName, true);
            }

            allaganListObserved = false;
            Svc.Framework.Update -= MonitorAllaganCraft;
            Svc.Framework.Update += MonitorAllaganCraft;
        }

        private static void MonitorAllaganCraft(Dalamud.Plugin.Services.IFramework framework)
        {
            if (CraftingListUI.Processing)
            {
                allaganListObserved = true;
                return;
            }

            if (!allaganListObserved && (RetainerInfo.TM.IsBusy || P.TM.NumQueuedTasks > 0))
                return;
            RestoreAllaganSolvers();
        }

        private static void RestoreAllaganSolvers()
        {
            Svc.Framework.Update -= MonitorAllaganCraft;
            foreach (var (recipeId, previous) in AllaganSolverRestore)
            {
                if (!P.Config.RecipeConfigs.TryGetValue(recipeId, out var config))
                    continue;
                config.TempSolverType = previous.Type;
                config.TempSolverFlavour = previous.Flavour;
            }
            AllaganSolverRestore.Clear();
            allaganListObserved = false;
        }

        /// <summary>
        /// Begins a non-blocking Craftimizer 2.11 HQ/collectability preflight.
        /// All Dalamud/game data is captured on the caller's framework thread;
        /// only the pure solver loop runs in the background.
        /// </summary>
        public static string StartCraftimizerHqPrediction(ushort recipeId)
        {
            try
            {
                StartCraftimizerPrediction(recipeId, true);
                return "PENDING|Craftimizer 2.11 正在計算；視配方複雜度可能需要數秒。";
            }
            catch (Exception ex)
            {
                ex.Log();
                return $"BLOCK|無法啟動 Craftimizer 2.11 預測：{ex.Message}";
            }
        }

        public static string GetCraftimizerHqPrediction(ushort recipeId)
        {
            if (!CraftimizerPredictions.TryGetValue(recipeId, out var job))
                return "IDLE|尚未啟動 Craftimizer 2.11 預測。";
            if (!job.Task.IsCompleted)
                return "PENDING|Craftimizer 2.11 正在計算；視配方複雜度可能需要數秒。";
            if (job.Task.IsCanceled)
                return "BLOCK|Craftimizer 2.11 預測已取消。";
            if (job.Task.IsFaulted)
                return $"BLOCK|Craftimizer 2.11 預測失敗：{job.Task.Exception?.GetBaseException().Message}";
            return job.Task.Result;
        }

        private static Task<string> StartCraftimizerPrediction(ushort recipeId, bool replaceExisting)
        {
            if (!LuminaSheets.RecipeSheet!.TryGetFirst(x => x.Value.RowId == recipeId, out var recipeRow))
                throw new InvalidOperationException("找不到配方。");

            var recipe = recipeRow.Value;
            var isCollectable = recipe.ItemResult.Value.AlwaysCollectable;
            if (!isCollectable && (!recipe.CanHq || !recipe.ItemResult.Value.CanBeHq))
            {
                var fixedQuality = Task.FromResult("SAFE|此成品為固定品質，不存在 HQ 版本；實際製作仍使用 Craftimizer 2.11。 ");
                ReplacePrediction(recipeId, new CancellationTokenSource(), fixedQuality, replaceExisting);
                return fixedQuality;
            }

            var job = (Job)((uint)Job.CRP + recipe.CraftType.RowId);
            if (!P.Config.RecipeConfigs.TryGetValue(recipe.RowId, out var config))
                config = new RecipeConfig();
            var stats = CharacterStats.GetBaseStatsForClassHeuristic(job);
            stats.AddConsumables(new(config.RequiredFood, config.RequiredFoodHQ),
                new(config.RequiredPotion, config.RequiredPotionHQ), CharacterInfo.FCCraftsmanshipbuff);
            var craft = Crafting.BuildCraftStateForRecipe(stats, job, recipe);
            var collectableMode = P.Config.SolverCollectibleMode switch
            {
                1 => (Name: "最低", Target: craft.CraftQualityMin1),
                2 => (Name: "中間", Target: craft.CraftQualityMin2),
                _ => (Name: "最高", Target: craft.CraftQualityMin3),
            };
            var details = $"Craftimizer 2.11 Next Action；裝備/食藥後：作業 {craft.StatCraftsmanship}、加工 {craft.StatControl}、CP {craft.StatCP}；食物：{config.FoodName}；藥水：{config.PotionName}" +
                (isCollectable ? $"；收藏品目標：{collectableMode.Name}（{collectableMode.Target}）" : string.Empty);

            var solverDesc = CraftingProcessor.GetAvailableSolversForRecipe(craft, false)
                .FirstOrDefault(candidate => candidate.Name == CraftimizerSolverName);
            if (solverDesc == default || !string.IsNullOrEmpty(solverDesc.UnsupportedReason))
                throw new InvalidOperationException(string.IsNullOrEmpty(solverDesc.UnsupportedReason)
                    ? "找不到 Craftimizer 2.11 求解器。"
                    : solverDesc.UnsupportedReason);
            var solver = solverDesc.CreateSolver(craft)
                ?? throw new InvalidOperationException("無法建立 Craftimizer 2.11 求解器。");
            var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var task = SimulateCraftimizerExecutionAsync(solver, craft, isCollectable,
                collectableMode.Target, collectableMode.Name, details, cancellation.Token);
            ReplacePrediction(recipeId, cancellation, task, replaceExisting);
            return task;
        }

        private static void ReplacePrediction(ushort recipeId, CancellationTokenSource cancellation,
            Task<string> task, bool replaceExisting)
        {
            var replacement = new PredictionJob(cancellation, task);
            if (replaceExisting && CraftimizerPredictions.TryRemove(recipeId, out var previous))
            {
                previous.Cancellation.Cancel();
                _ = previous.Task.ContinueWith(_ => previous.Cancellation.Dispose(), TaskScheduler.Default);
            }
            CraftimizerPredictions[recipeId] = replacement;
        }

        private static async Task<string> SimulateCraftimizerExecutionAsync(Solver solver, CraftState craft,
            bool isCollectable, int collectableTarget, string collectableName, string details,
            CancellationToken cancellationToken)
        {
            var simulationSolver = solver.Clone();
            var step = Simulator.CreateInitial(craft, 0);
            while (Simulator.Status(craft, step) == Simulator.CraftStatus.InProgress)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var recommendation = simulationSolver is IAsyncSolver asyncSolver
                    ? await asyncSolver.SolveAsync(craft, step, cancellationToken).ConfigureAwait(false)
                    : simulationSolver.Solve(craft, step);
                var action = recommendation.Action;
                if (action == Skills.None)
                    return $"BLOCK|Craftimizer 2.11 未能完成製作；{details}";
                if (Simulator.GetSuccessRate(step, action) < 1.0)
                    return $"BLOCK|Craftimizer 2.11 會使用非 100% 成功技能「{action}」，無法保證品質；{details}";

                var (executeResult, next) = Simulator.Execute(craft, step, action, 0, 1);
                if (executeResult == Simulator.ExecuteResult.CantUse)
                    return $"BLOCK|Craftimizer 2.11 建議了目前不能使用的技能「{action}」；{details}";
                step = next;
            }

            if (isCollectable)
            {
                var passed = step.Progress >= craft.CraftProgress && step.Quality >= collectableTarget;
                return passed
                    ? $"SAFE|Craftimizer 2.11 收藏品模擬達標：{collectableName}檔，預測收藏價值 {step.Quality}（門檻 {collectableTarget}）；{details}"
                    : $"BLOCK|Craftimizer 2.11 收藏品模擬未達標：預測收藏價值 {step.Quality}／門檻 {collectableTarget}；{details}";
            }

            var qualityPercent = craft.CraftQualityMax > 0
                ? Math.Clamp(step.Quality * 100 / craft.CraftQualityMax, 0, 100)
                : 0;
            return Simulator.Status(craft, step) == Simulator.CraftStatus.SucceededMaxQuality
                ? $"SAFE|Craftimizer 2.11 保證 HQ（0 初始品質模擬達到 100%）；{details}"
                : $"BLOCK|Craftimizer 2.11 無法保證 HQ（模擬品質 {qualityPercent}%）；{details}";
        }

        /// <summary>
        /// Conservatively simulates the configured solver with the target job's gearset and
        /// configured consumables. Starting quality is intentionally zero: only a 100% quality
        /// result is allowed through the HQ-only bridge.
        /// </summary>
        public static string GetHqPrediction(ushort recipeId)
        {
            try
            {
                if (!LuminaSheets.RecipeSheet!.TryGetFirst(x => x.Value.RowId == recipeId, out var recipeRow))
                    return "BLOCK|找不到配方。";

                var recipe = recipeRow.Value;
                var isCollectable = recipe.ItemResult.Value.AlwaysCollectable;
                if (!isCollectable && (!recipe.CanHq || !recipe.ItemResult.Value.CanBeHq))
                    return "SAFE|此成品為固定品質，不存在 HQ 版本；允許正常製作。";

                var job = (Job)((uint)Job.CRP + recipe.CraftType.RowId);
                if (!P.Config.RecipeConfigs.TryGetValue(recipe.RowId, out var config))
                    config = new RecipeConfig();
                var stats = CharacterStats.GetBaseStatsForClassHeuristic(job);
                stats.AddConsumables(new(config.RequiredFood, config.RequiredFoodHQ),
                    new(config.RequiredPotion, config.RequiredPotionHQ), CharacterInfo.FCCraftsmanshipbuff);
                var craft = Crafting.BuildCraftStateForRecipe(stats, job, recipe);
                var collectableMode = P.Config.SolverCollectibleMode switch
                {
                    1 => (Name: "最低", Target: craft.CraftQualityMin1),
                    2 => (Name: "中間", Target: craft.CraftQualityMin2),
                    _ => (Name: "最高", Target: craft.CraftQualityMin3),
                };
                var simulationDetails = $"裝備/食藥後：作業 {craft.StatCraftsmanship}、加工 {craft.StatControl}、CP {craft.StatCP}；食物：{config.FoodName}；藥水：{config.PotionName}" +
                    (isCollectable ? $"；收藏品目標：{collectableMode.Name}（{collectableMode.Target}）" : string.Empty);
                var solverDesc = CraftingProcessor.GetSolverForRecipe(config, craft);
                if (!string.IsNullOrEmpty(solverDesc.UnsupportedReason))
                    return $"BLOCK|目前求解器不支援：{solverDesc.UnsupportedReason}；{simulationDetails}";
                var solver = solverDesc.CreateSolver(craft);
                if (solver == null)
                    return "BLOCK|找不到可用的 Artisan 求解器。";

                var result = SimulateGuaranteedExecution(solver, craft, out var unsafeAction);
                if (result == null)
                    return unsafeAction != null
                        ? $"BLOCK|求解器會使用非 100% 成功技能「{unsafeAction}」，無法保證 HQ；{simulationDetails}"
                        : $"BLOCK|Artisan 模擬未能完成製作；{simulationDetails}";
                var status = Simulator.Status(craft, result);
                if (isCollectable)
                {
                    var completed = result.Progress >= craft.CraftProgress;
                    var reachedTarget = result.Quality >= collectableMode.Target;
                    return completed && reachedTarget
                        ? $"SAFE|收藏品模擬達標：{collectableMode.Name}檔，預測收藏價值 {result.Quality}（門檻 {collectableMode.Target}）；{simulationDetails}"
                        : $"BLOCK|收藏品模擬未達標：預測收藏價值 {result.Quality}／門檻 {collectableMode.Target}，已取消操作；{simulationDetails}";
                }
                var qualityPercent = craft.CraftQualityMax > 0
                    ? Math.Clamp(result.Quality * 100 / craft.CraftQualityMax, 0, 100)
                    : 0;
                return status == Simulator.CraftStatus.SucceededMaxQuality
                    ? $"SAFE|保證 HQ（0 初始品質模擬達到 100%）；{simulationDetails}"
                    : $"BLOCK|無法保證 HQ（模擬品質 {qualityPercent}%），已取消操作；{simulationDetails}";
            }
            catch (Exception ex)
            {
                ex.Log();
                return $"BLOCK|HQ 模擬失敗：{ex.Message}";
            }
        }

        private static StepState? SimulateGuaranteedExecution(Solver solver, CraftState craft, out string? unsafeAction)
        {
            unsafeAction = null;
            var simulationSolver = solver.Clone();
            var step = Simulator.CreateInitial(craft, 0);
            while (Simulator.Status(craft, step) == Simulator.CraftStatus.InProgress)
            {
                var action = simulationSolver.Solve(craft, step).Action;
                if (action == Skills.None)
                    return null;
                if (Simulator.GetSuccessRate(step, action) < 1.0)
                {
                    unsafeAction = action.ToString();
                    return null;
                }

                var (executeResult, next) = Simulator.Execute(craft, step, action, 0, 1);
                if (executeResult == Simulator.ExecuteResult.CantUse)
                    return null;
                step = next;
            }
            return step;
        }

        public static bool IsBusy()
        {
            return Endurance.Enable || CraftingListUI.Processing || P.TM.NumQueuedTasks > 0 || P.CTM.NumQueuedTasks > 0 || !(Crafting.CurState is Crafting.State.IdleBetween or Crafting.State.IdleNormal);
        }

        public enum ArtisanMode
        {
            None = 0,
            Endurance = 1,
            Lists = 2,
        }
    }
}
