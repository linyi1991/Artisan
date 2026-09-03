using Artisan.CraftingLists;
using Artisan.CraftingLogic.Solvers;
using Artisan.GameInterop;
using Artisan.RawInformation;
using Artisan.RawInformation.Character;
using Artisan.Sounds;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using ECommons;
using ECommons.CircularBuffers;
using ECommons.DalamudServices;
using ECommons.ExcelServices;
using ECommons.ImGuiMethods;
using ECommons.Logging;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using static ECommons.GenericHelpers;

namespace Artisan.Autocraft
{
    public class EnduranceIngredients
    {
        public int HQSet { get; set; }
        public int IngredientSlot { get; set; }
        public int NQSet { get; set; }
    }

    internal static unsafe class Endurance
    {
        internal static bool IPCOverride = false;
        internal static bool SkipBuffs = false;
        internal static CircularBuffer<long> Errors = new(5);
        static CircularBuffer<long> FailedStarts = new(5);

        internal static List<int>? HQData = null;

        internal static ushort RecipeID
        {
            get;
            set
            {
                if (field != value)
                {
                    P.Config.CraftingX = false;
                    P.Config.CraftX = 0;
                }
                field = value;
            }
        }

        internal static EnduranceIngredients[] SetIngredients = new EnduranceIngredients[6];

        internal static readonly List<uint> UnableToCraftErrors = new List<uint>()
        {
            1134,1135,1136,1137,1138,1139,1140,1141,1142,1143,1144,1145,1146,1148,1149,1198,1199,1222,1223,1224,
        };

        internal static bool Enable
        {
            get => enable;
            set
            {
                enable = value;
            }
        }

        internal static string RecipeName
        {
            get => RecipeID == 0 ? "No Recipe Selected" : LuminaSheets.RecipeSheet[RecipeID].ItemResult.Value.Name.ToDalamudString().ToString().Trim();
        }

        internal static void ToggleEndurance(bool enable)
        {
            if (RecipeID > 0 && enable)
            {
                Enable = enable;
            }
            else if (Enable)
            {
                Svc.Log.Debug("Endurance toggled off");
                Enable = false;
                IPCOverride = false;
                PreCrafting.Tasks.Clear();
            }
        }

        internal static void Dispose()
        {
            Svc.Toasts.ErrorToast -= Toasts_ErrorToast;
            Svc.Toasts.ErrorToast -= CheckNonMaxQuantityModeFinished;
        }

        internal static void Draw()
        {
            if (CraftingListUI.Processing)
            {
                ImGui.TextWrapped("正在處理製作清單...");
                return;
            }

            ImGui.TextWrapped("連續製作模式會重複製作同一個配方，直到完成指定次數或材料耗盡。它可以自動修理裝備、使用食物與藥水、使用製作指南，並在精製度達到 100% 時提取魔晶石。這裡的設定與製作清單互相獨立，只適合重複製作單一物品。");
            ImGui.Separator();
            ImGui.Spacing();

            if (RecipeID == 0)
            {
                ImGuiEx.TextV(ImGuiColors.DalamudRed, "No recipe selected");
            }
            else
            {
                if (!CraftingListFunctions.HasItemsForRecipe(RecipeID))
                    ImGui.BeginDisabled();

                if (ImGui.Checkbox("啟用連續製作模式", ref enable))
                {
                    ToggleEndurance(enable);
                }

                if (!CraftingListFunctions.HasItemsForRecipe(RecipeID))
                {
                    ImGui.EndDisabled();

                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    {
                        ImGui.BeginTooltip();
                        ImGui.Text($"無法開始連續製作，因為材料不足。");
                        ImGui.EndTooltip();
                    }
                }

                ImGuiComponents.HelpMarker("請先在製作筆記選擇配方，再啟用連續製作。此模式會重複製作所選配方，並在開始前檢查食物與藥水效果。");

                ImGuiEx.Text($"配方：{RecipeName} {(RecipeID != 0 ? $"({LuminaSheets.ClassJobSheet[LuminaSheets.RecipeSheet[RecipeID].CraftType.RowId + 8].Abbreviation})" : "")}");
            }

            bool repairs = P.Config.Repair;
            if (ImGui.Checkbox("自動修理裝備", ref repairs))
            {
                P.Config.Repair = repairs;
                P.Config.Save();
            }
            ImGuiComponents.HelpMarker($"啟用後，任一裝備耐久度低於設定門檻時會自動修理。\n\n目前最低耐久度為 {RepairManager.GetMinEquippedPercent()}%，NPC 修理費用約 {RepairManager.GetNPCRepairPrice()} Gil。\n\n無法使用暗物質自行修理時，會嘗試尋找附近的修理 NPC。");
            if (P.Config.Repair)
            {
                //ImGui.SameLine();
                ImGui.PushItemWidth(200);
                int percent = P.Config.RepairPercent;
                if (ImGui.SliderInt("##repairp", ref percent, 10, 100, $"%d%%"))
                {
                    P.Config.RepairPercent = percent;
                    P.Config.Save();
                }
            }

            if (!CharacterInfo.MateriaExtractionUnlocked())
                ImGui.BeginDisabled();

            bool materia = P.Config.Materia;
            if (ImGui.Checkbox("自動提取魔晶石", ref materia))
            {
                P.Config.Materia = materia;
                P.Config.Save();
            }

            if (!CharacterInfo.MateriaExtractionUnlocked())
            {
                ImGui.EndDisabled();

                ImGuiComponents.HelpMarker("此角色尚未解鎖魔晶石提取功能，因此會忽略這項設定。");
            }
            else
                ImGuiComponents.HelpMarker("裝備精製度達到 100% 時，自動提取魔晶石。");

            ImGui.Checkbox("只製作指定次數", ref P.Config.CraftingX);
            if (P.Config.CraftingX)
            {
                ImGui.Text("製作次數：");
                ImGui.SameLine();
                ImGui.PushItemWidth(200);
                if (ImGui.InputInt("###TimesRepeat", ref P.Config.CraftX))
                {
                    if (P.Config.CraftX < 0)
                        P.Config.CraftX = 0;
                }
            }

            if (ImGui.Checkbox("可用時使用簡易製作", ref P.Config.QuickSynthMode))
            {
                P.Config.Save();
            }

            bool stopIfFail = P.Config.EnduranceStopFail;
            if (ImGui.Checkbox("製作失敗時停止連續製作", ref stopIfFail))
            {
                P.Config.EnduranceStopFail = stopIfFail;
                P.Config.Save();
            }

            bool stopIfNQ = P.Config.EnduranceStopNQ;
            if (ImGui.Checkbox("製作出普通品質物品時停止", ref stopIfNQ))
            {
                P.Config.EnduranceStopNQ = stopIfNQ;
                P.Config.Save();
            }

            if (ImGui.Checkbox("最大可製作數量模式", ref P.Config.MaxQuantityMode))
            {
                P.Config.Save();
            }

            ImGuiComponents.HelpMarker("自動設定目前配方的材料數量，儘可能製作最多次數。");
        }

