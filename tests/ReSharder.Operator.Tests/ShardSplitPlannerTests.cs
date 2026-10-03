using ReSharder.Operator.Services;

namespace ReSharder.Operator.Tests;

public class ShardSplitPlannerTests
{
    [Fact]
    public void PlanSplit_SingleShard_ReturnsScaleUp()
    {
        var mapping = new Dictionary<string, string>
        {
            ["s1"] = "instance-1",
        };

        var plan = ShardSplitPlanner.PlanSplit("instance-1", mapping, "instance-2");

        Assert.Equal(SplitAction.ScaleUp, plan.Action);
        Assert.Equal("instance-1", plan.SourceInstance);
        Assert.Null(plan.TargetInstance);
        Assert.Empty(plan.ShardsToMove);
    }

    [Fact]
    public void PlanSplit_TwoShards_MovesExactlyOne()
    {
        var mapping = new Dictionary<string, string>
        {
            ["s1"] = "instance-1",
            ["s2"] = "instance-1",
        };

        var plan = ShardSplitPlanner.PlanSplit("instance-1", mapping, "instance-2");

        Assert.Equal(SplitAction.Split, plan.Action);
        Assert.Single(plan.ShardsToMove);
        Assert.Single(plan.ShardsToKeep);
        Assert.Equal("s2", plan.ShardsToMove[0]);
        Assert.Equal("s1", plan.ShardsToKeep[0]);
    }

    [Fact]
    public void PlanSplit_EightShards_MovesFour()
    {
        var mapping = new Dictionary<string, string>();
        for (var i = 1; i <= 8; i++)
            mapping[$"s{i}"] = "instance-1";

        var plan = ShardSplitPlanner.PlanSplit("instance-1", mapping, "instance-2");

        Assert.Equal(SplitAction.Split, plan.Action);
        Assert.Equal(4, plan.ShardsToMove.Count);
        Assert.Equal(4, plan.ShardsToKeep.Count);
        Assert.Equal("instance-2", plan.TargetInstance);
    }

    [Fact]
    public void PlanSplit_OddNumber_RoundsDownMoveCount()
    {
        var mapping = new Dictionary<string, string>
        {
            ["s1"] = "instance-1",
            ["s2"] = "instance-1",
            ["s3"] = "instance-1",
        };

        var plan = ShardSplitPlanner.PlanSplit("instance-1", mapping, "instance-2");

        Assert.Equal(SplitAction.Split, plan.Action);
        Assert.Single(plan.ShardsToMove);
        Assert.Equal(2, plan.ShardsToKeep.Count);
    }

    [Fact]
    public void PlanSplit_IgnoresShardsOnOtherInstances()
    {
        var mapping = new Dictionary<string, string>
        {
            ["s1"] = "instance-1",
            ["s2"] = "instance-1",
            ["s3"] = "instance-2",
            ["s4"] = "instance-2",
        };

        var plan = ShardSplitPlanner.PlanSplit("instance-1", mapping, "instance-3");

        Assert.Equal(SplitAction.Split, plan.Action);
        Assert.Single(plan.ShardsToMove);
        Assert.Single(plan.ShardsToKeep);
    }

    [Fact]
    public void GenerateNextInstanceName_NoExisting_Returns1()
    {
        var name = ShardSplitPlanner.GenerateNextInstanceName("my-db", []);
        Assert.Equal("smd-instance-my-db-1", name);
    }

    [Fact]
    public void GenerateNextInstanceName_WithExisting_Increments()
    {
        var existing = new[] { "smd-instance-my-db-1", "smd-instance-my-db-2" };
        var name = ShardSplitPlanner.GenerateNextInstanceName("my-db", existing);
        Assert.Equal("smd-instance-my-db-3", name);
    }

    [Fact]
    public void GenerateNextInstanceName_IgnoresNonMatchingPrefixes()
    {
        var existing = new[] { "smd-instance-other-db-5", "smd-instance-my-db-2" };
        var name = ShardSplitPlanner.GenerateNextInstanceName("my-db", existing);
        Assert.Equal("smd-instance-my-db-3", name);
    }
}
