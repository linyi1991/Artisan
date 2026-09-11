using Artisan.Autocraft;
using Artisan.CraftingLists;
using Artisan.CraftingLogic;
using Artisan.CraftingLogic.Solvers;
using Artisan.GameInterop;
using Artisan.RawInformation;
using Artisan.RawInformation.Character;
using Dalamud.Game.Gui.Toast;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using ECommons.DalamudServices;
using ECommons.ImGuiMethods;
using ECommons.Logging;
using Dalamud.Bindings.ImGui;
using System;

namespace Artisan.UI
{
    internal class CraftingWindow : Window, IDisposable
    {
        private static readonly TimeSpan AutoActionWatchdogDelay = TimeSpan.FromSeconds(2);

        public bool RepeatTrial;
        private DateTime _estimatedCraftEnd;
        private DateTime _lastAutoActionQueuedAt;
        private Skills _lastAutoAction = Skills.None;
        private int _lastAutoActionStepIndex;
        private bool _autoActionRetryLogged;

        public CraftingWindow() : base("Artisan Crafting Window###MainCraftWindow", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse)
        {
            IsOpen = true;
            ShowCloseButton = false;
            RespectCloseHotkey = false;
            this.SizeConstraints = new()
            {
                MinimumSize = new System.Numerics.Vector2(150f, 0f),
                MaximumSize = new System.Numerics.Vector2(310f, 500f)
            };

            CraftingProcessor.SolverStarted += OnSolverStarted;
            CraftingProcessor.SolverFailed += OnSolverFailed;
            CraftingProcessor.SolverFinished += OnSolverFinished;
            CraftingProcessor.RecommendationReady += OnRecommendationReady;

            this.TitleBarButtons.Add(new()
            {
                Icon = FontAwesomeIcon.Cog,
                ShowTooltip = () => ImGuiEx.SetTooltip("開啟設定"),
                Click = (x) => P.PluginUi.IsOpen = true,
            });
        }

        public void Dispose()
        {
            CraftingProcessor.SolverStarted -= OnSolverStarted;
            CraftingProcessor.SolverFailed -= OnSolverFailed;
            CraftingProcessor.SolverFinished -= OnSolverFinished;
            CraftingProcessor.RecommendationReady -= OnRecommendationReady;
        }

        public override bool DrawConditions()
        {
            return P.PluginUi.CraftingVisible;
        }

        public void Tick()
        {
            RetryStaleAutoAction();
        }

        public override void PreDraw()
        {
            if (!P.Config.DisableTheme)
            {
                P.Style.Push();
                P.StylePushed = true;
            }
        }

        public override void PostDraw()
        {
            if (P.StylePushed)
            {
                P.Style.Pop();
                P.StylePushed = false;
            }
        }

        public override void Draw()
        {
            if (!P.Config.DisableHighlightedAction)
                Hotbars.MakeButtonsGlow(CraftingProcessor.NextRec.Action);

            if (Crafting.CurCraft != null && !Crafting.CurCraft.CraftExpert && Crafting.CurRecipe?.SecretRecipeBook.RowId > 0 && Crafting.CurCraft?.CraftLevel == Crafting.CurCraft?.StatLevel && !CraftingProcessor.ActiveSolver.IsType<MacroSolver>())
            {
                ImGui.Dummy(new System.Numerics.Vector2(12f));
                ImGuiEx.TextWrapped(ImGuiColors.DalamudYellow, "這是目前等級的秘籍配方，成功率可能受隨機狀態影響；建議先用製作練習測試，或使用已驗證的 Artisan 宏。");
            }

            bool autoMode = P.Config.AutoMode;
            if (ImGui.Checkbox("自動執行技能", ref autoMode))
            {
                P.Config.AutoMode = autoMode;
                P.Config.Save();
            }

            if (autoMode && !P.Config.ReplicateMacroDelay)
            {
                var delay = P.Config.AutoDelay;
                ImGui.PushItemWidth(200);
                if (ImGui.SliderInt("執行延遲 (毫秒)", ref delay, 0, 1000))
                {
                    if (delay < 0) delay = 0;
                    if (delay > 1000) delay = 1000;

                    P.Config.AutoDelay = delay;
                    P.Config.Save();
                }
            }

            if (Endurance.RecipeID != 0 && !CraftingListUI.Processing && Endurance.Enable)
            {
                if (ImGui.Button("停止連續製作"))
                {
                    Endurance.ToggleEndurance(false);
                    P.TM.Abort();
                    CraftingListFunctions.CLTM.Abort();
                    PreCrafting.Tasks.Clear();
                }
            }

            if (!Endurance.Enable && Crafting.IsTrial)
                ImGui.Checkbox("重複製作練習", ref RepeatTrial);

            if (CraftingProcessor.ActiveSolver)
            {
                var text = $"正在使用：{CraftingProcessor.ActiveSolver.Name}";
                if (!string.IsNullOrEmpty(CraftingProcessor.NextRec.Comment))
                    text += $" ({CraftingProcessor.NextRec.Comment})";
                ImGuiEx.TextWrapped(text.Replace("%", ""));
            }

            if (P.Config.CraftingX && Endurance.Enable)
                ImGui.Text($"剩餘製作次數：{P.Config.CraftX}");

            if (_estimatedCraftEnd != default)
            {
                var diff = _estimatedCraftEnd - DateTime.Now;
                if (diff < TimeSpan.Zero)
                    ImGui.Text($"預計剩餘時間：超過預估 {FormatDuration(-diff)}");
                else
                    ImGui.Text($"預計剩餘時間：{FormatDuration(diff)}");
            }

            if (!P.Config.AutoMode)
            {
                ImGui.Text("半手動模式");

                var action = CraftingProcessor.NextRec.Action;
                using var disable = ImRaii.Disabled(action == Skills.None);

                if (ImGui.Button("執行推薦技能"))
                {
                    ActionManagerEx.UseSkill(action);
                }
                if (ImGui.Button("重新取得推薦"))
                {
                    ShowRecommendation(action);
                }
            }
        }

