using System;
using System.Collections.Generic;
using System.Linq;
using Skills = Artisan.RawInformation.Character.Skills;

namespace Artisan.CraftingLogic;

// Stores detached snapshots only. Recipe sheet handles and array identity are
// not simulation inputs; all scalar fields (including InitialQuality) are.
internal sealed class ValidatedCraftPlanCache(int capacity)
{
    private sealed record Key(CraftState Craft, int TargetQuality, string Conditions);
    private sealed record Plan(Skills[] Actions, StepState[] Before);
    private readonly object sync = new();
    private readonly Dictionary<Key, Plan> plans = new();
    private readonly Queue<Key> order = new();

    private static Key CreateKey(CraftState craft, int targetQuality) => new(
        craft with { Recipe = default, LevelTable = default, CraftConditionProbabilities = Array.Empty<float>() },
        targetQuality, string.Join(",", craft.CraftConditionProbabilities));

    public static bool InputsMatch(CraftState left, int leftTarget, CraftState right, int rightTarget)
        => CreateKey(left, leftTarget) == CreateKey(right, rightTarget);

    public void Store(CraftState craft, int targetQuality, IReadOnlyList<Skills> actions, IReadOnlyList<StepState> before)
    {
        if (capacity <= 0 || actions.Count == 0 || actions.Count > 128 || actions.Count != before.Count)
            throw new ArgumentException("A validated plan must have one snapshot per action, at most 128 actions.");
        var key = CreateKey(craft, targetQuality);
        var plan = new Plan(actions.ToArray(), before.Select(s => s with { }).ToArray());
        lock (sync)
        {
            if (!plans.ContainsKey(key))
                order.Enqueue(key);
            plans[key] = plan;
            while (plans.Count > capacity && order.TryDequeue(out var oldest))
                plans.Remove(oldest);
        }
    }

    public bool Contains(CraftState craft, int targetQuality, out int count)
    {
        lock (sync)
        {
            count = plans.TryGetValue(CreateKey(craft, targetQuality), out var plan) ? plan.Actions.Length : 0;
            return count > 0;
        }
    }

    public bool TryGetPlan(CraftState craft, int targetQuality, out IReadOnlyList<Skills> actions)
    {
        lock (sync)
        {
            if (plans.TryGetValue(CreateKey(craft, targetQuality), out var plan))
            {
                actions = plan.Actions.ToArray();
                return true;
            }
            actions = Array.Empty<Skills>();
            return false;
        }
    }

    public bool TryGetAction(CraftState craft, int targetQuality, StepState step, out Skills action, out int count)
    {
        action = Skills.None;
        count = 0;
        lock (sync)
        {
            if (!plans.TryGetValue(CreateKey(craft, targetQuality), out var plan))
                return false;
            count = plan.Actions.Length;
            // Specialist actions can share a step index. Match the full state,
            // not index - 1: CP, durability, buffs, previous action and quality
            // must still agree with the successful simulation.
            for (var i = 0; i < plan.Before.Length; i++)
                if (plan.Before[i] == step)
                {
                    action = plan.Actions[i];
                    return true;
                }
        }
        return false;
    }
}
