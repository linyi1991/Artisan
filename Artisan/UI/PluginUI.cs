using Artisan.Autocraft;
using Artisan.CraftingLists;
using Artisan.CraftingLogic;
using Artisan.FCWorkshops;
using Artisan.RawInformation;
using Artisan.RawInformation.Character;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using ECommons;
using ECommons.DalamudServices;
using ECommons.ImGuiMethods;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;
using PunishLib.ImGuiMethods;
using System;
using System.IO;
using System.Linq;
using System.Numerics;
using ThreadLoadImageHandler = ECommons.ImGuiMethods.ThreadLoadImageHandler;

namespace Artisan.UI
{
    unsafe internal class PluginUI : Window
    {
        public event EventHandler<bool>? CraftingWindowStateChanged;


        private bool visible = false;
        public OpenWindow OpenWindow { get; set; }

        public bool Visible
        {
            get { return this.visible; }
            set { this.visible = value; }
        }

        private bool settingsVisible = false;
        public bool SettingsVisible
        {
            get { return this.settingsVisible; }
            set { this.settingsVisible = value; }
        }

        private bool craftingVisible = false;
        public bool CraftingVisible
        {
            get { return this.craftingVisible; }
            set { if (this.craftingVisible != value) CraftingWindowStateChanged?.Invoke(this, value); this.craftingVisible = value; }
        }

        public PluginUI() : base($"{P.Name} {P.GetType().Assembly.GetName().Version}###Artisan")
        {
            this.RespectCloseHotkey = false;
            this.SizeConstraints = new()
            {
                MinimumSize = new(250, 100),
                MaximumSize = new(9999, 9999)
            };
            this.TitleBarButtons.Add(new()
            {
                Icon = FontAwesomeIcon.Cog,
                ShowTooltip = () => ImGuiEx.SetTooltip("Open Config"),
                Click = (x) => P.PluginUi.IsOpen = true,
            });
            P.ws.AddWindow(this);
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

        public void Dispose()
        {

        }

        public override void Draw()
        {
            if (DalamudInfo.IsOnStaging())
            {
                var scale = ImGui.GetIO().FontGlobalScale;
                ImGui.GetIO().FontGlobalScale = scale * 1.5f;
                using (var f = ImRaii.PushFont(ImGui.GetFont()))
                {
                    ImGuiEx.TextWrapped($"Listen buddy, you're on Dalamud staging, there's every chance any problems you might encounter is specific to Dalamud's testing and not Artisan. I don't make this plugin to work on staging, so don't expect any fixes unless the problem makes it to Dalamud release.");
                    ImGui.Separator();

                    ImGui.Spacing();
                    ImGui.GetIO().FontGlobalScale = scale;
                }

            }
            var region = ImGui.GetContentRegionAvail();
            var itemSpacing = ImGui.GetStyle().ItemSpacing;

            var topLeftSideHeight = region.Y;

            ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(5f.Scale(), 0));
            try
            {
                ShowEnduranceMessage();

                using (var table = ImRaii.Table($"ArtisanTableContainer", 2, ImGuiTableFlags.Resizable))
                {
                    if (!table)
                        return;

                    ImGui.TableSetupColumn("##LeftColumn", ImGuiTableColumnFlags.WidthFixed, ImGui.GetWindowWidth() / 2);

                    ImGui.TableNextColumn();

                    var regionSize = ImGui.GetContentRegionAvail();

                    ImGui.PushStyleVar(ImGuiStyleVar.SelectableTextAlign, new Vector2(0.5f, 0.5f));
                    using (var leftChild = ImRaii.Child($"###ArtisanLeftSide", regionSize with { Y = topLeftSideHeight }, false, ImGuiWindowFlags.NoDecoration))
                    {
                        var imagePath = Path.Combine(Svc.PluginInterface.AssemblyLocation.DirectoryName!, "Images/artisan-icon.png");

                        if (ThreadLoadImageHandler.TryGetTextureWrap(imagePath, out var logo))
                        {
                            ImGuiEx.LineCentered("###ArtisanLogo", () =>
                            {
                                ImGui.Image(logo.Handle, new(125f.Scale(), 125f.Scale()));
                                if (ImGui.IsItemHovered())
                                {
                                    ImGui.BeginTooltip();
                                    ImGui.Text($"You are the 69th person to find this secret. Nice!");
                                    ImGui.EndTooltip();
                                }
                            });

                        }
                        ImGui.Spacing();
                        ImGui.Separator();

                        if (ImGui.Selectable("總覽", OpenWindow == OpenWindow.Overview))
                        {
                            OpenWindow = OpenWindow.Overview;
                        }
                        if (ImGui.Selectable("設定", OpenWindow == OpenWindow.Main))
                        {
                            OpenWindow = OpenWindow.Main;
                        }
                        ImGui.Spacing();
                        if (ImGui.Selectable("連續製作", OpenWindow == OpenWindow.Endurance))
                        {
                            OpenWindow = OpenWindow.Endurance;
                        }
                        ImGui.Spacing();
                        if (ImGui.Selectable("宏管理", OpenWindow == OpenWindow.Macro))
                        {
                            OpenWindow = OpenWindow.Macro;
                        }
                        ImGui.Spacing();
                        if (ImGui.Selectable("Raphael 快取", OpenWindow == OpenWindow.RaphaelCache))
                        {
                            OpenWindow = OpenWindow.RaphaelCache;
                        }
                        ImGui.Spacing();
                        if (ImGui.Selectable("配方分配", OpenWindow == OpenWindow.Assigner))
                        {
                            OpenWindow = OpenWindow.Assigner;
                        }
                        ImGui.Spacing();
                        if (ImGui.Selectable("製作清單", OpenWindow == OpenWindow.Lists))
                        {
                            OpenWindow = OpenWindow.Lists;
                        }
                        ImGui.Spacing();
                        if (ImGui.Selectable("清單生成器", OpenWindow == OpenWindow.SpecialList))
                        {
                            OpenWindow = OpenWindow.SpecialList;
                        }
                        ImGui.Spacing();
                        if (ImGui.Selectable("部队工坊", OpenWindow == OpenWindow.FCWorkshop))
                        {
                            OpenWindow = OpenWindow.FCWorkshop;
                        }
                        ImGui.Spacing();
                        if (ImGui.Selectable("模擬器", OpenWindow == OpenWindow.Simulator))
                        {
                            OpenWindow = OpenWindow.Simulator;
                        }
                        ImGui.Spacing();
                        if (ImGui.Selectable("關於", OpenWindow == OpenWindow.About))
                        {
                            OpenWindow = OpenWindow.About;
                        }


#if DEBUG
                        ImGui.Spacing();
                        if (ImGui.Selectable("DEBUG", OpenWindow == OpenWindow.Debug))
                        {
                            OpenWindow = OpenWindow.Debug;
                        }
                        ImGui.Spacing();
#endif

                    }

                    ImGui.PopStyleVar();
                    ImGui.TableNextColumn();
                    using (var rightChild = ImRaii.Child($"###ArtisanRightSide", Vector2.Zero, false))
                    {
                        switch (OpenWindow)
                        {
                            case OpenWindow.Main:
                                DrawMainWindow();
                                break;
                            case OpenWindow.Endurance:
                                Endurance.Draw();
                                break;
                            case OpenWindow.Lists:
                                CraftingListUI.Draw();
                                break;
                            case OpenWindow.About:
                                AboutTab.Draw("Artisan");
                                break;
                            case OpenWindow.Debug:
                                DebugTab.Draw();
                                break;
                            case OpenWindow.Macro:
                                MacroUI.Draw();
                                break;
                            case OpenWindow.RaphaelCache:
                                RaphaelCacheUI.Draw();
                                break;
                            case OpenWindow.Assigner:
                                AssignerUI.Draw();
                                break;
                            case OpenWindow.FCWorkshop:
                                FCWorkshopUI.Draw();
                                break;
                            case OpenWindow.SpecialList:
                                SpecialLists.Draw();
                                break;
                            case OpenWindow.Overview:
                                DrawOverview();
                                break;
                            case OpenWindow.Simulator:
                                SimulatorUI.Draw();
                                break;
                            case OpenWindow.None:
                                break;
                            default:
                                break;
                        }
                        ;
                    }
                }
            }
            catch (Exception ex)
            {
                ex.Log();
            }
            ImGui.PopStyleVar();
        }