        internal static void DrawRecipeData()
        {
            var curRec = Operations.GetSelectedRecipeEntry();
            if (curRec is null || curRec->RecipeId == 0)
                return;

            RecipeID = curRec->RecipeId;
            try
            {
                for (int i = 0; i < curRec->IngredientsSpan.Length; i++)
                {
                    var ing = curRec->IngredientsSpan[i];
                    if (ing.ItemId == 0)
                        break;
                    var nq = ing.NumAssignedNQ;
                    var hq = ing.NumAssignedHQ;

                    SetIngredients[i] = new EnduranceIngredients()
                    {
                        NQSet = nq,
                        HQSet = hq,
                    };

                    //Svc.Log.Debug($"Assigned {nq}NQ, {hq}HQ {ing.ItemId.NameOfItem()}");
                }
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, "Setting Recipe ID");
                RecipeID = 0;
            }


        }

        internal static void Init()
        {
            Svc.Toasts.ErrorToast += Toasts_ErrorToast;
            Svc.Toasts.ErrorToast += CheckNonMaxQuantityModeFinished;
        }

        private static bool enable = false;
        private static void CheckNonMaxQuantityModeFinished(ref SeString message, ref bool isHandled)
        {
            if (!P.Config.MaxQuantityMode && Enable &&
                (message.GetText() == Svc.Data.GetExcelSheet<LogMessage>()?.First(x => x.RowId == 1147).Text.GetText() ||
                 message.GetText() == Svc.Data.GetExcelSheet<LogMessage>()?.First(x => x.RowId == 1146).Text.GetText() ||
                 message.GetText() == Svc.Data.GetExcelSheet<LogMessage>()?.First(x => x.RowId == 1145).Text.GetText() ||
                 message.GetText() == Svc.Data.GetExcelSheet<LogMessage>()?.First(x => x.RowId == 1144).Text.GetText()))
            {
                if (P.Config.PlaySoundFinishEndurance)
                    SoundPlayer.PlaySound();

                ToggleEndurance(false);
            }
        }

