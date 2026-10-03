using ReSharder.Operator.Entities;

namespace ReSharder.Operator.Services;

/// <summary>
/// Pure logic for deciding which shards to move during a split.
/// Stateless -- all state comes in via parameters.
/// </summary>
public static class ShardSplitPlanner
{
    /// <summary>
    /// Determines which shards should be migrated from the overloaded instance.
    /// Returns exactly half the shards (rounded down), selecting the lexicographically
    /// last half for deterministic, reproducible splits.
    /// </summary>
    public static SplitPlan PlanSplit(
        string overloadedInstance,
        Dictionary<string, string> shardMapping,
        string newInstanceName)
    {
        var shardsOnInstance = shardMapping
            .Where(kv => kv.Key is not null && kv.Value == overloadedInstance)
            .Select(kv => kv.Key)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        if (shardsOnInstance.Count <= 1)
        {
            return SplitPlan.ScaleUp(overloadedInstance);
        }

        var halfCount = shardsOnInstance.Count / 2;
        var shardsToMove = shardsOnInstance
            .Skip(shardsOnInstance.Count - halfCount)
            .ToList();
        var shardsToKeep = shardsOnInstance
            .Take(shardsOnInstance.Count - halfCount)
            .ToList();

        return SplitPlan.Split(
            overloadedInstance,
            newInstanceName,
            shardsToKeep,
            shardsToMove);
    }

    /// <summary>
    /// Generates a deterministic name for the next CNPG instance.
    /// Finds the highest existing instance number and increments.
    /// </summary>
    public static string GenerateNextInstanceName(
        string smdName,
        IEnumerable<string> existingInstances)
    {
        const string prefix = "smd-instance-";
        var fullPrefix = $"{prefix}{smdName}-";

        var maxIndex = existingInstances
            .Where(name => name.StartsWith(fullPrefix, StringComparison.Ordinal))
            .Select(name =>
            {
                var suffix = name[fullPrefix.Length..];
                return int.TryParse(suffix, out var idx) ? idx : 0;
            })
            .DefaultIfEmpty(0)
            .Max();

        return $"{fullPrefix}{maxIndex + 1}";
    }
}

/// <summary>
/// Result of the split planning phase.
/// </summary>
public sealed record SplitPlan
{
    public required SplitAction Action { get; init; }
    public required string SourceInstance { get; init; }
    public string? TargetInstance { get; init; }
    public IReadOnlyList<string> ShardsToKeep { get; init; } = [];
    public IReadOnlyList<string> ShardsToMove { get; init; } = [];

    public static SplitPlan ScaleUp(string instance) => new()
    {
        Action = SplitAction.ScaleUp,
        SourceInstance = instance,
    };

    public static SplitPlan Split(
        string source,
        string target,
        IReadOnlyList<string> keep,
        IReadOnlyList<string> move) => new()
    {
        Action = SplitAction.Split,
        SourceInstance = source,
        TargetInstance = target,
        ShardsToKeep = keep,
        ShardsToMove = move,
    };
}

public enum SplitAction
{
    ScaleUp,
    Split,
}