        private void DrawOverview()
        {
            var imagePath = Path.Combine(Svc.PluginInterface.AssemblyLocation.DirectoryName!, "Images/artisan.png");

            if (ThreadLoadImageHandler.TryGetTextureWrap(imagePath, out var logo))
            {
                ImGuiEx.LineCentered("###ArtisanTextLogo", () =>
                {
                    ImGui.Image(logo.Handle, new Vector2(logo.Width, 100f.Scale()));
                });
            }

            ImGuiEx.LineCentered("###ArtisanOverview", () =>
            {
                ImGuiEx.TextUnderlined("Artisan 使用總覽");
            });
            ImGui.Spacing();

            ImGuiEx.TextWrapped("感謝你使用 Artisan。這是一套持續維護的製作輔助插件，整合了動態求解、巨集、連續製作與製作清單功能。");
            ImGui.Spacing();
            ImGuiEx.TextWrapped("開始使用前，建議先了解以下幾種製作模式與求解器的運作方式。");

            ImGui.Spacing();
            ImGuiEx.LineCentered("###ArtisanModes", () =>
            {
                ImGuiEx.TextUnderlined("製作模式");
            });
            ImGui.Spacing();

            ImGuiEx.TextWrapped("「自動執行推薦技能」會依求解器建議代為執行製作技能。預設會以遊戲允許的速度執行，也可以在設定中加入延遲；這不會改變求解器本身的技能判斷。");

            var automode = Path.Combine(Svc.PluginInterface.AssemblyLocation.DirectoryName!, "Images/AutoMode.png");

            if (ThreadLoadImageHandler.TryGetTextureWrap(automode, out var example))
            {
                ImGuiEx.LineCentered("###AutoModeExample", () =>
                {
                    ImGui.Image(example.Handle, new Vector2(example.Width, example.Height));
                });
            }

            ImGuiEx.TextWrapped("未啟用自動模式時，可以使用「半手動」或「完全手動」模式。開始製作後，半手動模式會顯示小型操作視窗。");

            var craftWindowExample = Path.Combine(Svc.PluginInterface.AssemblyLocation.DirectoryName!, "Images/ThemeCraftingWindowExample.png");

            if (ThreadLoadImageHandler.TryGetTextureWrap(craftWindowExample, out example))
            {
                ImGuiEx.LineCentered("###CraftWindowExample", () =>
                {
                    ImGui.Image(example.Handle, new Vector2(example.Width, example.Height));
                });
            }

            ImGuiEx.TextWrapped("半手動模式需要你逐次按下「執行推薦技能」，但不必自己在快捷列尋找技能。完全手動模式則照常按快捷列；Artisan 預設會高亮建議技能，也可在設定中關閉。");

            var outlineExample = Path.Combine(Svc.PluginInterface.AssemblyLocation.DirectoryName!, "Images/OutlineExample.png");

            if (ThreadLoadImageHandler.TryGetTextureWrap(outlineExample, out example))
            {
                ImGuiEx.LineCentered("###OutlineExample", () =>
                {
                    ImGui.Image(example.Handle, new Vector2(example.Width, example.Height));
                });
            }

            ImGui.Spacing();
            ImGuiEx.LineCentered("###ArtisanSuggestions", () =>
            {
                ImGuiEx.TextUnderlined("求解器與巨集");
            });
            ImGui.Spacing();

            ImGuiEx.TextWrapped("Artisan 啟用後會自動提供下一步技能建議，但求解器不能取代足夠的裝備與屬性。若預設求解器無法完成某個配方，可以建立並指派自訂巨集；Artisan 巨集沒有一般遊戲巨集的長度限制，並提供額外的動態選項。");

            ImGui.Spacing();
            ImGuiEx.TextUnderlined("點此開啟巨集選單");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            }
            if (ImGui.IsItemClicked())
            {
                OpenWindow = OpenWindow.Macro;
            }
            ImGui.Spacing();
            ImGuiEx.TextWrapped("建立巨集後，需要透過配方視窗的下拉選單將它指派給配方。此視窗預設附加在遊戲製作筆記右上方，也能在設定中改為獨立顯示。");