        public static void Update()
        {
            if (!Enable) return;
            var needToRepair = P.Config.Repair && RepairManager.GetMinEquippedPercent() < P.Config.RepairPercent && (RepairManager.CanRepairAny() || RepairManager.RepairNPCNearby(out _));
            if ((Crafting.CurState == Crafting.State.QuickCraft && Crafting.QuickSynthCompleted) || needToRepair ||
                (P.Config.Materia && Spiritbond.IsSpiritbondReadyAny() && CharacterInfo.MateriaExtractionUnlocked()))
            {
                Operations.CloseQuickSynthWindow();
            }

            if (!P.TM.IsBusy && Crafting.CurState is Crafting.State.IdleNormal or Crafting.State.IdleBetween)
            {
                var isCrafting = Svc.Condition[ConditionFlag.Crafting];
                var preparing = Svc.Condition[ConditionFlag.PreparingToCraft];
                var recipe = LuminaSheets.RecipeSheet[RecipeID];
                if (PreCrafting.Tasks.Count > 0)
                {
                    return;
                }

                if (P.Config.CraftingX && P.Config.CraftX == 0 || PreCrafting.GetNumberCraftable(recipe) == 0)
                {
                    ToggleEndurance(false);
                    P.Config.CraftingX = false;
                    DuoLog.Information("Craft X has completed.");
                    if (P.Config.PlaySoundFinishEndurance)
                        SoundPlayer.PlaySound();

                    return;
                }

                if (RecipeID == 0)
                {
                    Svc.Toasts.ShowError("尚未指定連續製作配方，已停用連續製作模式。");
                    DuoLog.Error("尚未指定連續製作配方，已停用連續製作模式。");
                    ToggleEndurance(false);
                    return;
                }

                if ((Job)LuminaSheets.RecipeSheet[RecipeID].CraftType.RowId + 8 != CharacterInfo.JobID)
                {
                    PreCrafting.equipGearsetLoops = 0;
                    PreCrafting.Tasks.Add((() => PreCrafting.TaskExitCraft(), TimeSpan.FromMilliseconds(200)));
                    PreCrafting.Tasks.Add((() => PreCrafting.TaskClassChange((Job)LuminaSheets.RecipeSheet[RecipeID].CraftType.RowId + 8), TimeSpan.FromMilliseconds(200)));
                    return;
                }

                bool needEquipItem = recipe.ItemRequired.RowId > 0 && !PreCrafting.IsItemEquipped(recipe.ItemRequired.RowId);
                if (needEquipItem)
                {
                    PreCrafting.equipAttemptLoops = 0;
                    PreCrafting.Tasks.Add((() => PreCrafting.TaskEquipItem(recipe.ItemRequired.RowId), TimeSpan.FromMilliseconds(200)));
                    return;
                }

                if (!Spiritbond.ExtractMateriaTask(P.Config.Materia))
                {
                    PreCrafting.Tasks.Add((() => PreCrafting.TaskExitCraft(), TimeSpan.FromMilliseconds(200)));
                    return;
                }

                if (P.Config.Repair && !RepairManager.ProcessRepair())
                {
                    PreCrafting.Tasks.Add((() => PreCrafting.TaskExitCraft(), TimeSpan.FromMilliseconds(200)));
                    return;
                }

                var config = P.Config.RecipeConfigs.GetValueOrDefault(RecipeID) ?? new();
                PreCrafting.CraftType type = P.Config.QuickSynthMode && recipe.CanQuickSynth && P.ri.HasRecipeCrafted(recipe.RowId) ? PreCrafting.CraftType.Quick : PreCrafting.CraftType.Normal;
                bool needConsumables = PreCrafting.NeedsConsumablesCheck(type, config);
                bool hasConsumables = PreCrafting.HasConsumablesCheck(config);

                if (P.Config.AbortIfNoFoodPot && needConsumables && !hasConsumables)
                {
                    PreCrafting.MissingConsumablesMessage(recipe, config);
                    ToggleEndurance(false);
                    return;
                }

                bool needFood = config != default && ConsumableChecker.HasItem(config.RequiredFood, config.RequiredFoodHQ) && !ConsumableChecker.IsFooded(config);
                bool needPot = config != default && ConsumableChecker.HasItem(config.RequiredPotion, config.RequiredPotionHQ) && !ConsumableChecker.IsPotted(config);
                bool needManual = config != default && ConsumableChecker.HasItem(config.RequiredManual, false) && !ConsumableChecker.IsManualled(config);
                bool needSquadronManual = config != default && ConsumableChecker.HasItem(config.RequiredSquadronManual, false) && !ConsumableChecker.IsSquadronManualled(config);

                if (needFood || needPot || needManual || needSquadronManual)
                {
                    if (!P.TM.IsBusy && !PreCrafting.Occupied())
                    {
                        P.TM.Enqueue(() => PreCrafting.Tasks.Add((() => PreCrafting.TaskExitCraft(), TimeSpan.FromMilliseconds(200))));
                        P.TM.Enqueue(() => PreCrafting.Tasks.Add((() => PreCrafting.TaskUseConsumables(config, type), TimeSpan.FromMilliseconds(200))));
                        P.TM.DelayNext(100);
                    }
                    return;
                }

                if (Crafting.CurState is Crafting.State.IdleBetween or Crafting.State.IdleNormal && !PreCrafting.Occupied())
                {
                    if (!P.TM.IsBusy)
                    {
                        PreCrafting.Tasks.Add((() => PreCrafting.TaskSelectRecipe(recipe), TimeSpan.FromMilliseconds(500)));

                        if (!CraftingListFunctions.RecipeWindowOpen() && !CraftingListFunctions.CosmicLogOpen()) return;

                        if (type == PreCrafting.CraftType.Quick)
                        {
                            P.TM.Enqueue(() => Operations.QuickSynthItem(P.Config.CraftingX ? P.Config.CraftX : 99), "EnduranceQSStart");
                            P.TM.Enqueue(() => Crafting.CurState is Crafting.State.WaitStart, 5000, "EnduranceQSWaitStart");
                        }
                        else if (type == PreCrafting.CraftType.Normal)
                        {
                            P.TM.DelayNext(200);

                            if (P.Config.MaxQuantityMode)
                                P.TM.Enqueue(() => CraftingListFunctions.SetIngredients(), "EnduranceSetIngredientsNonLayout");
                            else
                                P.TM.Enqueue(() => CraftingListFunctions.SetIngredients(SetIngredients), "EnduranceSetIngredientsLayout");

                            P.TM.Enqueue(() => Operations.RepeatActualCraft(), 500, "EnduranceNormalStart");
                            P.TM.Enqueue(() => Crafting.CurState is Crafting.State.WaitStart, 500, "EnduranceNormalWaitStart");
                            P.TM.Enqueue(() =>
                            {
                                if (!RaphaelCache.InProgressAny())
                                {
                                    if (FailedStarts.Count() >= 5 && FailedStarts.All(x => x > Environment.TickCount64 - (10 * 1000)))
                                    {
                                        FailedStarts.Clear();
                                        if (Crafting.CurState is not Crafting.State.QuickCraft and not Crafting.State.InProgress and not Crafting.State.WaitStart)
                                        {
                                            if (!IPCOverride)
                                            {
                                                DuoLog.Error($"無法開始製作，已停用連續製作。{(!P.Config.MaxQuantityMode ? "請啟用最大可製作數量模式，或先設定要使用的材料。" : "")}");
                                            }
                                            else
                                            {
                                                DuoLog.Error($"其他插件控制 Artisan 時發生錯誤，已停用連續製作。");
                                            }
                                            ToggleEndurance(false);
                                        }
                                    }
                                    else
                                    {
                                        FailedStarts.PushBack(Environment.TickCount64);
                                    }
                                }
                            });

                        }
                    }

                }
            }
        }

