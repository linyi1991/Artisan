using Artisan.CraftingLogic.CraftData;
using Artisan.RawInformation;
using Artisan.RawInformation.Character;
using Dalamud.Interface.Components;
using Dalamud.Interface.Textures.TextureWraps;
using ECommons.DalamudServices;
using ECommons.ImGuiMethods;
using Dalamud.Bindings.ImGui;
using System;
using static Artisan.RawInformation.AddonExtensions;
using System.Numerics;

namespace Artisan.CraftingLogic.Solvers;

public class ExpertSolverSettings
{
    public bool MaxIshgardRecipes;
    public bool UseReflectOpener;
    public bool MuMeIntensiveGood = true; // if true, we allow spending mume on intensive (400p) rather than rapid (500p) if good condition procs
    public bool MuMeIntensiveMalleable = false; // if true and we have malleable during mume, use intensive rather than hoping for rapid
    public bool MuMeIntensiveLastResort = true; // if true and we're on last step of mume, use intensive (forcing via H&S if needed) rather than hoping for rapid (unless we have centered)
    public bool MuMePrimedManip = false; // if true, allow using primed manipulation after veneration is up on mume
    public bool MuMeAllowObserve = false; // if true, observe rather than use actions during unfavourable conditions to conserve durability
    public int MuMeMinStepsForManip = 2; // if this or less rounds are remaining on mume, don't use manipulation under favourable conditions
    public int MuMeMinStepsForVene = 1; // if this or less rounds are remaining on mume, don't use veneration
    public int MidMinIQForHSPrecise = 10; // min iq stacks where we use h&s+precise; 10 to disable
    public bool MidBaitPliantWithObservePreQuality = true; // if true, when very low on durability and without manip active during pre-quality phase, we use observe rather than normal manip
    public bool MidBaitPliantWithObserveAfterIQ = true; // if true, when very low on durability and without manip active after iq has 10 stacks, we use observe rather than normal manip or inno+finnesse
    public bool MidPrimedManipPreQuality = true; // if true, allow using primed manipulation during pre-quality phase
    public bool MidPrimedManipAfterIQ = true; // if true, allow using primed manipulation during after iq has 10 stacks
    public bool MidKeepHighDuraUnbuffed = true; // if true, observe rather than use actions during unfavourable conditions to conserve durability when no buffs are active
    public bool MidKeepHighDuraVeneration = false; // if true, observe rather than use actions during unfavourable conditions to conserve durability when veneration is active
    public bool MidAllowVenerationGoodOmen = true; // if true, we allow using veneration during iq phase if we lack a lot of progress on good omen
    public bool MidAllowVenerationAfterIQ = true; // if true, we allow using veneration after iq is fully stacked if we still lack a lot of progress
    public bool MidAllowIntensiveUnbuffed = false; // if true, we allow spending good condition on intensive if we still need progress when no buffs are active
    public bool MidAllowIntensiveVeneration = false; // if true, we allow spending good condition on intensive if we still need progress when veneration is active
    public bool MidAllowPrecise = true; // if true, we allow spending good condition on precise touch if we still need iq
    public bool MidAllowSturdyPreсise = false; // if true,we consider sturdy+h&s+precise touch a good move for building iq
    public bool MidAllowCenteredHasty = true; // if true, we consider centered hasty touch a good move for building iq (85% reliability)
    public bool MidAllowSturdyHasty = true; // if true, we consider sturdy hasty touch a good move for building iq (50% reliability), otherwise we use combo
    public bool MidAllowGoodPrep = true; // if true, we consider prep touch a good move for finisher under good+inno+gs
    public bool MidAllowSturdyPrep = true; // if true, we consider prep touch a good move for finisher under sturdy+inno
    public bool MidGSBeforeInno = true; // if true, we start quality combos with gs+inno rather than just inno
    public bool MidFinishProgressBeforeQuality = false; // if true, at 10 iq we first finish progress before starting on quality
    public bool MidObserveGoodOmenForTricks = false; // if true, we'll observe on good omen where otherwise we'd use tricks on good
    public bool FinisherBaitGoodByregot = true; // if true, use careful observations to try baiting good byregot
    public bool EmergencyCPBaitGood = false; // if true, we allow spending careful observations to try baiting good for tricks when we really lack cp
    public bool UseMaterialMiracle = false;

    [NonSerialized]
    public IDalamudTextureWrap? expertIcon;

    public ExpertSolverSettings()
    {
        var tex = Svc.PluginInterface.UiBuilder.LoadUld("ui/uld/RecipeNoteBook.uld");
        expertIcon = tex.LoadTexturePart("ui/uld/RecipeNoteBook_hr1.tex", 14);
    }

