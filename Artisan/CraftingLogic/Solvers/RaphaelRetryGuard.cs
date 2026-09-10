using System.Collections.Concurrent;

namespace Artisan.CraftingLogic.Solvers;

// Session-only failure memory. Include solver inputs absent from the solution cache key.
internal readonly record struct RaphaelAttemptSettings(uint RecipeId, int Level, bool Manipulation,
    bool Reliability, bool Backload, bool HeartAndSoul, bool QuickInnovation, int Threads, int TimeoutMinutes);

internal sealed class RaphaelRetryGuard
{
    private readonly ConcurrentDictionary<string, RaphaelAttemptSettings> attempts = new();
    public bool ShouldSkip(string key, RaphaelAttemptSettings settings, bool automatic) =>
        automatic && attempts.TryGetValue(key, out var previous) && previous == settings;
    public void RecordAttempt(string key, RaphaelAttemptSettings settings) => attempts[key] = settings;
    public void RecordSuccess(string key) => attempts.TryRemove(key, out _);
}