        private static void Toasts_ErrorToast(ref SeString message, ref bool isHandled)
        {
            if (Enable || (CraftingListUI.Processing && !CraftingListFunctions.Paused))
            {
                //foreach (uint errorId in UnableToCraftErrors)
                //{
                //    if (message.GetText() == Svc.Data.GetExcelSheet<LogMessage>()?.First(x => x.RowId == errorId).Text.GetText())
                //    {
                //        Svc.Toasts.ShowError($"Current crafting mode has been {(Enable ? "disabled" : "paused")} due to unable to craft error.");
                //        DuoLog.Error($"Current crafting mode has been {(Enable ? "disabled" : "paused")} due to unable to craft error.");
                //        if (enable)
                //            ToggleEndurance(false);
                //        if (CraftingListUI.Processing)
                //            CraftingListFunctions.Paused = true;
                //        PreCrafting.Tasks.Add((() => PreCrafting.TaskExitCraft(), default));

                //        P.TM.Abort();
                //        CraftingListFunctions.CLTM.Abort();
                //    }
                //}

                Errors.PushBack(Environment.TickCount64);
                Svc.Log.Warning($"Error Warnings [{Errors.Count(x => x > Environment.TickCount64 - 10 * 1000)}]: {message}");
                if (Errors.Count() >= 5 && Errors.All(x => x > Environment.TickCount64 - 10 * 1000))
                {
                    Svc.Toasts.ShowError($"連續發生太多錯誤，目前製作模式已{(Enable ? "停用" : "暫停")}。");
                    DuoLog.Error($"連續發生太多錯誤，目前製作模式已{(Enable ? "停用" : "暫停")}。");
                    if (enable)
                        ToggleEndurance(false);
                    if (CraftingListUI.Processing)
                        CraftingListFunctions.Paused = true;
                    Errors.Clear();
                    PreCrafting.Tasks.Add((() => PreCrafting.TaskExitCraft(), default));

                    P.TM.Abort();
                    CraftingListFunctions.CLTM.Abort();
                }
            }
        }
    }
}