    public bool Draw()
    {
        ImGui.TextWrapped($"專家配方求解器只用於專家配方，並不是普通配方求解器的替代品。");
        if (expertIcon != null)
        {
            ImGui.TextWrapped($"此求解器只適用於製作筆記中帶有");
            ImGui.SameLine();
            ImGui.Image(expertIcon.Handle, expertIcon.Size, new Vector2(0, 0), new Vector2(1, 1), new Vector4(0.94f, 0.57f, 0f, 1f));
            ImGui.SameLine();
            ImGui.TextWrapped($"圖示的配方。");
        }
        bool changed = false;
        ImGui.Indent();
        if (ImGui.CollapsingHeader("起手設定"))
        {
            changed |= ImGui.Checkbox($"起手改用{Skills.Reflect.NameOfAction()}，不使用{Skills.MuscleMemory.NameOfAction()}", ref UseReflectOpener);
            changed |= ImGui.Checkbox($"狀態為{Condition.Good.ToLocalizedString()}時，允許將{Skills.MuscleMemory.NameOfAction()}用於{Skills.IntensiveSynthesis.NameOfAction()} (400%)，而不是{Skills.RapidSynthesis.NameOfAction()} (500%)", ref MuMeIntensiveGood);
            changed |= ImGui.Checkbox($"{Skills.MuscleMemory.NameOfAction()}期間遇到{Condition.Malleable.ToLocalizedString()}{ConditionString}時，使用{Skills.HeartAndSoul.NameOfAction()} + {Skills.IntensiveSynthesis.NameOfAction()}", ref MuMeIntensiveMalleable);
            changed |= ImGui.Checkbox($"{Skills.MuscleMemory.NameOfAction()}剩最後一步且不是{Condition.Centered.ToLocalizedString()}{ConditionString}時，使用{Skills.IntensiveSynthesis.NameOfAction()}；必要時搭配{Skills.HeartAndSoul.NameOfAction()}", ref MuMeIntensiveLastResort);
            changed |= ImGui.Checkbox($"{Skills.Veneration.NameOfAction()}已生效且狀態為{Condition.Primed.ToLocalizedString()}時，使用{Skills.Manipulation.NameOfAction()}", ref MuMePrimedManip);
            changed |= ImGui.Checkbox($"狀態不利時使用{Skills.Observe.NameOfAction()}，避免以{Skills.RapidSynthesis.NameOfAction()}消耗{DurabilityString}", ref MuMeAllowObserve);
            ImGui.Text($"只有在{Skills.MuscleMemory.NameOfAction()}剩餘步數高於下列數值時，才使用{Skills.Manipulation.NameOfAction()}");
            ImGui.PushItemWidth(250);
            changed |= ImGui.SliderInt("###MumeMinStepsForManip", ref MuMeMinStepsForManip, 0, 5);
            ImGui.Text($"只有在{Skills.MuscleMemory.NameOfAction()}剩餘步數高於下列數值時，才使用{Skills.Veneration.NameOfAction()}");
            ImGui.PushItemWidth(250);
            changed |= ImGui.SliderInt("###MuMeMinStepsForVene", ref MuMeMinStepsForVene, 0, 5);
        }
        if (ImGui.CollapsingHeader("主要循環設定"))
        {
            ImGui.Text($"使用{Skills.HeartAndSoul.NameOfAction()} + {Skills.PreciseTouch.NameOfAction()}所需的最低{Buffs.InnerQuiet.NameOfBuff()}層數 (10 代表停用)");
            ImGui.PushItemWidth(250);
            changed |= ImGui.SliderInt($"###MidMinIQForHSPrecise", ref MidMinIQForHSPrecise, 0, 10);
            changed |= ImGui.Checkbox($"{DurabilityString}偏低且{Buffs.InnerQuiet.NameOfBuff()}未滿 10 層時，優先用{Skills.Observe.NameOfAction()}等待{Condition.Pliant.ToLocalizedString()}，避免直接使用{Skills.Manipulation.NameOfAction()}", ref MidBaitPliantWithObservePreQuality);
            changed |= ImGui.Checkbox($"{DurabilityString}偏低且{Buffs.InnerQuiet.NameOfBuff()}已滿 10 層時，優先用{Skills.Observe.NameOfAction()}等待{Condition.Pliant.ToLocalizedString()}，避免直接使用{Skills.Manipulation.NameOfAction()}或{Skills.Innovation.NameOfAction()} + {Skills.TrainedFinesse.NameOfAction()}", ref MidBaitPliantWithObserveAfterIQ);
            changed |= ImGui.Checkbox($"{Buffs.InnerQuiet.NameOfBuff()}未滿 10 層且狀態為{Condition.Primed.ToLocalizedString()}時，使用{Skills.Manipulation.NameOfAction()}", ref MidPrimedManipPreQuality);
            changed |= ImGui.Checkbox($"{Buffs.InnerQuiet.NameOfBuff()}已滿 10 層且 CP 足夠時，在{Condition.Primed.ToLocalizedString()}{ConditionString}使用{Skills.Manipulation.NameOfAction()}", ref MidPrimedManipAfterIQ);
            changed |= ImGui.Checkbox($"沒有增益時，允許在不利{ConditionString}使用{Skills.Observe.NameOfAction()}", ref MidKeepHighDuraUnbuffed);
            changed |= ImGui.Checkbox($"{Buffs.Veneration.NameOfBuff()}期間，允許在不利{ConditionString}使用{Skills.Observe.NameOfAction()}", ref MidKeepHighDuraVeneration);
            changed |= ImGui.Checkbox($"遇到{Condition.GoodOmen.ToLocalizedString()}且仍欠缺大量{ProgressString}時，允許使用{Skills.Veneration.NameOfAction()}", ref MidAllowVenerationGoodOmen);
            changed |= ImGui.Checkbox($"{Buffs.InnerQuiet.NameOfBuff()}已滿 10 層且仍欠缺大量{ProgressString}時，允許使用{Skills.Veneration.NameOfAction()}", ref MidAllowVenerationAfterIQ);
            changed |= ImGui.Checkbox($"沒有增益且需要更多{ProgressString}時，將{Condition.Good.ToLocalizedString()}{ConditionString}用於{Skills.IntensiveSynthesis.NameOfAction()}", ref MidAllowIntensiveUnbuffed);
            changed |= ImGui.Checkbox($"{Skills.Veneration.NameOfAction()}期間需要更多{ProgressString}時，將{Condition.Good.ToLocalizedString()}{ConditionString}用於{Skills.IntensiveSynthesis.NameOfAction()}", ref MidAllowIntensiveVeneration);
            changed |= ImGui.Checkbox($"需要更多{Buffs.InnerQuiet.NameOfBuff()}層數時，將{Condition.Good.ToLocalizedString()}{ConditionString}用於{Skills.PreciseTouch.NameOfAction()}", ref MidAllowPrecise);
            changed |= ImGui.Checkbox($"將{Condition.Sturdy.ToLocalizedString()}{ConditionString}下的{Skills.HeartAndSoul.NameOfAction()} + {Skills.PreciseTouch.NameOfAction()}視為累積{Buffs.InnerQuiet.NameOfBuff()}的有效選擇", ref MidAllowSturdyPreсise);
            changed |= ImGui.Checkbox($"將{Condition.Centered.ToLocalizedString()}{ConditionString}下的{Skills.HastyTouch.NameOfAction()}視為累積{Buffs.InnerQuiet.NameOfBuff()}的有效選擇 (成功率 85%，消耗 10 {DurabilityString})", ref MidAllowCenteredHasty);
            changed |= ImGui.Checkbox($"將{Condition.Sturdy.ToLocalizedString()}{ConditionString}下的{Skills.HastyTouch.NameOfAction()}視為累積{Buffs.InnerQuiet.NameOfBuff()}的有效選擇 (成功率 50%，消耗 5 {DurabilityString})", ref MidAllowSturdyHasty);
            changed |= ImGui.Checkbox($"{DurabilityString}足夠時，將{Condition.Good.ToLocalizedString()}{ConditionString} + {Buffs.Innovation.NameOfBuff()} + {Buffs.GreatStrides.NameOfBuff()}下的{Skills.PreparatoryTouch.NameOfAction()}視為有效選擇", ref MidAllowGoodPrep);
            changed |= ImGui.Checkbox($"{DurabilityString}足夠時，將{Condition.Sturdy.ToLocalizedString()}{ConditionString} + {Buffs.Innovation.NameOfBuff()}下的{Skills.PreparatoryTouch.NameOfAction()}視為有效選擇", ref MidAllowSturdyPrep);
            changed |= ImGui.Checkbox($"在{Skills.Innovation.NameOfAction()} + {QualityString}連段前先使用{Skills.GreatStrides.NameOfAction()}", ref MidGSBeforeInno);
            changed |= ImGui.Checkbox($"先完成{ProgressString}，再進入{QualityString}階段", ref MidFinishProgressBeforeQuality);
            changed |= ImGui.Checkbox($"遇到{Condition.GoodOmen.ToLocalizedString()}{ConditionString}時使用{Skills.Observe.NameOfAction()}，等待後續以{Condition.Good.ToLocalizedString()}{ConditionString}使用{Skills.TricksOfTrade.NameOfAction()}", ref MidObserveGoodOmenForTricks);
        }
        ImGui.Unindent();
        changed |= ImGui.Checkbox("伊修加德復興配方儘量達到最高品質，而不是只達到最高檔位", ref MaxIshgardRecipes);
        ImGuiComponents.HelpMarker("儘量提高品質，以取得更多天穹積分。");
        changed |= ImGui.Checkbox($"收尾：使用{Skills.CarefulObservation.NameOfAction()}等待{Condition.Good.ToLocalizedString()}{ConditionString}，再使用{Skills.ByregotsBlessing.NameOfAction()}", ref FinisherBaitGoodByregot);
        changed |= ImGui.Checkbox($"緊急回 CP：CP 很低時使用{Skills.CarefulObservation.NameOfAction()}等待{Condition.Good.ToLocalizedString()}{ConditionString}，再使用{Skills.TricksOfTrade.NameOfAction()}", ref EmergencyCPBaitGood);
        changed |= ImGui.Checkbox($"宇宙探索中使用材料奇蹟", ref UseMaterialMiracle);
        if (ImGuiEx.ButtonCtrl("重設專家求解器設定 (按住 CTRL)"))
        {
            P.Config.ExpertSolverConfig = new();
            changed |= true;
        }
        return changed;
    }
}
