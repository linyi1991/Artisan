using Artisan.GameInterop;
using Artisan.RawInformation;
using Artisan.UI;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using ECommons;
using ECommons.DalamudServices;
using ECommons.ExcelServices;
using ECommons.ImGuiMethods;
using ECommons.Logging;
using Dalamud.Bindings.ImGui;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Artisan.CraftingLogic.Solvers
{
    public class RaphaelSolverDefintion : ISolverDefinition
    {
        public Solver Create(CraftState craft, int flavour)
        {
            if (RaphaelCache.HasSolution(craft, out var output))
            {
                return new MacroSolver(output!, craft);
            }
            return craft.CraftExpert ? new ExpertSolver() : new StandardSolver(false);
        }

        public IEnumerable<ISolverDefinition.Desc> Flavours(CraftState craft)
        {
            if (craft.IsCosmic || RaphaelCache.IsCosmicContextOpen())
                yield break;

            if (RaphaelCache.HasSolution(craft, out var solution))
                yield return new(this, 3, 0, $"Raphael Recipe Solver");
        }
    }

    internal static class RaphaelCache
    {
        internal static readonly ConcurrentDictionary<string, Tuple<CancellationTokenSource, Task>> Tasks = [];
        private static readonly ConcurrentDictionary<uint, DateTimeOffset> CosmicSkipLogTimes = [];
        private static readonly RaphaelRetryGuard RetryGuard = new();
        [NonSerialized]
        public static Dictionary<string, RaphaelSolutionConfig> TempConfigs = new();

        public static void Build(CraftState craft, RaphaelSolutionConfig config, bool automatic = false)
        {
            if (craft.IsCosmic || IsCosmicContextOpen())
            {
                var now = DateTimeOffset.UtcNow;
                if (!CosmicSkipLogTimes.TryGetValue(craft.RecipeId, out var lastLog) ||
                    now - lastLog >= TimeSpan.FromSeconds(30))
                {
                    CosmicSkipLogTimes[craft.RecipeId] = now;
                    Svc.Log.Information("Skipping Raphael generation for cosmic recipe {RecipeId}.", craft.RecipeId);
                }

                return;
            }

            var key = GetKey(craft);
            var attemptSettings = new RaphaelAttemptSettings(craft.RecipeId, craft.StatLevel, craft.UnlockedManipulation,
                config.EnsureReliability, config.BackloadProgress, config.HeartAndSoul, config.QuickInno,
                P.Config.RaphaelSolverConfig.MaximumThreads, P.Config.RaphaelSolverConfig.TimeOutMins);
            if (RetryGuard.ShouldSkip(key, attemptSettings, automatic))
                return;

            if (CLIExists() && !Tasks.ContainsKey(key))
            {
                P.Config.RaphaelSolverCacheV3.TryRemove(key, out _);

                RetryGuard.RecordAttempt(key, attemptSettings);
                Svc.Log.Information("Spawning Raphael process");

                var manipulation = craft.UnlockedManipulation ? "--manipulation" : "";
                var itemText = $"--recipe-id {craft.RecipeId}";
                var extraArgsBuilder = new StringBuilder();

                extraArgsBuilder.Append($"--initial {craft.InitialQuality} "); // must always have a space after

                if (config.EnsureReliability)
                {
                    Svc.Log.Error("Ensuring reliability is enabled, this may take a while. NO SUPPORT GIVEN IF ENABLED.");
                    extraArgsBuilder.Append($"--adversarial "); // must always have a space after
                }

                if (config.BackloadProgress)
                {
                    extraArgsBuilder.Append($"--backload-progress "); // must always have a space after
                }

                if (config.HeartAndSoul)
                {
                    extraArgsBuilder.Append($"--heart-and-soul "); // must always have a space after
                }

                if (config.QuickInno)
                {
                    extraArgsBuilder.Append($"--quick-innovation "); // must always have a space after
                }

                if (P.Config.RaphaelSolverConfig.MaximumThreads > 0)
                {
                    extraArgsBuilder.Append($"--threads {P.Config.RaphaelSolverConfig.MaximumThreads} "); // must always have a space after
                }

                var process = new Process()
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = Path.Join(Path.GetDirectoryName(Svc.PluginInterface.AssemblyLocation.FullName), "raphael-cli.exe"),
                        Arguments = $"solve {itemText} {manipulation} --level {craft.StatLevel} --stats {craft.StatCraftsmanship} {craft.StatControl} {craft.StatCP} {extraArgsBuilder} --output-variables action_ids", // Command to execute
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                Svc.Log.Information(process.StartInfo.Arguments);

                var cts = new CancellationTokenSource();
                var cancellationRegistration = cts.Token.Register(() =>
                {
                    try
                    {
                        if (!process.HasExited)
                            process.Kill();
                    }
                    catch (InvalidOperationException)
                    {
                        // Process did not start or already exited.
                    }
                    catch (Exception ex)
                    {
                        Svc.Log.Warning(ex, "Unable to stop Raphael process for {Key}", key);
                    }
                });
                cts.CancelAfter(TimeSpan.FromMinutes(P.Config.RaphaelSolverConfig.TimeOutMins));

                var registered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var task = Task.Run(async () =>
                {
                    await registered.Task.ConfigureAwait(false);
                    try
                    {
                        if (!process.Start())
                            throw new InvalidOperationException("Raphael process could not be started.");

                        var outputTask = process.StandardOutput.ReadToEndAsync(cts.Token);
                        var errorTask = process.StandardError.ReadToEndAsync(cts.Token);
                        await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                        var output = await outputTask.ConfigureAwait(false);
                        var error = (await errorTask.ConfigureAwait(false)).Trim();

                        if (process.ExitCode != 0)
                        {
                            var message = error.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
                                ?? $"Raphael exited with code {process.ExitCode}.";
                            DuoLog.Error(message);
                            return;
                        }

                        cts.Token.ThrowIfCancellationRequested();
                        var actionIds = output.Replace("[", "").Replace("]", "").Replace("\"", "")
                            .Split(", ", StringSplitOptions.RemoveEmptyEntries)
                            .Select(x => int.TryParse(x, out var id) ? id : 0)
                            .Where(id => id > 0)
                            .ToArray();
                        var steps = MacroUI.ParseMacro(actionIds);
                        if (steps.Count == 0)
                        {
                            Svc.Log.Error("Raphael did not return a valid macro for {Key}.", key);
                            if (P.Config.RaphaelSolverConfig.AutoGenerate)
                            {
                                P.Config.RaphaelSolverConfig.AutoGenerate = false;
                                P.Config.Save();
                            }
                            return;
                        }

                        var id = Random.Shared.Next(50001, 10000000);
                        while (P.Config.RaphaelSolverCacheV3.Any(kv => kv.Value.ID == id))
                            id = Random.Shared.Next(50001, 10000000);

                        P.Config.RaphaelSolverCacheV3[key] = new MacroSolverSettings.Macro()
                        {
                            ID = id,
                            Name = key,
                            Steps = steps,
                            Options = new()
                            {
                                SkipQualityIfMet = false,
                                UpgradeProgressActions = false,
                                UpgradeQualityActions = false,
                                MinCP = craft.StatCP,
                                MinControl = craft.StatControl,
                                MinCraftsmanship = craft.StatCraftsmanship,
                            }
                        };

                        if (P.Config.RaphaelSolverConfig.AutoSwitch)
                        {
                            var opt = CraftingProcessor.GetAvailableSolversForRecipe(craft, true).FirstOrNull(x => x.Name == $"Raphael Recipe Solver");
                            if (opt is not null)
                            {
                                var config = P.Config.RecipeConfigs.GetValueOrDefault(craft.Recipe.RowId) ?? new();
                                config.SolverType = opt.Value.Def.GetType().FullName!;
                                config.SolverFlavour = opt.Value.Flavour;
                                if (!P.Config.RaphaelSolverConfig.AutoSwitchOnAll)
                                {
                                    P.Config.RecipeConfigs[craft.Recipe.RowId] = config;
                                }
                                else
                                {
                                    foreach (var validCraft in AllValidCrafts(key, craft.Recipe.CraftType.RowId))
                                        P.Config.RecipeConfigs[validCraft.Recipe.RowId] = config;
                                }
                            }
                        }

                        P.Config.Save();
                        RetryGuard.RecordSuccess(key);
                    }
                    catch (OperationCanceledException)
                    {
                        Svc.Log.Information("Raphael generation cancelled or timed out for {Key}.", key);
                    }
                    catch (Exception ex)
                    {
                        Svc.Log.Error(ex, "Raphael generation failed for {Key}.", key);
                        DuoLog.Error("Raphael 求解失敗，請查看 Dalamud 日誌取得詳細資訊。");
                    }
                    finally
                    {
                        Tasks.TryRemove(key, out _);
                        cancellationRegistration.Dispose();
                        process.Dispose();
                        cts.Dispose();
                    }
                });

                Tasks[key] = new(cts, task);
                registered.SetResult(true);
            }
        }

        internal static bool IsCosmicContextOpen() =>
            Svc.GameGui.GetAddonByName("WKSHud") != nint.Zero ||
            Svc.GameGui.GetAddonByName("WKSMission") != nint.Zero ||
            Svc.GameGui.GetAddonByName("WKSMissionInfomation") != nint.Zero ||
            Svc.GameGui.GetAddonByName("WKSRecipeNotebook") != nint.Zero;

        public static string GetKey(CraftState craft)
        {
            return $"{craft.CraftLevel}/{craft.CraftProgress}/{craft.CraftQualityMax}/{craft.CraftDurability}-{craft.StatCraftsmanship}/{craft.StatControl}/{craft.StatCP}-{(craft.CraftExpert ? "Expert" : "Standard")}/{craft.InitialQuality}";
        }

        public static IEnumerable<CraftState> AllValidCrafts(string key, uint craftType)
        {
            var stats = KeyParts(key);
            var recipes = LuminaSheets.RecipeSheet.Values.Where(x => x.CraftType.RowId == craftType && x.RecipeLevelTable.Value.ClassJobLevel == stats.Level);
            foreach (var recipe in recipes)
            {
                var state = Crafting.BuildCraftStateForRecipe(default, (Job)((uint)Job.CRP + recipe.CraftType.RowId), recipe);
                if (stats.Prog == state.CraftProgress &&
                    stats.Qual == state.CraftQualityMax &&
                    stats.Dur == state.CraftDurability)
                    yield return state;
            }
        }

        public static (int Level, int Prog, int Qual, int Dur, int Initial, int Crafts, int Control, int CP) KeyParts(string key)
        {
            var parts = key.Split('/');

            int.TryParse(parts[0], out var lvl);
            int.TryParse(parts[1], out var prog);
            int.TryParse(parts[2], out var qual);
            int.TryParse(parts[3].Split('-')[0], out var dur);
            int.TryParse(parts[3].Split('-')[1], out var crafts);
            int.TryParse(parts[4], out var ctrl);
            int.TryParse(parts[5].Split('-')[0], out var cp);
            int.TryParse(parts[6], out var initial);

            return (lvl, prog, qual, dur, initial, crafts, ctrl, cp);
        }

        public static bool HasSolution(CraftState craft, out MacroSolverSettings.Macro? raphaelSolutionConfig)
        {
            if (craft.IsCosmic)
            {
                raphaelSolutionConfig = null;
                return false;
            }

            foreach (var solution in P.Config.RaphaelSolverCacheV3.OrderByDescending(x => KeyParts(x.Key).Control))
            {
                if (solution.Value.Steps.Count == 0) continue;

                var solKey = KeyParts(solution.Key);

                if (solKey.Level == craft.CraftLevel &&
                    solKey.Prog == craft.CraftProgress &&
                    solKey.Qual == craft.CraftQualityMax &&
                    solKey.Crafts == craft.StatCraftsmanship &&
                    solKey.Control <= craft.StatControl &&
                    solKey.Initial == craft.InitialQuality &&
                    solKey.CP <= craft.StatCP)
                {
                    raphaelSolutionConfig = solution.Value;
                    return true;
                }
            }
            raphaelSolutionConfig = null;
            return false;
        }

        public static bool InProgress(CraftState craft) => Tasks.TryGetValue(GetKey(craft), out var _);

        public static bool InProgressAny() => Tasks.Any();

        internal static bool CLIExists()
        {
            return File.Exists(Path.Join(Path.GetDirectoryName(Svc.PluginInterface.AssemblyLocation.FullName), "raphael-cli.exe"));
        }

        public static bool DrawRaphaelDropdown(CraftState craft, bool liveStats = true)
        {
            bool changed = false;
            if (craft.IsCosmic)
                return changed;

            var config = P.Config.RecipeConfigs.GetValueOrDefault(craft.RecipeId) ?? new();
            if (CLIExists())
            {
                var hasSolution = HasSolution(craft, out var solution);
                var key = GetKey(craft);

                if (!TempConfigs.ContainsKey(key))
                {
                    TempConfigs.Add(key, new());
                    TempConfigs[key].EnsureReliability = P.Config.RaphaelSolverConfig.AllowEnsureReliability;
                    TempConfigs[key].BackloadProgress = P.Config.RaphaelSolverConfig.AllowBackloadProgress;
                    TempConfigs[key].HeartAndSoul = P.Config.RaphaelSolverConfig.ShowSpecialistSettings && craft.Specialist;
                    TempConfigs[key].QuickInno = P.Config.RaphaelSolverConfig.ShowSpecialistSettings && craft.Specialist;
                }

                if (hasSolution)
                {
                    var opt = CraftingProcessor.GetAvailableSolversForRecipe(craft, true).FirstOrNull(x => x.Name == $"Raphael Recipe Solver");
                    var solverIsRaph = config.SolverType == opt?.Def.GetType().FullName!;
                    var curStats = CharacterStats.GetCurrentStats();
                    //Svc.Log.Debug($"{curStats.Craftsmanship}/{craft.StatCraftsmanship} - {curStats.Control}/{craft.StatControl} - {curStats.CP}/{craft.StatCP}");
                    if (liveStats && craft.StatCraftsmanship != curStats.Craftsmanship && solverIsRaph)
                    {
                        var craftsmanshipError = curStats.Craftsmanship - craft.StatCraftsmanship > 0 ? $"（高出 {curStats.Craftsmanship - craft.StatCraftsmanship}）" : "";
                        ImGuiEx.Text(ImGuiColors.DalamudRed, $"目前作業精度 {craftsmanshipError}與產生方案時不一致。\n為避免進度提早完成，數值一致前不會使用此方案。\n請確認食物、藥水、部隊增益與裝備狀態和產生方案時相同。");
                    }

                    if (!solverIsRaph)
                    {
                        if (liveStats)
                        {
                            ImGuiEx.TextCentered($"Raphael 製作方案已產生（點擊切換）");
                            if (ImGui.IsItemClicked())
                            {
                                config.SolverType = opt?.Def.GetType().FullName!;
                                config.SolverFlavour = (int)(opt?.Flavour);
                                changed = true;
                            }
                        }
                        else
                        {
                            ImGuiEx.TextCentered($"Raphael Solution Has Been Generated.");
                        }
                    }
                }
                else
                {
                    if (liveStats && P.Config.RaphaelSolverConfig.AutoGenerate && CraftingProcessor.GetAvailableSolversForRecipe(craft, true).Any())
                    {
                        if (!craft.CraftExpert || (craft.CraftExpert && P.Config.RaphaelSolverConfig.GenerateOnExperts))
                            Build(craft, TempConfigs[key], automatic: true);
                    }
                }

                ImGui.Separator();
                var inProgress = InProgress(craft);
                var raphChanges = false;

                if (inProgress)
                    ImGui.BeginDisabled();

                if (P.Config.RaphaelSolverConfig.AllowEnsureReliability)
                    raphChanges |= ImGui.Checkbox($"Ensure reliability##{key}Reliability", ref TempConfigs[key].EnsureReliability);
                if (P.Config.RaphaelSolverConfig.AllowBackloadProgress)
                    raphChanges |= ImGui.Checkbox($"回填進度##{key}Progress", ref TempConfigs[key].BackloadProgress);
                if (P.Config.RaphaelSolverConfig.ShowSpecialistSettings && craft.Specialist)
                    raphChanges |= ImGui.Checkbox($"Allow heart and soul usage##{key}HS", ref TempConfigs[key].HeartAndSoul);
                if (P.Config.RaphaelSolverConfig.ShowSpecialistSettings && craft.Specialist)
                    raphChanges |= ImGui.Checkbox($"Allow quick innovation usage##{key}QI", ref TempConfigs[key].QuickInno);

                changed |= raphChanges;

                if (inProgress)
                    ImGui.EndDisabled();

                if (!inProgress)
                {
                    if (ImGui.Button("建立 Raphael 製作方案", new Vector2(ImGui.GetContentRegionAvail().X, 25f.Scale())))
                    {
                        Build(craft, TempConfigs[key]);
                    }
                }
                else
                {
                    if (ImGui.Button("取消 Raphael 運算", new Vector2(ImGui.GetContentRegionAvail().X, 25f.Scale())))
                    {
                        if (Tasks.TryGetValue(key, out var task))
                        {
                            try { task.Item1.Cancel(); }
                            catch (ObjectDisposedException) { /* Completed concurrently. */ }
                        }
                    }
                }

                if (TempConfigs[key].EnsureReliability && ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    ImGui.Text("Ensuring quality is enabled, no support shall be provided when its enabled\nDue to problems that can be caused.");
                    ImGui.EndTooltip();
                }

                if (TempConfigs[key].HeartAndSoul || TempConfigs[key].QuickInno)
                {
                    ImGui.Text("Specialist actions are enabled, this can slow down the solver a lot.");
                }

                if (inProgress)
                {
                    ImGuiEx.TextCentered("Generating...");
                }
            }

            return changed;
        }
    }

    public class RaphaelSolverSettings
    {
        public bool AllowEnsureReliability = false;
        public bool AllowBackloadProgress = true;
        public bool ShowSpecialistSettings = false;
        public bool ExactCraftsmanship = false;
        public bool AutoGenerate = true;
        public bool AutoSwitch = true;
        public bool AutoSwitchOnAll = false;
        public int MaximumThreads = 1;
        public bool GenerateOnExperts = false;
        public int TimeOutMins = 3;

        public bool Draw()
        {
            bool changed = false;

            ImGui.Indent();
            ImGui.TextWrapped("Raphael 設定會影響運算效能與記憶體用量。若可用記憶體較少，建議維持預設值；至少保留 2 GB 可用記憶體。");

            if (ImGui.SliderInt("最大執行緒數", ref MaximumThreads, 0, Environment.ProcessorCount))
            {
                P.Config.Save();
            }
            ImGuiEx.TextWrapped("預設會使用所有可用處理能力。效能較低的電腦可減少執行緒以降低 CPU 負擔，但產生解法會變慢。(0 代表使用全部)");

            changed |= ImGui.Checkbox("產生巨集時嘗試確保 100% 可靠", ref AllowEnsureReliability);
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(new System.Numerics.Vector4(255, 0, 0, 1), "可靠性模式不一定能成功，而且會大量使用 CPU 與記憶體。建議至少保留 16 GB 可用記憶體；啟用此功能時不提供相關支援。");
            ImGui.PopTextWrapPos();
            changed |= ImGui.Checkbox("產生巨集時允許延後完成進展", ref AllowBackloadProgress);
            changed |= ImGui.Checkbox("可用時顯示專家技能選項", ref ShowSpecialistSettings);
            changed |= ImGui.Checkbox("尚無有效解法時自動產生解法", ref AutoGenerate);

            if (AutoGenerate)
            {
                ImGui.Indent();
                changed |= ImGui.Checkbox("也為專家配方產生解法", ref GenerateOnExperts);
                ImGui.Unindent();
            }

            changed |= ImGui.Checkbox("解法產生完成後自動切換至 Raphael 求解器", ref AutoSwitch);

            if (AutoSwitch)
            {
                ImGui.Indent();
                changed |= ImGui.Checkbox("套用至所有有效配方", ref AutoSwitchOnAll);
                ImGui.Unindent();
            }

            changed |= ImGui.SliderInt("產生解法的逾時時間（分鐘）", ref TimeOutMins, 1, 15);

            ImGuiComponents.HelpMarker("若產生解法超過指定分鐘數，將取消該產生工作。");

            if (ImGui.Button($"清除 Raphael 巨集快取（目前儲存 {P.Config.RaphaelSolverCacheV3.Count} 筆）"))
            {
                P.Config.RaphaelSolverCacheV3.Clear();
                changed |= true;
            }

            ImGui.Unindent();
            return changed;
        }
    }

    public class RaphaelSolutionConfig
    {
        public bool EnsureReliability = false;
        public bool BackloadProgress = false;
        public bool HeartAndSoul = false;
        public bool QuickInno = false;
        public string Macro = string.Empty;

        public int MinCP = 0;
        public int MinControl = 0;
        public int ExactCraftsmanship = 0;

        [NonSerialized]
        public bool HasChanges = false;
    }
}