            var recipeWindowExample = Path.Combine(Svc.PluginInterface.AssemblyLocation.DirectoryName!, "Images/RecipeWindowExample.png");

            if (ThreadLoadImageHandler.TryGetTextureWrap(recipeWindowExample, out example))
            {
                ImGuiEx.LineCentered("###RecipeWindowExample", () =>
                {
                    ImGui.Image(example.Handle, new Vector2(example.Width, example.Height));
                });
            }


            ImGuiEx.TextWrapped("從下拉選單選擇已建立的巨集後，製作該物品時就會用巨集內容取代預設求解器建議。");


            ImGui.Spacing();
            ImGuiEx.LineCentered("###Endurance", () =>
            {
                ImGuiEx.TextUnderlined("連續製作");
            });
            ImGui.Spacing();

            ImGuiEx.TextWrapped("連續製作模式會重複製作目前在製作筆記中選取的配方，直到達到指定數量或材料耗盡。它也能在每次製作之間管理食物、藥品、指南、裝備修理與魔晶石提取。");

            ImGui.Spacing();
            ImGuiEx.TextUnderlined("點此開啟連續製作選單");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            }
            if (ImGui.IsItemClicked())
            {
                OpenWindow = OpenWindow.Endurance;
            }

            ImGui.Spacing();
            ImGuiEx.LineCentered("###Lists", () =>
            {
                ImGuiEx.TextUnderlined("製作清單");
            });
            ImGui.Spacing();

            ImGuiEx.TextWrapped("製作清單可以依序製作多種物品，並協助整理原料、半成品與最終成品，也支援 Teamcraft 匯入與匯出。");

