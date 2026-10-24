namespace ReSharder.Operator.Services;

/// <summary>
/// Abstraction for shard split planning logic.
/// Allows swapping implementation and easier testing.
/// </summary>
public interface IShardSplitPlanner
{
    SplitPlan PlanSplit(
        string overloadedInstance,
        Dictionary<string, string> shardMapping,
        string newInstanceName);

    string GenerateNextInstanceName(
        string smdName,
        IEnumerable<string> existingInstances,
        IReadOnlyList<string> shardsToMove);
}