        private void ShowRecommendation(Skills action)
        {
            if (!P.Config.DisableToasts)
            {
                QuestToastOptions options = new() { IconId = action.IconOfAction(CharacterInfo.JobID) };
                Svc.Toasts.ShowQuest($"請使用：{action.NameOfAction()}", options);
            }
        }

        private void OnSolverStarted(Lumina.Excel.Sheets.Recipe recipe, SolverRef solver, CraftState craft, StepState initialStep)
        {
            if (P.Config.AutoMode && solver)
            {
                var estimatedTime = SolverUtils.EstimateCraftTime(solver.Clone()!, craft, initialStep.Quality);
                var count = P.Config.CraftingX && Endurance.Enable ? P.Config.CraftX : 1;
                _estimatedCraftEnd = DateTime.Now + count * estimatedTime;
            }
        }

        private void OnSolverFailed(Lumina.Excel.Sheets.Recipe recipe, string reason)
        {
            var text = $"{reason}。Artisan 已停止繼續製作。";
            Svc.Toasts.ShowError(text);
            DuoLog.Error(text);
            ResetAutoActionTracking();
        }

        private void OnSolverFinished(Lumina.Excel.Sheets.Recipe recipe, SolverRef solver, CraftState craft, StepState finalStep)
        {
            _estimatedCraftEnd = default;
            ResetAutoActionTracking();
        }

        private void OnRecommendationReady(Lumina.Excel.Sheets.Recipe recipe, SolverRef solver, CraftState craft, StepState step, Solver.Recommendation recommendation)
        {
            if (!Simulator.CanUseAction(craft, step, recommendation.Action))
            {
                return;
            }
            ShowRecommendation(recommendation.Action);
            if (P.Config.AutoMode || Endurance.IPCOverride)
            {
                QueueRecommendedAction(recommendation.Action, step);
            }
        }

        private void RetryStaleAutoAction()
        {
            if ((!P.Config.AutoMode && !Endurance.IPCOverride) ||
                !CraftingProcessor.ActiveSolver ||
                Crafting.CurState != Crafting.State.InProgress ||
                Crafting.CurCraft == null ||
                Crafting.CurStep == null)
            {
                if (Crafting.CurState is Crafting.State.IdleNormal or Crafting.State.IdleBetween)
                    ResetAutoActionTracking();
                return;
            }

            if (P.CTM.NumQueuedTasks > 0)
                return;

            var action = CraftingProcessor.NextRec.Action;
            var step = Crafting.CurStep;
            if (action == Skills.None || !Simulator.CanUseAction(Crafting.CurCraft, step, action))
                return;

            if (_lastAutoAction == action &&
                _lastAutoActionStepIndex == step.Index &&
                DateTime.Now - _lastAutoActionQueuedAt < AutoActionWatchdogDelay)
            {
                return;
            }

            Svc.Log.Warning($"Auto action watchdog re-queueing {action} at craft step {step.Index}; state remained {Crafting.CurState} and the action queue is empty.");
            QueueRecommendedAction(action, step);
        }

        private void QueueRecommendedAction(Skills action, StepState step)
        {
            _lastAutoAction = action;
            _lastAutoActionStepIndex = step.Index;
            _lastAutoActionQueuedAt = DateTime.Now;
            _autoActionRetryLogged = false;

            if (!P.Config.ReplicateMacroDelay)
                P.CTM.DelayNext(P.Config.AutoDelay);
            P.CTM.Enqueue(() => Crafting.CurState == Crafting.State.InProgress, 3000, true, "WaitForStateToUseAction");
            P.CTM.Enqueue(() => TryUseRecommendedAction(action, step.Index), 5000, false, $"UseRecommendedAction({action})");
            if (P.Config.ReplicateMacroDelay)
                P.CTM.DelayNext(Calculations.ActionIsLengthyAnimation(action) ? 3000 : 2000);
        }

        private bool? TryUseRecommendedAction(Skills action, int stepIndex)
        {
            if (Crafting.CurState != Crafting.State.InProgress ||
                Crafting.CurStep == null ||
                Crafting.CurStep.Index != stepIndex)
            {
                return true;
            }

            if (!ActionManagerEx.CanUseSkill(action))
            {
                if (!_autoActionRetryLogged && DateTime.Now - _lastAutoActionQueuedAt >= TimeSpan.FromSeconds(1))
                {
                    _autoActionRetryLogged = true;
                    Svc.Log.Warning($"Recommended action {action} is not available yet at craft step {stepIndex}; retrying until the game accepts it.");
                }

                return false;
            }

            return ActionManagerEx.UseSkill(action);
        }

        private static string FormatDuration(TimeSpan duration)
        {
            if (duration < TimeSpan.Zero)
                duration = TimeSpan.Zero;

            return $"{(int)duration.TotalHours:D2}h {duration.Minutes:D2}m {duration.Seconds:D2}s";
        }

        private void ResetAutoActionTracking()
        {
            _lastAutoAction = Skills.None;
            _lastAutoActionStepIndex = 0;
            _lastAutoActionQueuedAt = default;
            _autoActionRetryLogged = false;
        }
    }
}