            ImGui.Spacing();
            ImGuiEx.TextUnderlined("點此開啟製作清單選單");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            }
            if (ImGui.IsItemClicked())
            {
                OpenWindow = OpenWindow.Lists;
            }

            ImGui.Spacing();
            ImGuiEx.LineCentered("###Questions", () =>
            {
                ImGuiEx.TextUnderlined("需要協助？");
            });
            ImGui.Spacing();

            ImGuiEx.TextWrapped("若這裡沒有解答你的問題，可以前往");
            ImGui.SameLine(ImGui.GetCursorPosX(), 1.5f);
            ImGuiEx.TextUnderlined("Discord 伺服器");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                if (ImGui.IsItemClicked())
                {
                    Util.OpenLink("https://discord.gg/Zzrcc8kmvy");
                }
            }

            ImGuiEx.TextWrapped("也可以在以下頁面回報問題：");
            ImGui.SameLine(ImGui.GetCursorPosX(), 2f);
            ImGuiEx.TextUnderlined("GitHub 專案頁面");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

                if (ImGui.IsItemClicked())
                {
                    Util.OpenLink("https://github.com/PunishXIV/Artisan");
                }
            }

        }

        public static void DrawMainWindow()
        {
            ImGui.TextWrapped($"這裡可以調整 Artisan 的製作設定；部分選項也能在製作過程中切換。");
            ImGui.TextWrapped($"若要使用手動技能高亮，請把所有已解鎖的製作技能放在可見的快捷鍵欄上。");
            bool autoEnabled = P.Config.AutoMode;
            bool delayRec = P.Config.DelayRecommendation;
            bool failureCheck = P.Config.DisableFailurePrediction;
            int maxQuality = P.Config.MaxPercentage;
            bool useTricksGood = P.Config.UseTricksGood;
            bool useTricksExcellent = P.Config.UseTricksExcellent;
            bool useSpecialist = P.Config.UseSpecialist;
            //bool showEHQ = P.Config.ShowEHQ;
            //bool useSimulated = P.Config.UseSimulatedStartingQuality;
            bool disableGlow = P.Config.DisableHighlightedAction;
            bool disableToasts = P.Config.DisableToasts;

            ImGui.Separator();

            if (ImGui.Button("套用安全推薦設定", new Vector2(ImGui.GetContentRegionAvail().X, 0)))
            {
                P.Config.ApplySafeRecommendedSettings();
                Notify.Success("已套用安全推薦設定：HQ 100%、失敗或 NQ 停止、自動修理與消耗品檢查。專家技能僅在非批量製作時視情況使用。");
            }
            ImGuiComponents.HelpMarker("適合希望直接使用的安全預設。單件或手動製作可視情況使用專家技能；連續製作與製作清單會自動停用，避免大量消耗工匠圖紙。");

            if (ImGui.CollapsingHeader("一般設定"))
            {
                if (ImGui.Checkbox("自動執行推薦技能", ref autoEnabled))
                {
                    P.Config.AutoMode = autoEnabled;
                    P.Config.Save();
                }
                ImGuiComponents.HelpMarker($"自動使用 Artisan 推薦的每一個製作技能。");
                if (autoEnabled)
                {
                    if (ImGui.Checkbox($"使用宏指令的等待時間", ref P.Config.ReplicateMacroDelay))
                    {
                        P.Config.Save();
                    }

                    if (!P.Config.ReplicateMacroDelay)
                    {
                        var delay = P.Config.AutoDelay;
                        ImGui.PushItemWidth(200);
                        if (ImGui.SliderInt("技能執行延遲 (毫秒)###ActionDelay", ref delay, 0, 1000))
                        {
                            if (delay < 0) delay = 0;
                            if (delay > 1000) delay = 1000;

                            P.Config.AutoDelay = delay;
                            P.Config.Save();
                        }
                    }
                }

                bool requireFoodPot = P.Config.AbortIfNoFoodPot;
                if (ImGui.Checkbox("強制檢查食物／藥水／指南", ref requireFoodPot))
                {
                    P.Config.AbortIfNoFoodPot = requireFoodPot;
                    P.Config.Save();
                }
                ImGuiComponents.HelpMarker("找不到配方指定的食物、藥水或指南時，拒絕開始正式製作，避免在屬性不足時浪費材料。");

                if (ImGui.Checkbox("製作練習時使用消耗品", ref P.Config.UseConsumablesTrial))
                {
                    P.Config.Save();
                }

                if (ImGui.Checkbox("簡易製作時使用消耗品", ref P.Config.UseConsumablesQuickSynth))
                {
                    P.Config.Save();
                }

                ImGui.Indent();
                if (ImGui.CollapsingHeader("預設消耗品"))
                {
                    bool changed = false;
                    changed |= P.Config.DefaultConsumables.DrawFood();
                    changed |= P.Config.DefaultConsumables.DrawPotion();
                    changed |= P.Config.DefaultConsumables.DrawManual();
                    changed |= P.Config.DefaultConsumables.DrawSquadronManual();

                    if (changed)
                    {
                        P.Config.Save();
                    }
                }
                ImGui.Unindent();

                if (ImGui.Checkbox($"附近有修理 NPC 時優先使用 NPC", ref P.Config.PrioritizeRepairNPC))
                {
                    P.Config.Save();
                }

                ImGuiComponents.HelpMarker("附近沒有修理 NPC 時，如果職業等級足夠，仍會嘗試自行修理。");

                if (ImGui.Checkbox($"無法修理時停止連續製作", ref P.Config.DisableEnduranceNoRepair))
                    P.Config.Save();

                ImGuiComponents.HelpMarker($"耐久度到達修理門檻後，若自己與 NPC 都無法修理，就停止連續製作。");

                if (ImGui.Checkbox($"無法修理時暫停製作清單", ref P.Config.DisableListsNoRepair))
                    P.Config.Save();

                ImGuiComponents.HelpMarker($"耐久度到達修理門檻後，若無法修理，就暫停目前製作清單。");

                bool requestStop = P.Config.RequestToStopDuty;
                bool requestResume = P.Config.RequestToResumeDuty;
                int resumeDelay = P.Config.RequestToResumeDelay;

                if (ImGui.Checkbox("副本排隊完成時停止連續製作／暫停清單", ref requestStop))
                {
                    P.Config.RequestToStopDuty = requestStop;
                    P.Config.Save();
                }

                if (requestStop)
                {
                    if (ImGui.Checkbox("離開副本後恢復連續製作／繼續清單", ref requestResume))
                    {
                        P.Config.RequestToResumeDuty = requestResume;
                        P.Config.Save();
                    }

                    if (requestResume)
                    {
                        if (ImGui.SliderInt("恢復前等待 (秒)", ref resumeDelay, 5, 60))
                        {
                            P.Config.RequestToResumeDelay = resumeDelay;
                        }
                    }
                }

                if (ImGui.Checkbox("不要自動裝備製作所需物品", ref P.Config.DontEquipItems))
                    P.Config.Save();

                if (ImGui.Checkbox("連續製作完成後播放聲音", ref P.Config.PlaySoundFinishEndurance))
                    P.Config.Save();

                if (ImGui.Checkbox($"製作清單完成後播放聲音", ref P.Config.PlaySoundFinishList))
                    P.Config.Save();

                if (P.Config.PlaySoundFinishEndurance || P.Config.PlaySoundFinishList)
                {
                    if (ImGui.SliderFloat("聲音音量", ref P.Config.SoundVolume, 0f, 1f, "%.2f"))
                        P.Config.Save();
                }

                if (ImGuiEx.ButtonCtrl("重置宇宙探索製作設定"))
                {
                    var copy = P.Config.RecipeConfigs;
                    foreach (var c in copy)
                    {
                        if (Svc.Data.GetExcelSheet<Recipe>().GetRow(c.Key).Number == 0)
                            P.Config.RecipeConfigs.Remove(c.Key);
                    }
                }
            }
            if (ImGui.CollapsingHeader("宏設定"))
            {
                if (ImGui.Checkbox("無法使用技能時跳過該宏步驟", ref P.Config.SkipMacroStepIfUnable))
                    P.Config.Save();

                if (ImGui.Checkbox($"宏執行完畢後不要讓 Artisan 繼續製作", ref P.Config.DisableMacroArtisanRecommendation))
                    P.Config.Save();
            }
            if (ImGui.CollapsingHeader("普通配方求解器設定"))
            {
                if (ImGui.Checkbox($"在{LuminaSheets.AddonSheet[227].Text.ToString()}狀態使用{Skills.TricksOfTrade.NameOfAction()}", ref useTricksGood))
                {
                    P.Config.UseTricksGood = useTricksGood;
                    P.Config.Save();
                }
                ImGui.SameLine();
                if (ImGui.Checkbox($"在{LuminaSheets.AddonSheet[228].Text.ToString()}狀態使用{Skills.TricksOfTrade.NameOfAction()}", ref useTricksExcellent))
                {
                    P.Config.UseTricksExcellent = useTricksExcellent;
                    P.Config.Save();
                }
                ImGuiComponents.HelpMarker($"製作狀態為{LuminaSheets.AddonSheet[227].Text.ToString()}或{LuminaSheets.AddonSheet[228].Text.ToString()}時，優先使用{Skills.TricksOfTrade.NameOfAction()}。這可能取代{Skills.PreciseTouch.NameOfAction()}或{Skills.IntensiveSynthesis.NameOfAction()}。");
                if (ImGui.Checkbox("視情況使用專家技能（批量時自動停用）", ref useSpecialist))
                {
                    P.Config.UseSpecialist = useSpecialist;
                    P.Config.Save();
                }
                ImGuiComponents.HelpMarker($"單件或手動高難度製作時，求解器會視狀況使用專家技能，可能消耗工匠圖紙。進入連續製作或製作清單後會自動停用，不需手動切換。");
                ImGui.TextWrapped("目標品質百分比");
                ImGuiComponents.HelpMarker($"品質達到下方比例後，Artisan 將只提升製作進度。昂貴 HQ 配方建議保持 100%。");
                if (ImGui.SliderInt("###SliderMaxQuality", ref maxQuality, 0, 100, $"%d%%"))
                {
                    P.Config.MaxPercentage = maxQuality;
                    P.Config.Save();
                }

                ImGui.Text($"收藏品目標檔位");
                ImGuiComponents.HelpMarker("收藏品達到指定檔位後，求解器將停止繼續提升品質。");

                if (ImGui.RadioButton($"最低", P.Config.SolverCollectibleMode == 1))
                {
                    P.Config.SolverCollectibleMode = 1;
                    P.Config.Save();
                }
                ImGui.SameLine();
                if (ImGui.RadioButton($"中間", P.Config.SolverCollectibleMode == 2))
                {
                    P.Config.SolverCollectibleMode = 2;
                    P.Config.Save();
                }
                ImGui.SameLine();
                if (ImGui.RadioButton($"最高", P.Config.SolverCollectibleMode == 3))
                {
                    P.Config.SolverCollectibleMode = 3;
                    P.Config.Save();
                }

                if (ImGui.Checkbox($"使用品質起手技能 ({Skills.Reflect.NameOfAction()})", ref P.Config.UseQualityStarter))
                    P.Config.Save();
                ImGuiComponents.HelpMarker($"通常更適合耐久較低的配方。");

                //if (ImGui.Checkbox("Low Stat Mode", ref P.Config.LowStatsMode))
                //    P.Config.Save();

                //ImGuiComponents.HelpMarker("This swaps out Waste Not II & Groundwork for Prudent Synthesis");

                ImGui.TextWrapped($"{Skills.PreparatoryTouch.NameOfAction()}：最多使用到 {Buffs.InnerQuiet.NameOfBuff()} 層數");
                ImGui.SameLine();
                ImGuiComponents.HelpMarker($"只會在{Buffs.InnerQuiet.NameOfBuff()}未達設定層數時使用{Skills.PreparatoryTouch.NameOfAction()}，可用來調整 CP 消耗。");
                if (ImGui.SliderInt($"###MaxIQStacksPrepTouch", ref P.Config.MaxIQPrepTouch, 0, 10))
                    P.Config.Save();

                if (ImGui.Checkbox($"可用時使用材料奇蹟", ref P.Config.UseMaterialMiracle))
                    P.Config.Save();

                ImGuiComponents.HelpMarker("材料奇蹟生效期間會暫時由普通配方求解器切換至專家配方求解器。由於這是有時間限制的狀態，而不是固定層數增益，模擬器無法完全準確重現結果。");

                if (P.Config.UseMaterialMiracle)
                {
                    ImGui.Indent();
                    if (ImGui.Checkbox($"每次製作可使用多次", ref P.Config.MaterialMiracleMulti))
                        P.Config.Save();

                    ImGui.Unindent();
                }

            }
            bool openExpert = false;
            if (ImGui.CollapsingHeader("專家配方求解器設定"))
            {
                openExpert = true;
                if (P.Config.ExpertSolverConfig.expertIcon is not null)
                {
                    ImGui.SameLine();
                    ImGui.Image(P.Config.ExpertSolverConfig.expertIcon.Handle, new(P.Config.ExpertSolverConfig.expertIcon.Width * ImGuiHelpers.GlobalScaleSafe, ImGui.GetItemRectSize().Y), new(0, 0), new Vector2(1, 1), new(0.94f, 0.57f, 0f, 1f));
                }
                if (P.Config.ExpertSolverConfig.Draw())
                    P.Config.Save();
            }
            if (!openExpert)
            {
                if (P.Config.ExpertSolverConfig.expertIcon is not null)
                {
                    ImGui.SameLine();
                    ImGui.Image(P.Config.ExpertSolverConfig.expertIcon.Handle, new(P.Config.ExpertSolverConfig.expertIcon.Width * ImGuiHelpers.GlobalScaleSafe, ImGui.GetItemRectSize().Y), new(0, 0), new Vector2(1, 1), new(0.94f, 0.57f, 0f, 1f));
                }
            }

            if (ImGui.CollapsingHeader("Raphael 求解器設定"))
            {
                if (P.Config.RaphaelSolverConfig.Draw())
                    P.Config.Save();
            }

            if (ImGui.CollapsingHeader("Craftimizer 求解器設定"))
            {
                var settings = P.Config.CraftimizerSolverConfig;
                ImGui.Indent();
                ImGui.TextWrapped("此設定只控制 Craftimizer 2.11 的即時求解資源；Artisan 仍負責執行技能、逾時處理與備援。所有搜尋共用單一工作閘門，不會同時啟動多輪求解。");

                if (ImGui.Checkbox("依電腦規格安全自動調整執行緒", ref settings.AutoThreads))
                    P.Config.Save();

                var effectiveThreads = settings.ResolveThreadCount();
                var memoryGiB = settings.DetectedMemoryGiB();
                ImGui.TextWrapped($"偵測：{Environment.ProcessorCount} 核心、約 {memoryGiB:0.#} GiB 可用記憶體上限；目前求解使用 {effectiveThreads} 個執行緒。");
                ImGuiComponents.HelpMarker("安全自動模式會依 CPU 與記憶體規格選擇 1–4 個執行緒；偵測到高記憶體壓力時，該次求解會自動降為 1。這不會增加同時執行的求解工作數。你的 10 核／32 GiB 電腦正常情況建議 2 個執行緒。");

                if (!settings.AutoThreads)
                {
                    var maxManualThreads = Math.Max(1, Math.Min(4, Environment.ProcessorCount));
                    if (ImGui.SliderInt("最大執行緒數###CraftimizerThreads", ref settings.MaxThreads, 1, maxManualThreads))
                    {
                        settings.Clamp();
                        P.Config.Save();
                    }
                    ImGui.TextWrapped("手動模式最高限制為 4；提高執行緒可能縮短單步等待，但會增加遊戲與 Wine 的 CPU 負擔。");
                }

                if (ImGui.SliderInt("每步求解時間上限（毫秒）", ref settings.MaxTimeMs, 250, 1500))
                {
                    settings.Clamp();
                    P.Config.Save();
                }
                ImGuiComponents.HelpMarker("宇宙／專家製作每個實際步驟的 Craftimizer 搜尋時間。逾時或無完整解法時會切回 Artisan 備援。");

                if (ImGui.SliderInt("每步總迭代上限", ref settings.MaxIterations, 25_000, 200_000, "%d"))
                {
                    settings.Clamp();
                    P.Config.Save();
                }
                ImGuiComponents.HelpMarker("這是所有執行緒共享的總上限，不會乘上執行緒數。數值越高可能改善困難配方的搜尋，但會增加 CPU 與配置量。");

                if (ImGui.Button("恢復 Craftimizer 安全預設值"))
                {
                    P.Config.CraftimizerSolverConfig = new();
                    P.Config.Save();
                }
                ImGui.Unindent();
            }

            using (ImRaii.Disabled())
            {
                if (ImGui.CollapsingHeader("腳本求解器設定 (目前停用)"))
                {
                    if (P.Config.ScriptSolverConfig.Draw())
                        P.Config.Save();
                }
            }
            if (ImGui.CollapsingHeader("介面設定"))
            {
                if (ImGui.Checkbox("關閉快捷鍵欄技能高亮框", ref disableGlow))
                {
                    P.Config.DisableHighlightedAction = disableGlow;
                    P.Config.Save();
                }
                ImGuiComponents.HelpMarker("手動製作時，用於標示快捷鍵欄推薦技能的高亮框。");

                if (ImGui.Checkbox($"關閉推薦技能彈出提示", ref disableToasts))
                {
                    P.Config.DisableToasts = disableToasts;
                    P.Config.Save();
                }

                ImGuiComponents.HelpMarker("Artisan 推薦新技能時顯示的彈出提示。");

                bool lockMini = P.Config.LockMiniMenuR;
                if (ImGui.Checkbox("將配方迷你選單固定在配方視窗旁", ref lockMini))
                {
                    P.Config.LockMiniMenuR = lockMini;
                    P.Config.Save();
                }

                if (!P.Config.LockMiniMenuR)
                {
                    if (ImGui.Checkbox($"鎖定迷你選單位置", ref P.Config.PinMiniMenu))
                    {
                        P.Config.Save();
                    }
                }

                if (ImGui.Button("重置配方迷你選單位置"))
                {
                    AtkResNodeFunctions.ResetPosition = true;
                }

                if (ImGui.Checkbox($"啟用增強配方搜索欄", ref P.Config.ReplaceSearch))
                {
                    P.Config.Save();
                }
                ImGuiComponents.HelpMarker($"強化製作筆記的搜尋列，可即時顯示結果並點擊開啟配方。");

                bool hideQuestHelper = P.Config.HideQuestHelper;
                if (ImGui.Checkbox($"隱藏任務製作助手", ref hideQuestHelper))
                {
                    P.Config.HideQuestHelper = hideQuestHelper;
                    P.Config.Save();
                }

                bool hideTheme = P.Config.DisableTheme;
                if (ImGui.Checkbox("關閉 Artisan 自訂主題", ref hideTheme))
                {
                    P.Config.DisableTheme = hideTheme;
                    P.Config.Save();
                }
                ImGui.SameLine();

                if (IconButtons.IconTextButton(FontAwesomeIcon.Clipboard, "複製主題"))
                {
                    ImGui.SetClipboardText("DS1H4sIAAAAAAAACq1YS3PbNhD+Kx2ePR6AeJG+xXYbH+KOJ3bHbW60REusaFGlKOXhyX/v4rEACEqumlY+ECD32/cuFn7NquyCnpOz7Cm7eM1+zy5yvfnDPL+fZTP4at7MHVntyMi5MGTwBLJn+HqWLZB46Ygbx64C5kQv/nRo8xXQ3AhZZRdCv2jdhxdHxUeqrJO3Ftslb5l5u/Fa2rfEvP0LWBkBPQiSerF1Cg7wApBn2c5wOMv2juNn9/zieH09aP63g+Kqyr1mI91mHdj5mj3UX4bEG+b5yT0fzRPoNeF1s62e2np+EuCxWc+7z5cLr1SuuCBlkTvdqBCEKmaQxCHJeZmXnFKlgMHVsmnnEZ5IyXMiFUfjwt6yCHvDSitx1212m4gHV0QURY4saMEYl6Q4rsRl18/rPuCZQ+rFJxeARwyAJb5fVmD4NBaJEK3eL331UscuAgflOcY0J5zLUioHpHmhCC0lCuSBwU23r3sfF/0N0wKdoxcGFqHezYZmHypJIkgiSCJIalc8NEM7Utb6ErWlwngt9aUoFRWSB3wilRUl5SRwISUFvhJt9lvDrMgLIjgLzK66tq0228j0H+R3W693l1UfmUd9kqA79MKn9/2sB9lPI8hbofb073vdh1BbQYRgqKzfGbTfTWVqHmnMOcXUpI6BXhzGJjEQCNULmy4x9GpZz1a3Vb8KqaIDz4RPVGZin6dlZPKDSS29baAyRqYfzVGnr0ekaaowTbEw9MLjLnfD0GGT1unHSSlKr2lRyqLA2qU5ESovi6m+lkvqYiZ1/ygxyqrgjDKF8Yr2lp1pd4R7dokhvOBUQk37TCVKQbX4TMVtyuymruKWJCURVEofClYWbNpWCQfFifDwsWnYyXXS8ZxDOI+H0uLToPzrhKg3VV8N3amt1dP/t5goW/E85pg2pB8N8sd623yr3/dNOPYVstELg9cLA8zFCJKapQpEYkPVi9CMA/L/Uv8hrk1hmg9WKKMQXyIxnGFrm6i06MkhBHlIiQ8rI0xx4k/rsLWBsWpbTmmhqFIypcvUHTRgQ859V/bbKaPf1s/dbBcfD0R6NnCWwg/dS3lB4MfQMSrnCY9EK8qEw9uUl4YdHjRQRVFTuu5mq2a9uOvrfVOH0SDHqtXxMjDfi1RA/fyyGb7G5y5KdJg8EnTXdsOHZl1vQyJJQrlCQTDsEBi80HdhO+VwrEP48hwdTRp202yHbgGzhRfu03/UCA4gjglDd44mUT2D2i4UH9coSy8mfjEYN54NfbcOOIZnn15M7YqAH5rFEmdl3eJ8r0N5E9zH0fz71nQQyN+1/zSP6yR2A/l93dazoY6n5DdyiumWc91Xi+u+2zxU/aI+Jipq2QD5tdrfgO3t2P5jcqz9gLEXAEjgFHzcMJUgr5uXyDQsNSxZtCvX81s3r1qLOw0EztC3ORiEs4vssu9W9fqn2263HqpmncFF016PqklGjh1kjQ2NUyUJH08mcIk9gSrqn+jg0XFoqeqTrmDPwQv+PDEr6wl3oljaxcRSRTCyMc/lJJ/lAcnNhMr3WWZ+ES3exrXE+HJ2yNOrowkb97A2cExdXcrYjaFToVDfGSMqnCaDa0pi/vzNMyLG/wQEyzmzfhx7KAwJUn93Fz6v5shD8B+DRAG4Oh+QHYapovAd3/OEQzuiDSdE4c8wjJHh7iiBFFozvP3+NxT8RWGlEQAA");
                    Notify.Success("主題設定已複製到剪貼簿。");
                }

                if (ImGui.Checkbox("關閉製作清單的 Allagan Tools 整合", ref P.Config.DisableAllaganTools))
                    P.Config.Save();

                if (ImGui.Checkbox("隱藏 Artisan 右鍵選單選項", ref P.Config.HideContextMenus))
                    P.Config.Save();

                ImGuiComponents.HelpMarker("在配方上按右鍵或手把方形鍵時顯示的 Artisan 功能選單。");

                ImGui.Indent();
                if (ImGui.CollapsingHeader("模擬器設定"))
                {
                    if (ImGui.Checkbox("隱藏配方視窗中的快速模擬結果", ref P.Config.HideRecipeWindowSimulator))
                        P.Config.Save();

                    if (ImGui.SliderFloat("模擬器技能圖示大小", ref P.Config.SimulatorActionSize, 5f, 70f))
                    {
                        P.Config.Save();
                    }
                    ImGuiComponents.HelpMarker("調整模擬器內技能圖示的大小。");

                    if (ImGui.Checkbox("手動模式中啟用滑鼠停留預覽", ref P.Config.SimulatorHoverMode))
                        P.Config.Save();

                    if (ImGui.Checkbox($"隱藏技能說明提示", ref P.Config.DisableSimulatorActionTooltips))
                        P.Config.Save();

                    ImGuiComponents.HelpMarker("手動模式中，滑鼠停留在技能上時不顯示技能說明。");
                }
                ImGui.Unindent();
            }
            if (ImGui.CollapsingHeader("製作清單設定"))
            {
                ImGui.TextWrapped($"以下選項會自動套用到新建立的製作清單。");

                if (ImGui.Checkbox("跳過庫存數量已經足夠的物品", ref P.Config.DefaultListSkip))
                {
                    P.Config.Save();
                }

                if (ImGui.Checkbox("自動精製魔晶石", ref P.Config.DefaultListMateria))
                {
                    P.Config.Save();
                }

                if (ImGui.Checkbox("自動修理裝備", ref P.Config.DefaultListRepair))
                {
                    P.Config.Save();
                }

                if (P.Config.DefaultListRepair)
                {
                    ImGui.TextWrapped($"耐久度低於此比例時修理");
                    ImGui.SameLine();
                    if (ImGui.SliderInt("###SliderRepairDefault", ref P.Config.DefaultListRepairPercent, 0, 100, $"%d%%"))
                    {
                        P.Config.Save();
                    }
                }

                if (ImGui.Checkbox("新增到清單的物品預設使用簡易製作", ref P.Config.DefaultListQuickSynth))
                {
                    P.Config.Save();
                }

                if (ImGui.Checkbox("加入清單後重設加入數量", ref P.Config.ResetTimesToAdd))
                    P.Config.Save();

                ImGui.PushItemWidth(100);
                if (ImGui.InputInt("右鍵選單每次加入的製作次數", ref P.Config.ContextMenuLoops))
                {
                    if (P.Config.ContextMenuLoops <= 0)
                        P.Config.ContextMenuLoops = 1;

                    P.Config.Save();
                }

                ImGui.SetNextItemWidth(180f);
                if (ImGui.InputInt("Recipe 視窗 Craft X / 取料最大次數", ref P.Config.RecipeWindowRetainerRestockMax, 10, 100))
                {
                    if (P.Config.RecipeWindowRetainerRestockMax <= 0)
                        P.Config.RecipeWindowRetainerRestockMax = 1;

                    P.Config.Save();
                }

                ImGui.PushItemWidth(400);
                if (ImGui.SliderFloat("每次製作之間的等待時間", ref P.Config.ListCraftThrottle2, 0f, 2f, "%.1f"))
                {
                    if (P.Config.ListCraftThrottle2 < 0f)
                        P.Config.ListCraftThrottle2 = 0f;

                    if (P.Config.ListCraftThrottle2 > 2f)
                        P.Config.ListCraftThrottle2 = 2f;

                    P.Config.Save();
                }

                ImGui.Indent();
                if (ImGui.CollapsingHeader("材料表設定"))
                {
                    ImGuiEx.TextWrapped(ImGuiColors.DalamudYellow, "若已經開啟過某份清單的材料表，下列欄位預設值不會回頭修改該清單。");

                    if (ImGui.Checkbox($@"預設隱藏“背包庫存”欄", ref P.Config.DefaultHideInventoryColumn))
                        P.Config.Save();

                    if (ImGui.Checkbox($"預設隱藏“僱員庫存”欄", ref P.Config.DefaultHideRetainerColumn))
                        P.Config.Save();

                    if (ImGui.Checkbox($"預設隱藏“尚缺數量”欄", ref P.Config.DefaultHideRemainingColumn))
                        P.Config.Save();

                    if (ImGui.Checkbox($"預設隱藏“來源”欄", ref P.Config.DefaultHideCraftableColumn))
                        P.Config.Save();

                    if (ImGui.Checkbox($"預設隱藏“可製作數量”欄", ref P.Config.DefaultHideCraftableCountColumn))
                        P.Config.Save();

                    if (ImGui.Checkbox($"預設隱藏“用於製作”欄", ref P.Config.DefaultHideCraftItemsColumn))
                        P.Config.Save();

                    if (ImGui.Checkbox($"預設隱藏“分類”欄", ref P.Config.DefaultHideCategoryColumn))
                        P.Config.Save();

                    if (ImGui.Checkbox($"預設隱藏“採集區域”欄", ref P.Config.DefaultHideGatherLocationColumn))
                        P.Config.Save();

                    if (ImGui.Checkbox($"預設隱藏“物品 ID”欄", ref P.Config.DefaultHideIdColumn))
                        P.Config.Save();

                    if (ImGui.Checkbox($"預設只顯示可製作 HQ 的半成品", ref P.Config.DefaultHQCrafts))
                        P.Config.Save();

                    if (ImGui.Checkbox($"預設啟用顏色狀態檢查", ref P.Config.DefaultColourValidation))
                        P.Config.Save();

                    if (ImGui.Checkbox($"從 Universalis 取得市場價格", ref P.Config.UseUniversalis))
                        P.Config.Save();

                    if (P.Config.UseUniversalis)
                    {
                        if (ImGui.Checkbox($"Universalis 只查詢目前資料中心", ref P.Config.LimitUnversalisToDC))
                            P.Config.Save();

                        if (ImGui.Checkbox($"只在手動要求時查詢價格", ref P.Config.UniversalisOnDemand))
                            P.Config.Save();

                        ImGuiComponents.HelpMarker("必須手動點擊按鈕才會查詢各物品價格。");
                    }
                }

                ImGui.Unindent();
            }
        }

        private void ShowEnduranceMessage()
        {
            if (!P.Config.ViewedEnduranceMessage)
            {
                P.Config.ViewedEnduranceMessage = true;
                P.Config.Save();

                ImGui.OpenPopup("EndurancePopup");

                var windowSize = new Vector2(512 * ImGuiHelpers.GlobalScale,
                    ImGui.GetTextLineHeightWithSpacing() * 13 + 2 * ImGui.GetFrameHeightWithSpacing() * 2f);
                ImGui.SetNextWindowSize(windowSize);
                ImGui.SetNextWindowPos((ImGui.GetIO().DisplaySize - windowSize) / 2);

                using var popup = ImRaii.Popup("EndurancePopup",
                    ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.Modal);
                if (!popup)
                    return;

                ImGui.TextWrapped("連續製作的材料配置方式已移至新的設定。若沒有正確設定材料比例，請使用下方的最大可製作數量模式。");
                ImGui.Dummy(new Vector2(0));

                var imagePath = Path.Combine(Svc.PluginInterface.AssemblyLocation.DirectoryName!, "Images/EnduranceNewSetting.png");

                if (ThreadLoadImageHandler.TryGetTextureWrap(imagePath, out var img))
                {
                    ImGuiEx.ImGuiLineCentered("###EnduranceNewSetting", () =>
                    {
                        ImGui.Image(img.Handle, new Vector2(img.Width, img.Height));
                    });
                }

                ImGui.Spacing();

                ImGui.TextWrapped("若不在意 NQ／HQ 原料比例，請啟用「最大可製作數量模式」，讓 Artisan 自動依現有材料配置並持續製作。");

                ImGui.SetCursorPosY(windowSize.Y - ImGui.GetFrameHeight() - ImGui.GetStyle().WindowPadding.Y);
                if (ImGui.Button("關閉", -Vector2.UnitX))
                {
                    ImGui.CloseCurrentPopup();
                }
            }
        }
    }

    public enum OpenWindow
    {
        None = 0,
        Main = 1,
        Endurance = 2,
        Macro = 3,
        Lists = 4,
        About = 5,
        Debug = 6,
        FCWorkshop = 7,
        SpecialList = 8,
        Overview = 9,
        Simulator = 10,
        RaphaelCache = 11,
        Assigner = 12,
    }
}
