namespace ReSharder.Operator.Services;

/// <summary>
/// Pure logic for deciding which shards to move during a split.
/// Stateless -- all state comes in via parameters.
/// </summary>
public sealed class ShardSplitPlanner : IShardSplitPlanner
{
    /// <summary>
    /// Determines which shards should be migrated from the overloaded instance.
    /// In a properly functioning system, each shard should have its own instance
    /// with matching numbers (s3 -> smd-instance-db-3). This method splits shards
    /// to move them towards their target instances.
    /// </summary>
    public SplitPlan PlanSplit(
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

        // Determine which shards should be moved in this split.
        // The desired behavior for convergence is to move the upper half of the
        // ordered shards from the overloaded instance to a new instance. This
        // produces the cascading pattern: {i1}[s1..s8] -> {i1}[s1..s4], {i5}[s5..s8]
        // and then repeats on each instance until each shard is on its matching
        // instance (sN -> smd-instance-...-N).
        var shardsToMove = new List<string>();
        var shardsToKeep = new List<string>();

        var halfCount = shardsOnInstance.Count / 2;
        // Move the upper half (higher-numbered shards) to the new instance
        shardsToMove = shardsOnInstance.Skip(shardsOnInstance.Count - halfCount).ToList();
        shardsToKeep = shardsOnInstance.Take(shardsOnInstance.Count - halfCount).ToList();

        // Validate the provided newInstanceName against the shardsToMove.
        // If it doesn't match the expected instance number (based on the first
        // shard to move), generate a correct name to avoid accidental misplacement.
        string validatedNewInstanceName = newInstanceName;

        if (shardsToMove.Count > 0)
        {
            try
            {
                var firstShard = shardsToMove.OrderBy(s => s, StringComparer.Ordinal).First();
                var shardNumberStr = new string(firstShard.Skip(1).ToArray());
                if (int.TryParse(shardNumberStr, out var shardNumber))
                {
                    var smdNameSegment = GetSmdNameFromInstance(overloadedInstance);
                    // Only enforce the expected naming when the source instance already
                    // follows the smd-instance-<name>-<number> pattern. Otherwise keep
                    // the caller-supplied newInstanceName.
                    if (!string.IsNullOrEmpty(smdNameSegment))
                    {
                        var expectedName = $"smd-instance-{smdNameSegment}-{shardNumber}";
                        // Only override when caller did not supply a candidate name.
                        if (string.IsNullOrEmpty(newInstanceName))
                        {
                            validatedNewInstanceName = expectedName;
                        }
                    }
                }
            }
            catch
            {
                // If any parsing fails, fall back to using the supplied newInstanceName
                // or GenerateNextInstanceName behavior elsewhere. Fail-safe: keep provided name.
                validatedNewInstanceName = newInstanceName;
            }
        }

        return SplitPlan.Split(
            overloadedInstance,
            validatedNewInstanceName,
            shardsToKeep,
            shardsToMove);
    }

    // Helper to extract smdName segment from an instance name like "smd-instance-db-1".
    private string GetSmdNameFromInstance(string instanceName)
    {
        const string prefix = "smd-instance-";
        if (string.IsNullOrEmpty(instanceName) || !instanceName.StartsWith(prefix, StringComparison.Ordinal))
            return string.Empty;

        // Remove prefix and trailing -<number>
        var rest = instanceName[prefix.Length..];
        var lastDash = rest.LastIndexOf('-');
        if (lastDash <= 0)
            return rest;

        return rest[..lastDash];
    }

    /// <summary>
    /// Generates a name for the next CNPG instance based on the shard being moved.
    /// Uses the number from the first shard in the move list to ensure consistency.
    /// In a properly functioning system, each shard should have its own instance
    /// with matching numbers (s3 -> smd-instance-db-3).
    /// </summary>
    public string GenerateNextInstanceName(
        string smdName,
        IEnumerable<string> existingInstances,
        IReadOnlyList<string> shardsToMove)
    {
        const string prefix = "smd-instance-";
        var fullPrefix = $"{prefix}{smdName}-";

        // If no shards to move, use the next sequential number
        if (shardsToMove.Count == 0)
        {
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

        // Use the number from the first shard in the move list
        var firstShard = shardsToMove.OrderBy(s => s, StringComparer.Ordinal).First();

        // Extract number from shard name (e.g., "s3" -> 3)
        var shardNumberStr = new string(firstShard.Skip(1).ToArray());
        if (int.TryParse(shardNumberStr, out var shardNumber))
        {
            // In a properly functioning system, each shard should have its own instance
            // with matching numbers. If this instance already exists, it means:
            // 1. The system is in an inconsistent state, OR
            // 2. This instance already contains the shard we're trying to move (which is wrong)
            // In either case, we should use the next available number to avoid conflicts.
            var instanceName = $"{fullPrefix}{shardNumber}";

            // Check if this instance name already exists
            if (!existingInstances.Contains(instanceName))
            {
                return instanceName;
            }

            // If the instance already exists, it indicates a system inconsistency.
            // Find the next available number to avoid conflicts.
            var usedNumbers = existingInstances
                .Where(name => name.StartsWith(fullPrefix, StringComparison.Ordinal))
                .Select(name =>
                {
                    var suffix = name[fullPrefix.Length..];
                    return int.TryParse(suffix, out var idx) ? idx : 0;
                })
                .ToHashSet();

            // Find the smallest available number greater than all used numbers
            var maxUsed = usedNumbers.DefaultIfEmpty(0).Max();
            for (int i = maxUsed + 1; i < 1000; i++)
            {
                if (!usedNumbers.Contains(i))
                {
                    return $"{fullPrefix}{i}";
                }
            }
        }

        // Fallback to sequential numbering
        var fallbackMaxIndex = existingInstances
            .Where(name => name.StartsWith(fullPrefix, StringComparison.Ordinal))
            .Select(name =>
            {
                var suffix = name[fullPrefix.Length..];
                return int.TryParse(suffix, out var idx) ? idx : 0;
            })
            .DefaultIfEmpty(0)
            .Max();

        return $"{fullPrefix}{fallbackMaxIndex + 1}";
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