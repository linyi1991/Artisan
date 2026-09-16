namespace Artisan.CraftingLogic;

// Compare verified results, never the solver's name or its optimistic score.
internal readonly record struct CraftPlanScore(bool Completed, int Quality, bool Guaranteed, int Actions)
{
    public bool Improves(CraftPlanScore baseline, int targetQuality)
    {
        var passes = Completed && Quality >= targetQuality;
        var baselinePasses = baseline.Completed && baseline.Quality >= targetQuality;
        if (passes != baselinePasses) return passes;
        if (Completed != baseline.Completed) return Completed;
        if (Guaranteed != baseline.Guaranteed) return Guaranteed;
        if (Quality != baseline.Quality) return Quality > baseline.Quality;
        return Actions < baseline.Actions;
    }
}
