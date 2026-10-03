using ReSharder.Operator.Services;

namespace ReSharder.Operator.Tests;

/// <summary>
/// Tests that verify the system handles cascading splits correctly:
/// after the first split creates instance-2, if instance-2 also overflows,
/// it gets split into instance-3, and so on.
/// </summary>
public class MultiSplitScenarioTests
{
    [Fact]
    public void AfterFirstSplit_Instance2_CanBeSplitFurther()
    {
        // After the first split: instance-1 has [s1,s2,s3,s4], instance-2 has [s5,s6,s7,s8].
        // Now instance-2 overflows.
        var mapping = new Dictionary<string, string>
        {
            ["s1"] = "smd-instance-db-1",
            ["s2"] = "smd-instance-db-1",
            ["s3"] = "smd-instance-db-1",
            ["s4"] = "smd-instance-db-1",
            ["s5"] = "smd-instance-db-2",
            ["s6"] = "smd-instance-db-2",
            ["s7"] = "smd-instance-db-2",
            ["s8"] = "smd-instance-db-2",
        };

        var newName = ShardSplitPlanner.GenerateNextInstanceName(
            "db", mapping.Values.Distinct());

        var plan = ShardSplitPlanner.PlanSplit("smd-instance-db-2", mapping, newName);

        Assert.Equal(SplitAction.Split, plan.Action);
        Assert.Equal("smd-instance-db-2", plan.SourceInstance);
        Assert.Equal("smd-instance-db-3", plan.TargetInstance);
        Assert.Equal(2, plan.ShardsToKeep.Count);
        Assert.Equal(2, plan.ShardsToMove.Count);
        // Lexicographic last half moves: s7, s8
        Assert.Contains("s7", plan.ShardsToMove);
        Assert.Contains("s8", plan.ShardsToMove);
        Assert.Contains("s5", plan.ShardsToKeep);
        Assert.Contains("s6", plan.ShardsToKeep);
    }

    [Fact]
    public void CascadingSplit_ThreeGenerations()
    {
        // Generation 1: 8 shards on instance-1
        // After split 1: instance-1 [s1..s4], instance-2 [s5..s8]
        // After split 2: instance-2 [s5,s6], instance-3 [s7,s8]
        // Now instance-1 also overflows.

        var mapping = new Dictionary<string, string>
        {
            ["s1"] = "smd-instance-db-1",
            ["s2"] = "smd-instance-db-1",
            ["s3"] = "smd-instance-db-1",
            ["s4"] = "smd-instance-db-1",
            ["s5"] = "smd-instance-db-2",
            ["s6"] = "smd-instance-db-2",
            ["s7"] = "smd-instance-db-3",
            ["s8"] = "smd-instance-db-3",
        };

        var newName = ShardSplitPlanner.GenerateNextInstanceName(
            "db", mapping.Values.Distinct());

        var plan = ShardSplitPlanner.PlanSplit("smd-instance-db-1", mapping, newName);

        Assert.Equal(SplitAction.Split, plan.Action);
        Assert.Equal("smd-instance-db-1", plan.SourceInstance);
        Assert.Equal("smd-instance-db-4", plan.TargetInstance);
        Assert.Equal(2, plan.ShardsToKeep.Count);
        Assert.Equal(2, plan.ShardsToMove.Count);
        // s3, s4 move (lexicographic last half of [s1,s2,s3,s4])
        Assert.Contains("s3", plan.ShardsToMove);
        Assert.Contains("s4", plan.ShardsToMove);
    }

    [Fact]
    public void SingleShardInstance_AfterManySplits_ScalesUp()
    {
        // After splitting 8 -> 4+4 -> 2+2+2+2 -> 1+1+1+1+1+1+1+1
        // All instances have exactly 1 shard -- no more splitting possible.
        var mapping = new Dictionary<string, string>
        {
            ["s1"] = "smd-instance-db-1",
            ["s2"] = "smd-instance-db-2",
            ["s3"] = "smd-instance-db-3",
            ["s4"] = "smd-instance-db-4",
        };

        foreach (var instance in mapping.Values.Distinct())
        {
            var plan = ShardSplitPlanner.PlanSplit(instance, mapping, "unused");
            Assert.Equal(SplitAction.ScaleUp, plan.Action);
            Assert.Equal(instance, plan.SourceInstance);
        }
    }

    [Fact]
    public void InstanceNaming_IsGloballyUnique_AcrossAllInstances()
    {
        // Even if splits happen on different instances, the global counter
        // finds the max across ALL existing instances.
        var existing = new[]
        {
            "smd-instance-db-1",
            "smd-instance-db-2",
            "smd-instance-db-5", // gap -- e.g. 3 and 4 were deleted
        };

        var next = ShardSplitPlanner.GenerateNextInstanceName("db", existing);
        Assert.Equal("smd-instance-db-6", next);
    }

    [Fact]
    public void Split_OnlyAffectsTargetInstance_NotOthers()
    {
        var mapping = new Dictionary<string, string>
        {
            ["s1"] = "smd-instance-db-1",
            ["s2"] = "smd-instance-db-1",
            ["s3"] = "smd-instance-db-2",
            ["s4"] = "smd-instance-db-2",
            ["s5"] = "smd-instance-db-3",
            ["s6"] = "smd-instance-db-3",
        };

        // Split instance-2 only
        var plan = ShardSplitPlanner.PlanSplit(
            "smd-instance-db-2", mapping, "smd-instance-db-4");

        // Only s3/s4 are involved -- s1,s2,s5,s6 untouched
        var allInvolved = plan.ShardsToKeep.Concat(plan.ShardsToMove).ToHashSet();
        Assert.Equal(new HashSet<string> { "s3", "s4" }, allInvolved);
    }
}
