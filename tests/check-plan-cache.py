#!/usr/bin/env python3
"""Run the production snapshot cache without Dalamud/Wine.

Lumina handles are type-only stubs: the cache deliberately removes them from
its keys. The real CraftState, StepState, enums and cache source are compiled.
This checks orchestration, not the game's action or ingredient calculations.
"""
from pathlib import Path
import os
import subprocess
import tempfile

root = Path(__file__).resolve().parents[1] / 'Artisan'
with tempfile.TemporaryDirectory(prefix='artisan-cache-regression-') as temp:
    out = Path(temp)
    (out / 'Check.csproj').write_text('''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net9.0</TargetFramework>
<Nullable>enable</Nullable></PropertyGroup></Project>''')
    for source in ('CraftingLogic/State.cs', 'CraftingLogic/ValidatedCraftPlanCache.cs', 'CraftingLogic/CraftPlanPreference.cs', 'CraftingLogic/CraftData/Condition.cs'):
        (out / Path(source).name).write_bytes((root / source).read_bytes())
    skills = (root / 'RawInformation/Character/Skills.cs').read_text(encoding='utf-8-sig')
    enum = skills[skills.index('    public enum Skills'):skills.index('    public static class SkillActionMap')]
    (out / 'Types.cs').write_text('namespace Artisan.RawInformation.Character {\n' + enum + '''
}
namespace FFXIVClientStructs { }
namespace Lumina.Excel.Sheets { public struct Recipe { } public struct RecipeLevelTable { } }
''')
    (out / 'Program.cs').write_text('''
using System;
using Artisan.CraftingLogic;
using Artisan.CraftingLogic.CraftData;
using Artisan.RawInformation.Character;
class Program {
    static int checks;
    static void Check(bool value, string name) {
        if (!value) throw new Exception(name);
        checks++;
    }
    static void Main() {
        var craft = new CraftState { RecipeId=35829, InitialQuality=6000, StatCP=708,
            StatControl=5292, StatCraftsmanship=5408, ConditionFlags=ConditionFlags.Normal,
            CraftConditionProbabilities=new float[] {1, 0.25f}, CraftProgress=6600 };
        var step = new StepState { Index=1, Quality=6000, Durability=80, RemainingCP=708 };
        var cache = new ValidatedCraftPlanCache(2);
        cache.Store(craft, 11400, new[]{Skills.BasicTouch}, new[]{step});
        Check(cache.TryGetAction(craft,11400,step,out var action,out _) && action==Skills.BasicTouch,"initial hit");
        var same = craft with { CraftConditionProbabilities=new float[]{1,0.25f} };
        Check(cache.Contains(same,11400,out _),"array identity must not break input matching");
        Check(!cache.Contains(craft with {InitialQuality=0},11400,out _),"NQ must not use HQ plan");
        Check(!cache.Contains(craft with {StatCP=707},11400,out _),"food or CP change");
        Check(!cache.Contains(craft with {StatControl=5293},11400,out _),"gear change");
        Check(!cache.Contains(craft,12000,out _),"target change");
        Check(!cache.TryGetAction(craft,11400,step with {RemainingCP=690},out _,out _),"CP divergence");
        Check(!cache.TryGetAction(craft,11400,step with {PrevComboAction=Skills.Observe},out _,out _),"manual action divergence");
        Check(!cache.TryGetAction(craft,11400,step with {Condition=Condition.Poor},out _,out _),"condition divergence");
        var later = step with {PrevComboAction=Skills.HeartAndSoul,HeartAndSoulActive=true};
        cache.Store(craft,11400,new[]{Skills.HeartAndSoul,Skills.BasicTouch},new[]{step,later});
        Check(cache.TryGetAction(craft,11400,later,out action,out _) && action==Skills.BasicTouch,"same step index specialist action");
        for(int i=0;i<999;i++)
            if(!cache.TryGetAction(same,11400,later,out _,out _)) throw new Exception("bulk reuse");
        Check(true,"999 repeat lookups");
        later.RemainingCP=0;
        Check(cache.TryGetAction(craft,11400,step with {PrevComboAction=Skills.HeartAndSoul,HeartAndSoulActive=true},out _,out _),"snapshot detached");
        var nq=craft with {InitialQuality=0};
        cache.Store(nq,11400,new[]{Skills.BasicTouch},new[]{step with {Quality=0}});
        Check(cache.Contains(craft,11400,out _) && cache.Contains(nq,11400,out _),"HQ and NQ coexist");
        Check(cache.TryGetPlan(craft,11400,out var rotation) && rotation.Count==2,"complete plan reused without search");
        ((Skills[])rotation)[0]=Skills.None;
        Check(cache.TryGetPlan(craft,11400,out rotation) && rotation[0]==Skills.HeartAndSoul,"returned actions detached");
        cache.Store(craft with {RecipeId=123},11400,new[]{Skills.BasicTouch},new[]{step});
        Check(!cache.Contains(craft,11400,out _) && cache.Contains(nq,11400,out _),"bounded FIFO eviction");
        var baseline=new CraftPlanScore(true,12000,true,32);
        Check(!new CraftPlanScore(true,10829,true,20).Improves(baseline,11400),"failed random retry cannot replace success");
        Check(!new CraftPlanScore(false,12000,true,20).Improves(baseline,11400),"unfinished plan cannot replace success");
        Check(!new CraftPlanScore(true,12000,false,20).Improves(baseline,11400),"risky skills cannot replace guaranteed success");
        Check(new CraftPlanScore(true,12000,true,28).Improves(baseline,11400),"same quality fewer steps wins");
        Check(!new CraftPlanScore(true,11400,true,28).Improves(baseline,11400),"fewer steps cannot silently reduce quality");
        Check(baseline.Improves(new(true,10829,true,20),11400),"bounded solver can rescue failed baseline");
        Check(baseline.Improves(new(true,12000,false,20),11400),"guaranteed solution improves optimistic estimate");
        Console.WriteLine($"Plan cache regression: {checks} passed (production source; no game runtime)");
    }
}
''')
    subprocess.run(['dotnet', 'run', '--project', str(out / 'Check.csproj'), '-c', 'Release'],
                   check=True, env={**os.environ, 'DOTNET_ROLL_FORWARD': 'Major'})
