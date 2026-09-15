using Artisan.Autocraft;
using Artisan.CraftingLists;
using Artisan.CraftingLogic;
using Artisan.CraftingLogic.Solvers;
using Artisan.GameInterop;
using Artisan.RawInformation;
using Artisan.RawInformation.Character;
using Dalamud.Game.ClientState.Conditions;
using ECommons;
using ECommons.DalamudServices;
using ECommons.ExcelServices;
using ECommons.Logging;
using FFXIVClientStructs.FFXIV.Client.Game;
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
        private static readonly SemaphoreSlim CraftimizerPredictionGate = new(1, 1);
        private static readonly Dictionary<uint, (string Type, int Flavour)> AllaganSolverRestore = new();
        private static readonly HashSet<uint> AllaganHqIngredientRecipes = new();
        private static bool allaganListObserved;

        private sealed record HqPlanSegment(
            int CraftCount,
            int StartingQuality,
            EnduranceIngredients[] Assignments);

        private sealed record HqIngredientPlan(
            IReadOnlyList<HqPlanSegment> Segments,
            IReadOnlyDictionary<uint, int> RequiredHqTotals,
            string Description)
        {
            public int StartingQuality => Segments.Count == 0 ? 0 : Segments[0].StartingQuality;
        }

        private sealed record PredictionJob(
            CraftState Craft,
            int Amount,
            bool UseAvailableHq,
            bool IncludeRetainers,
            HqIngredientPlan HqPlan,
            CancellationTokenSource Cancellation,
            Task<string> Task);

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
            Svc.PluginInterface.GetIpcProvider<ushort, int, bool, string>("Artisan.StartCraftimizerHqPredictionWithInventory").RegisterFunc(StartCraftimizerHqPredictionWithInventory);
            Svc.PluginInterface.GetIpcProvider<ushort, int, bool, string>("Artisan.GetCraftimizerHqPredictionWithInventory").RegisterFunc(GetCraftimizerHqPredictionWithInventory);
            Svc.PluginInterface.GetIpcProvider<ushort, int, bool, bool, object>("Artisan.PrepareAndCraftWithCraftimizerWithInventory").RegisterAction(PrepareAndCraftWithCraftimizerWithInventory);
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
            Svc.PluginInterface.GetIpcProvider<ushort, int, bool, string>("Artisan.StartCraftimizerHqPredictionWithInventory").UnregisterFunc();
            Svc.PluginInterface.GetIpcProvider<ushort, int, bool, string>("Artisan.GetCraftimizerHqPredictionWithInventory").UnregisterFunc();
            Svc.PluginInterface.GetIpcProvider<ushort, int, bool, bool, object>("Artisan.PrepareAndCraftWithCraftimizerWithInventory").UnregisterAction();
            Svc.PluginInterface.GetIpcProvider<bool>("Artisan.IsBusy").UnregisterFunc();
            Svc.PluginInterface.GetIpcProvider<uint, string, bool, object>("Artisan.ChangeSolver").UnregisterAction();
            Svc.PluginInterface.GetIpcProvider<uint, object>("Artisan.SetTempSolverBackToNormal").UnregisterAction();
            foreach (var prediction in CraftimizerPredictions.Values)
                prediction.Cancellation.Cancel();
            CraftimizerPredictions.Clear();
            AllaganHqIngredientRecipes.Clear();
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

            PrepareAndCraftCore(recipeId, amount, includeSubcrafts, false, null);
        }

        /// <summary>
        /// Starts the Craftimizer 2.11 preflight off the framework thread. Only a
        /// successful deterministic simulation is allowed to create the list.
        /// </summary>
        public static void PrepareAndCraftWithCraftimizer(ushort recipeId, int amount, bool includeSubcrafts)
            => PrepareAndCraftWithCraftimizerCore(recipeId, amount, includeSubcrafts, false, false);

        public static void PrepareAndCraftWithCraftimizerWithInventory(ushort recipeId, int amount,
            bool includeSubcrafts, bool includeRetainers)
            => PrepareAndCraftWithCraftimizerCore(recipeId, amount, includeSubcrafts, true, includeRetainers);

        private static void PrepareAndCraftWithCraftimizerCore(ushort recipeId, int amount,
            bool includeSubcrafts, bool useAvailableHq, bool includeRetainers)
        {
            if (amount <= 0)
                throw new ArgumentOutOfRangeException(nameof(amount));
            if (IsBusy() || RetainerInfo.TM.IsBusy)
                throw new InvalidOperationException("Artisan is currently busy.");

            // The explicit analysis button already performed the expensive
            // bounded solve and cached a plan for this exact craft state. Reuse
            // that successful preflight here; only solve again when no matching
            // validated plan exists (for example after gear/settings changed).
            var prediction = StartCraftimizerPrediction(recipeId, amount, useAvailableHq, includeRetainers, false);
            _ = prediction.ContinueWith(task =>
            {
                if (task.IsCanceled)
                    return;
                var result = task.IsFaulted
                    ? $"BLOCK|Craftimizer 2.11 HQ 預測失敗：{task.Exception?.GetBaseException().Message}"
                    : task.Result;
                // ContinueWith runs on a worker thread. All Dalamud logging,
                // plugin state and crafting-list work must return to the
                // framework thread, including the BLOCK/error path.
                Svc.Framework.RunOnTick(() =>
                {
                    try
                    {
                        if (!result.StartsWith("SAFE|", StringComparison.Ordinal))
                        {
                            var separator = result.IndexOf('|');
                            DuoLog.Error(separator >= 0 ? result[(separator + 1)..] : result);
                            return;
                        }

                        if (IsBusy() || RetainerInfo.TM.IsBusy)
                            throw new InvalidOperationException("Artisan 在預測完成前已開始其他工作，已取消本次製作。");
                        if (!CraftimizerPredictions.TryGetValue(recipeId, out var completedPrediction) ||
                            completedPrediction.Task != task || completedPrediction.Amount != amount ||
                            completedPrediction.UseAvailableHq != useAvailableHq ||
                            completedPrediction.IncludeRetainers != includeRetainers)
                            throw new InvalidOperationException("預演結果已被較新的配方或數量取代，請重新按一次製作。");

                        var solverRecipeCount = PrepareAndCraftCore(recipeId, amount, includeSubcrafts, true,
                            useAvailableHq ? completedPrediction.HqPlan : null);
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

        private static int PrepareAndCraftCore(ushort recipeId, int amount, bool includeSubcrafts,
            bool preferCraftimizer, HqIngredientPlan? hqPlan)
        {
            if (!LuminaSheets.RecipeSheet!.TryGetFirst(x => x.Value.RowId == recipeId, out var recipe))
                throw new InvalidOperationException("RecipeID not found.");

            var list = new NewCraftingList
            {
                Name = $"Allagan Tools - {recipe.Value.ItemResult.Value.Name}",
                // This is an ephemeral list, so NewCraftingList.Save() never
                // applies the user's list defaults. Explicitly inherit the
                // global bulk-crafting maintenance settings instead of leaving
                // repair disabled at the CLR default.
                Repair = P.Config.Repair,
                RepairPercent = Math.Clamp(P.Config.RepairPercent, 1, 100),
                Materia = P.Config.Materia,
            };
            if (includeSubcrafts)
                CraftingListUI.AddAllSubcrafts(recipe.Value, list, 1, amount);
            list.Recipes.Add(new ListItem { ID = recipeId, Quantity = amount });
            CraftingListUI.selectedList = list;

            Svc.Log.Information(
                $"Allagan craft request: recipe={recipeId}, crafts={amount}, yield={recipe.Value.AmountResult}, " +
                $"outputs={amount * recipe.Value.AmountResult}, includeSubcrafts={includeSubcrafts}, " +
                $"retainerIpcReady={RetainerInfo.ATools}, repair={list.Repair}, " +
                $"repairPercent={list.RepairPercent}");

            if (preferCraftimizer)
                ApplyCraftimizerToList(list);

            if (hqPlan != null)
            {
                AllaganHqIngredientRecipes.Add(recipeId);
                Svc.Log.Information(
                    $"[Allagan HQ Materials] Recipe {recipeId}: startingQuality={hqPlan.StartingQuality}, " +
                    $"crafts={amount}, {hqPlan.Description}");
            }

            if (RetainerInfo.ATools)
            {
                RetainerInfo.RestockFromRetainers(list, hqPlan?.RequiredHqTotals);
                RetainerInfo.TM.Enqueue(() => CraftingListUI.StartList(), "StartAllaganCraft");
            }
            else
            {
                CraftingListUI.StartList();
            }

            return list.Recipes.Select(item => item.ID).Distinct().Count();
        }

        internal static bool TryGetAllaganIngredientPlan(uint recipeId, out EnduranceIngredients[] plan)
        {
            plan = Array.Empty<EnduranceIngredients>();
            if (!AllaganHqIngredientRecipes.Contains(recipeId) ||
                !LuminaSheets.RecipeSheet!.TryGetValue(recipeId, out var recipe))
                return false;

            plan = BuildCurrentCharacterHqAssignments(recipe);
            return true;
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
            AllaganHqIngredientRecipes.Clear();
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
                StartCraftimizerPrediction(recipeId, 1, false, false, true);
                return "PENDING|Craftimizer 2.11 正在計算；視配方複雜度可能需要數秒。";
            }
            catch (Exception ex)
            {
                ex.Log();
                return $"BLOCK|無法啟動 Craftimizer 2.11 預測：{ex.Message}";
            }
        }

        public static string GetCraftimizerHqPrediction(ushort recipeId)
            => GetCraftimizerHqPredictionCore(recipeId, 1, false);

        public static string StartCraftimizerHqPredictionWithInventory(ushort recipeId, int amount, bool includeRetainers)
        {
            try
            {
                if (amount <= 0)
                    throw new ArgumentOutOfRangeException(nameof(amount));
                StartCraftimizerPrediction(recipeId, amount, true, includeRetainers, true);
                return "PENDING|Craftimizer 2.11 正在依指定數量與角色／僱員 HQ 庫存計算。";
            }
            catch (Exception ex)
            {
                ex.Log();
                return $"BLOCK|無法啟動 Craftimizer 2.11 HQ 庫存預測：{ex.Message}";
            }
        }

        public static string GetCraftimizerHqPredictionWithInventory(ushort recipeId, int amount, bool includeRetainers)
            => GetCraftimizerHqPredictionCore(recipeId, amount, true, includeRetainers);

        private static string GetCraftimizerHqPredictionCore(ushort recipeId, int amount, bool useAvailableHq,
            bool includeRetainers = false)
        {
            if (!CraftimizerPredictions.TryGetValue(recipeId, out var job))
                return "IDLE|尚未啟動 Craftimizer 2.11 預測。";
            if (job.Amount != amount || job.UseAvailableHq != useAvailableHq ||
                job.IncludeRetainers != includeRetainers)
                return "IDLE|製作數量或 HQ 庫存模式已改變，請重新模擬。";
            if (!job.Task.IsCompleted)
                return "PENDING|Craftimizer 2.11 正在計算；視配方複雜度可能需要數秒。";
            if (job.Task.IsCanceled)
                return "BLOCK|Craftimizer 2.11 預測已取消。";
            if (job.Task.IsFaulted)
                return $"BLOCK|Craftimizer 2.11 預測失敗：{job.Task.Exception?.GetBaseException().Message}";
            return job.Task.Result;
        }

        private static Task<string> StartCraftimizerPrediction(ushort recipeId, int amount,
            bool useAvailableHq, bool includeRetainers, bool replaceExisting)
        {
            if (amount <= 0)
                throw new ArgumentOutOfRangeException(nameof(amount));
            if (!LuminaSheets.RecipeSheet!.TryGetFirst(x => x.Value.RowId == recipeId, out var recipeRow))
                throw new InvalidOperationException("找不到配方。");

            var recipe = recipeRow.Value;
            var isCollectable = recipe.ItemResult.Value.AlwaysCollectable;
            var job = (Job)((uint)Job.CRP + recipe.CraftType.RowId);
            var targetJobName = job.ToString();
            if (!P.Config.RecipeConfigs.TryGetValue(recipe.RowId, out var config))
                config = new RecipeConfig();
            var stats = CharacterStats.GetBaseStatsForClassHeuristic(job);
            var baseCraftsmanship = stats.Craftsmanship;
            var baseControl = stats.Control;
            var baseCp = stats.CP;
            stats.AddConsumables(new(config.RequiredFood, config.RequiredFoodHQ),
                new(config.RequiredPotion, config.RequiredPotionHQ), CharacterInfo.FCCraftsmanshipbuff);
            var craft = Crafting.BuildCraftStateForRecipe(stats, job, recipe);
            var hqPlan = BuildHqIngredientPlan(recipe, amount, useAvailableHq, includeRetainers);
            var collectableMode = P.Config.SolverCollectibleMode switch
            {
                1 => (Name: "最低", Target: craft.CraftQualityMin1),
                2 => (Name: "中間", Target: craft.CraftQualityMin2),
                _ => (Name: "最高", Target: craft.CraftQualityMin3),
            };
            var details = $"Craftimizer 2.11 Next Action；目標職業：{targetJobName}；基礎裝備：作業 {baseCraftsmanship}、加工 {baseControl}、CP {baseCp}；" +
                $"預先套用指定食藥後：作業 {craft.StatCraftsmanship}、加工 {craft.StatControl}、CP {craft.StatCP}；食物：{config.FoodName}；藥水：{config.PotionName}；" +
                $"HQ 材料：{hqPlan.Description}；起始品質：{hqPlan.StartingQuality}/{craft.CraftQualityMax}" +
                (isCollectable ? $"；收藏品目標：{collectableMode.Name}（{collectableMode.Target}）" : string.Empty);
            Svc.Log.Information(
                $"[Craftimizer Preflight] Recipe {recipeId}: targetJob={targetJobName} ({(uint)job}), " +
                $"base={baseCraftsmanship}/{baseControl}/{baseCp}, " +
                $"configuredConsumables={config.FoodName} + {config.PotionName}, " +
                $"simulated={craft.StatCraftsmanship}/{craft.StatControl}/{craft.StatCP}, " +
                $"crafts={amount}, useAvailableHq={useAvailableHq}, startingQuality={hqPlan.StartingQuality}, " +
                $"hqPlan={hqPlan.Description}");

            if (!replaceExisting &&
                CraftimizerPredictions.TryGetValue(recipeId, out var existing) &&
                existing.Craft == craft &&
                existing.Amount == amount && existing.UseAvailableHq == useAvailableHq &&
                existing.IncludeRetainers == includeRetainers &&
                existing.HqPlan.Description == hqPlan.Description &&
                !existing.Task.IsCanceled && !existing.Task.IsFaulted)
            {
                if (!existing.Task.IsCompleted)
                {
                    Svc.Log.Information($"[Craftimizer Preview Cache] Reusing the in-flight analysis for recipe {recipeId}");
                    return existing.Task;
                }

                if (existing.Task.Result.StartsWith("SAFE|", StringComparison.Ordinal))
                {
                    Svc.Log.Information(
                        $"[Craftimizer Preview Cache] Reusing recipe {recipeId} preflight for {amount} crafts " +
                        $"at starting quality {hqPlan.StartingQuality}; no second solve");
                    return existing.Task;
                }
            }

            var solverDesc = CraftingProcessor.GetAvailableSolversForRecipe(craft, false)
                .FirstOrDefault(candidate => candidate.Name == CraftimizerSolverName);
            if (solverDesc == default || !string.IsNullOrEmpty(solverDesc.UnsupportedReason))
                throw new InvalidOperationException(string.IsNullOrEmpty(solverDesc.UnsupportedReason)
                    ? "找不到 Craftimizer 2.11 求解器。"
                    : solverDesc.UnsupportedReason);
            var solver = solverDesc.CreateSolver(craft)
                ?? throw new InvalidOperationException("無法建立 Craftimizer 2.11 求解器。");
            // Bulk quantity only determines a small set of HQ/NQ transition
            // segments. The solver runs once per distinct starting quality,
            // never once per requested craft.
            var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var task = SimulateCraftimizerExecutionAsync(solver, craft, hqPlan, isCollectable,
                collectableMode.Target, collectableMode.Name, details, cancellation.Token);
            ReplacePrediction(recipeId, craft, amount, useAvailableHq, includeRetainers, hqPlan,
                cancellation, task, replaceExisting);
            return task;
        }

        private static void ReplacePrediction(ushort recipeId, CraftState craft, int amount, bool useAvailableHq,
            bool includeRetainers, HqIngredientPlan hqPlan, CancellationTokenSource cancellation,
            Task<string> task, bool replaceExisting)
        {
            var replacement = new PredictionJob(craft with { }, amount, useAvailableHq, includeRetainers,
                hqPlan, cancellation, task);
            if (replaceExisting && CraftimizerPredictions.TryRemove(recipeId, out var previous))
            {
                previous.Cancellation.Cancel();
                _ = previous.Task.ContinueWith(_ => previous.Cancellation.Dispose(), TaskScheduler.Default);
            }
            CraftimizerPredictions[recipeId] = replacement;
        }

        private static unsafe HqIngredientPlan BuildHqIngredientPlan(Lumina.Excel.Sheets.Recipe recipe,
            int amount, bool useAvailableHq, bool includeRetainers)
        {
            var ingredients = recipe.Ingredients().Take(6).ToArray();
            var remainingHq = new Dictionary<uint, int>();
            foreach (var ingredient in ingredients)
            {
                var itemId = ingredient.Item.RowId;
                if (!useAvailableHq || itemId == 0 || ingredient.Amount <= 0 ||
                    !ingredient.Item.CanBeHq || remainingHq.ContainsKey(itemId))
                    continue;

                var characterHq = GetCharacterHqCount(itemId);
                var retainerHq = includeRetainers && RetainerInfo.ATools
                    ? RetainerInfo.GetRetainerItemCount(itemId, true, true)
                    : 0;
                remainingHq[itemId] = Math.Max(0, characterHq + retainerHq);
            }

            var requiredTotals = new Dictionary<uint, int>();
            var segments = new List<HqPlanSegment>();
            for (var craftIndex = 0; craftIndex < amount; craftIndex++)
            {
                var assignments = new EnduranceIngredients[ingredients.Length];
                var hqCounts = new int[ingredients.Length];
                for (var slot = 0; slot < ingredients.Length; slot++)
                {
                    var ingredient = ingredients[slot];
                    var required = Math.Max(0, ingredient.Amount);
                    var itemId = ingredient.Item.RowId;
                    var availableHq = remainingHq.GetValueOrDefault(itemId);
                    var hq = useAvailableHq && ingredient.Item.CanBeHq
                        ? Math.Min(required, availableHq)
                        : 0;
                    hqCounts[slot] = hq;
                    assignments[slot] = new EnduranceIngredients
                    {
                        IngredientSlot = slot,
                        NQSet = required - hq,
                        HQSet = hq,
                    };
                    if (hq <= 0)
                        continue;
                    remainingHq[itemId] = availableHq - hq;
                    requiredTotals[itemId] = requiredTotals.GetValueOrDefault(itemId) + hq;
                }

                var startingQuality = Calculations.GetStartingQuality(recipe, hqCounts);
                if (segments.Count > 0 && SameAssignments(segments[^1].Assignments, assignments))
                    segments[^1] = segments[^1] with { CraftCount = segments[^1].CraftCount + 1 };
                else
                    segments.Add(new HqPlanSegment(1, startingQuality, assignments));
            }

            if (segments.Count == 0)
                segments.Add(new HqPlanSegment(amount, 0, Array.Empty<EnduranceIngredients>()));

            var description = string.Join("；", segments.Select(segment =>
            {
                var hq = string.Join("、", segment.Assignments
                    .Where(x => x.HQSet > 0)
                    .Select(x => $"槽{x.IngredientSlot + 1} HQ×{x.HQSet}"));
                return $"{segment.CraftCount} 次：起始品質 {segment.StartingQuality}" +
                    (hq.Length == 0 ? "（全 NQ）" : $"（{hq}）");
            }));
            return new HqIngredientPlan(segments, requiredTotals, description);
        }

        private static unsafe EnduranceIngredients[] BuildCurrentCharacterHqAssignments(
            Lumina.Excel.Sheets.Recipe recipe)
        {
            var ingredients = recipe.Ingredients().Take(6).ToArray();
            var remainingHq = ingredients.Select(x => x.Item.RowId).Distinct()
                .ToDictionary(itemId => itemId, GetCharacterHqCount);
            var assignments = new EnduranceIngredients[ingredients.Length];
            for (var slot = 0; slot < ingredients.Length; slot++)
            {
                var ingredient = ingredients[slot];
                var required = Math.Max(0, ingredient.Amount);
                var itemId = ingredient.Item.RowId;
                var availableHq = remainingHq.GetValueOrDefault(itemId);
                var hq = ingredient.Item.CanBeHq ? Math.Min(required, availableHq) : 0;
                remainingHq[itemId] = Math.Max(0, availableHq - hq);
                assignments[slot] = new EnduranceIngredients
                {
                    IngredientSlot = slot,
                    NQSet = required - hq,
                    HQSet = hq,
                };
            }
            return assignments;
        }

        private static unsafe int GetCharacterHqCount(uint itemId)
        {
            var inventory = InventoryManager.Instance();
            return inventory == null ? 0 : inventory->GetInventoryItemCount(itemId, true, false, false);
        }

        private static bool SameAssignments(IReadOnlyList<EnduranceIngredients> left,
            IReadOnlyList<EnduranceIngredients> right) =>
            left.Count == right.Count && left.Zip(right).All(pair =>
                pair.First.NQSet == pair.Second.NQSet && pair.First.HQSet == pair.Second.HQSet);

        private static async Task<string> SimulateCraftimizerExecutionAsync(Solver solver, CraftState craft,
            HqIngredientPlan hqPlan, bool isCollectable, int collectableTarget, string collectableName, string details,
            CancellationToken cancellationToken)
        {
            var outcomes = new List<(int StartingQuality, int CraftCount, StepState Step, int ActionCount, bool Guaranteed)>();
            await CraftimizerPredictionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Match Artisan's simulator panel exactly: CraftimizerSolver.Solve
                // intentionally delegates synchronous/offline simulation to its
                // lightweight Artisan fallback. Do not run MCTS here. Each
                // distinct HQ/NQ starting quality is solved once regardless of
                // whether the requested quantity is 1 or 9999.
                foreach (var group in hqPlan.Segments.GroupBy(segment => segment.StartingQuality))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!TryBuildArtisanSimulatorPlan(solver, craft, group.Key, cancellationToken,
                            out var step, out var plan, out var allActionsGuaranteed))
                        return $"BLOCK|Artisan 同步模擬器未能完成起始品質 {group.Key} 的製作；{details}";

                    var craftCount = group.Sum(segment => segment.CraftCount);
                    outcomes.Add((group.Key, craftCount, step, plan.Count, allActionsGuaranteed));
                    Svc.Log.Information(
                        $"[Craftimizer Preflight] Artisan simulator parity: startingQuality={group.Key}, " +
                        $"crafts={craftCount}, actions={plan.Count}, allGuaranteed={allActionsGuaranteed}, " +
                        $"result={step.Progress}/{step.Quality}");
                }
            }
            finally
            {
                CraftimizerPredictionGate.Release();
            }

            var failed = outcomes.FirstOrDefault(outcome =>
                !MeetsPreflightTarget(craft, outcome.Step, isCollectable, collectableTarget));
            if (failed != default)
            {
                if (isCollectable)
                    return $"BLOCK|起始品質 {failed.StartingQuality} 的 {failed.CraftCount} 次收藏品模擬未達標：" +
                        $"預測收藏價值 {failed.Step.Quality}／門檻 {collectableTarget}；{details}";
                if (craft.CraftHQ)
                {
                    var qualityPercent = craft.CraftQualityMax > 0
                        ? Math.Clamp(failed.Step.Quality * 100 / craft.CraftQualityMax, 0, 100)
                        : 0;
                    return $"BLOCK|起始品質 {failed.StartingQuality} 的 {failed.CraftCount} 次無法保證 HQ" +
                        $"（模擬品質 {qualityPercent}%）；{details}";
                }
                return $"BLOCK|起始品質 {failed.StartingQuality} 的固定品質配方未能完成；{details}";
            }

            if (isCollectable)
            {
                var minimum = outcomes.Min(outcome => outcome.Step.Quality);
                return $"SAFE|Artisan／Craftimizer 同步模擬全部 {outcomes.Count} 種 HQ/NQ 配料皆達標：" +
                    $"{collectableName}檔，最低收藏價值 {minimum}（門檻 {collectableTarget}）；{details}";
            }

            if (!craft.CraftHQ)
                return $"SAFE|Artisan／Craftimizer 同步模擬已驗證固定品質配方可完成；{details}";

            return $"SAFE|Artisan／Craftimizer 同步模擬全部 {outcomes.Count} 種 HQ/NQ 配料皆達到 100%；{details}";
        }

        private static bool MeetsPreflightTarget(CraftState craft, StepState step,
            bool isCollectable, int collectableTarget)
        {
            if (isCollectable)
                return step.Progress >= craft.CraftProgress && step.Quality >= collectableTarget;
            if (!craft.CraftHQ)
                return Simulator.Status(craft, step) == Simulator.CraftStatus.SucceededNoQualityReq;
            return Simulator.Status(craft, step) == Simulator.CraftStatus.SucceededMaxQuality;
        }

        private static bool TryBuildArtisanSimulatorPlan(Solver selectedSolver, CraftState craft, int startingQuality,
            CancellationToken cancellationToken, out StepState step, out IReadOnlyList<Skills> plan,
            out bool allActionsGuaranteed)
        {
            var solver = selectedSolver.Clone();
            var actions = new List<Skills>();
            allActionsGuaranteed = true;
            step = Simulator.CreateInitial(craft, startingQuality);
            while (Simulator.Status(craft, step) == Simulator.CraftStatus.InProgress && actions.Count < 64)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var action = solver.Solve(craft, step).Action;
                if (action == Skills.None)
                {
                    plan = Array.Empty<Skills>();
                    return false;
                }

                allActionsGuaranteed &= Simulator.GetSuccessRate(step, action) >= 1.0;

                var (executeResult, next) = Simulator.Execute(craft, step, action, 0, 1);
                if (executeResult == Simulator.ExecuteResult.CantUse)
                {
                    plan = Array.Empty<Skills>();
                    return false;
                }
                actions.Add(action);
                step = next;
            }

            plan = actions;
            return actions.Count > 0 && Simulator.Status(craft, step) != Simulator.CraftStatus.InProgress;
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
