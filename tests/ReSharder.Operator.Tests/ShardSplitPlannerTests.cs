using ReSharder.Operator.Services;

namespace ReSharder.Operator.Tests;

public class ShardSplitPlannerTests
{
    private readonly IShardSplitPlanner planner = new ShardSplitPlanner();
    [Fact]
    public void PlanSplit_SingleShard_ReturnsScaleUp()
    {
        var mapping = new Dictionary<string, string>
        {
            ["s1"] = "instance-1",
        };

        var plan = planner.PlanSplit("instance-1", mapping, "instance-2");

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

        var plan = planner.PlanSplit("instance-1", mapping, "instance-2");

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

        var plan = planner.PlanSplit("instance-1", mapping, "instance-2");

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

        var plan = planner.PlanSplit("instance-1", mapping, "instance-2");

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
            ["s3"] = "instance-1",  // s3 should be on instance-3, so it should be moved
            ["s4"] = "instance-1",  // s4 should be on instance-4, so it should be moved
        };

        var plan = planner.PlanSplit("instance-1", mapping, "instance-3");

        Assert.Equal(SplitAction.Split, plan.Action);
        // Both s3 and s4 should be moved because they belong on higher-numbered instances
        Assert.Equal(2, plan.ShardsToMove.Count);
        Assert.Contains("s3", plan.ShardsToMove);
        Assert.Contains("s4", plan.ShardsToMove);
        // Lower-numbered shards remain on the source instance
        Assert.Equal(2, plan.ShardsToKeep.Count);
        Assert.Contains("s1", plan.ShardsToKeep);
        Assert.Contains("s2", plan.ShardsToKeep);
    }

    [Fact]
    public void PlanSplit_ConvergenceScenario_FirstSplit()
    {
        // Initial state: {i1}[s1,s2,s3,s4,s5,s6,s7,s8]
        var mapping = new Dictionary<string, string>
        {
            ["s1"] = "instance-1",
            ["s2"] = "instance-1",
            ["s3"] = "instance-1",
            ["s4"] = "instance-1",
            ["s5"] = "instance-1",
            ["s6"] = "instance-1",
            ["s7"] = "instance-1",
            ["s8"] = "instance-1",
        };

        var plan = planner.PlanSplit("instance-1", mapping, "instance-5");

        Assert.Equal(SplitAction.Split, plan.Action);
        // Should move higher-numbered shards (s5,s6,s7,s8) to instance-5
        Assert.Equal(4, plan.ShardsToMove.Count);
        Assert.Contains("s5", plan.ShardsToMove);
        Assert.Contains("s6", plan.ShardsToMove);
        Assert.Contains("s7", plan.ShardsToMove);
        Assert.Contains("s8", plan.ShardsToMove);
        // Should keep lower-numbered shards (s1,s2,s3,s4) on instance-1
        Assert.Equal(4, plan.ShardsToKeep.Count);
        Assert.Contains("s1", plan.ShardsToKeep);
        Assert.Contains("s2", plan.ShardsToKeep);
        Assert.Contains("s3", plan.ShardsToKeep);
        Assert.Contains("s4", plan.ShardsToKeep);
    }

    [Fact]
    public void PlanSplit_ConvergenceScenario_SecondSplit()
    {
        // After first split: {i1}[s1,s2,s3,s4] , {i5}[s5,s6,s7,s8]
        var mapping = new Dictionary<string, string>
        {
            ["s1"] = "instance-1",
            ["s2"] = "instance-1",
            ["s3"] = "instance-1",
            ["s4"] = "instance-1",
            ["s5"] = "instance-5",
            ["s6"] = "instance-5",
            ["s7"] = "instance-5",
            ["s8"] = "instance-5",
        };

        // Test split on instance-1
        var plan1 = planner.PlanSplit("instance-1", mapping, "instance-3");
        Assert.Equal(SplitAction.Split, plan1.Action);
        // Should move s3 and s4 to instance-3
        Assert.Equal(2, plan1.ShardsToMove.Count);
        Assert.Contains("s3", plan1.ShardsToMove);
        Assert.Contains("s4", plan1.ShardsToMove);

        // Test split on instance-5  
        var plan2 = planner.PlanSplit("instance-5", mapping, "instance-7");
        Assert.Equal(SplitAction.Split, plan2.Action);
        // Should move s7 and s8 to instance-7
        Assert.Equal(2, plan2.ShardsToMove.Count);
        Assert.Contains("s7", plan2.ShardsToMove);
        Assert.Contains("s8", plan2.ShardsToMove);
    }

    [Fact]
    public void GenerateNextInstanceName_NoExisting_Returns1()
    {
        var name = planner.GenerateNextInstanceName("my-db", [], []);
        Assert.Equal("smd-instance-my-db-1", name);
    }

    [Fact]
    public void GenerateNextInstanceName_WithExisting_Increments()
    {
        var existing = new[] { "smd-instance-my-db-1", "smd-instance-my-db-2" };
        var name = planner.GenerateNextInstanceName("my-db", existing, []);
        Assert.Equal("smd-instance-my-db-3", name);
    }

    [Fact]
    public void GenerateNextInstanceName_IgnoresNonMatchingPrefixes()
    {
        var existing = new[] { "smd-instance-other-db-5", "smd-instance-my-db-2" };
        var name = planner.GenerateNextInstanceName("my-db", existing, []);
        Assert.Equal("smd-instance-my-db-3", name);
    }

    [Fact]
    public void GenerateNextInstanceName_WithShardsToMove_UsesShardNumber()
    {
        var existing = new[] { "smd-instance-my-db-1" };
        var shardsToMove = new[] { "s3", "s5" };
        var name = planner.GenerateNextInstanceName("my-db", existing, shardsToMove);
        Assert.Equal("smd-instance-my-db-3", name);
    }

    [Fact]
    public void GenerateNextInstanceName_WithShardsToMove_TargetAlreadyExists_FindsNextAvailable()
    {
        var existing = new[] { "smd-instance-my-db-1", "smd-instance-my-db-3" };
        var shardsToMove = new[] { "s3", "s5" };
        var name = planner.GenerateNextInstanceName("my-db", existing, shardsToMove);
        Assert.Equal("smd-instance-my-db-4", name);
    }

    [Fact]
    public void GenerateNextInstanceName_WithShardsToMove_CreatesMatchingInstanceWhenAvailable()
    {
        // This test represents the ideal scenario where each shard has its own instance
        var existing = new[] { "smd-instance-my-db-1", "smd-instance-my-db-2" };
        var shardsToMove = new[] { "s3", "s5" };
        var name = planner.GenerateNextInstanceName("my-db", existing, shardsToMove);
        Assert.Equal("smd-instance-my-db-3", name);
    }

    [Fact]
    public void GenerateNextInstanceName_WithShardsToMove_NoValidNumber_Sequential()
    {
        var existing = new[] { "smd-instance-my-db-1", "smd-instance-my-db-2", "smd-instance-my-db-3" };
        var shardsToMove = new[] { "s3" };
        var name = planner.GenerateNextInstanceName("my-db", existing, shardsToMove);
        Assert.Equal("smd-instance-my-db-4", name);
    }

    [Fact]
    public void PlanSplit_NoShards_ReturnsScaleUp()
    {
        var mapping = new Dictionary<string, string>();

        var plan = planner.PlanSplit("smd-instance-my-db-1", mapping, "smd-instance-my-db-2");

        Assert.Equal(SplitAction.ScaleUp, plan.Action);
        Assert.Equal("smd-instance-my-db-1", plan.SourceInstance);
        Assert.Null(plan.TargetInstance);
        Assert.Empty(plan.ShardsToMove);
        Assert.Empty(plan.ShardsToKeep);
    }

    [Fact]
    public void CascadingSplit_ConvergenceScenario()
    {
        // Initial state: all shards on instance-1
        var mapping = new Dictionary<string, string>
        {
            ["s1"] = "smd-instance-db-1",
            ["s2"] = "smd-instance-db-1",
            ["s3"] = "smd-instance-db-1",
            ["s4"] = "smd-instance-db-1",
            ["s5"] = "smd-instance-db-1",
            ["s6"] = "smd-instance-db-1",
            ["s7"] = "smd-instance-db-1",
            ["s8"] = "smd-instance-db-1",
        };

        // First split: move s5-s8 to instance-5
        var existingInstances = mapping.Values.Distinct().ToList();
        // Determine shards expected to move in a balanced split and use them to generate the instance name
        var shardsToMoveForName = mapping.Keys.OrderBy(s => s, StringComparer.Ordinal)
            .Skip(mapping.Count / 2)
            .ToList();
        var newName1 = planner.GenerateNextInstanceName("db", existingInstances, shardsToMoveForName);
        var plan1 = planner.PlanSplit("smd-instance-db-1", mapping, newName1);

        Assert.Equal(SplitAction.Split, plan1.Action);
        Assert.Equal("smd-instance-db-5", plan1.TargetInstance);
        Assert.Equal(4, plan1.ShardsToMove.Count);
        Assert.Contains("s5", plan1.ShardsToMove);
        Assert.Contains("s6", plan1.ShardsToMove);
        Assert.Contains("s7", plan1.ShardsToMove);
        Assert.Contains("s8", plan1.ShardsToMove);
    }

    [Fact]
    public void PlanSplit_ShardsAlreadyOnCorrectInstances_NoSplit()
    {
        // When shards are already on their correct instances, no split should occur
        var mapping = new Dictionary<string, string>
        {
            ["s1"] = "smd-instance-db-1",
            ["s2"] = "smd-instance-db-2",
            ["s3"] = "smd-instance-db-3",
            ["s4"] = "smd-instance-db-4",
        };

        var plan = planner.PlanSplit("smd-instance-db-1", mapping, "smd-instance-db-5");

        Assert.Equal(SplitAction.ScaleUp, plan.Action);
        Assert.Equal("smd-instance-db-1", plan.SourceInstance);
    }

    [Fact]
    public void PlanSplit_InvalidShardName_HandlesGracefully()
    {
        // Test with invalid shard names (non-numeric suffix)
        var mapping = new Dictionary<string, string>
        {
            ["s1"] = "instance-1",
            ["s2"] = "instance-1",
            ["sinvalid"] = "instance-1",
            ["s4"] = "instance-1",
        };

        var plan = planner.PlanSplit("instance-1", mapping, "instance-2");

        Assert.Equal(SplitAction.Split, plan.Action);
        // Should still work, just ignore invalid shard names
        Assert.Equal(2, plan.ShardsToMove.Count);
    }

    [Fact]
    public void PlanSplit_EmptyShardList_ReturnsScaleUp()
    {
        // Test with empty shard list
        var mapping = new Dictionary<string, string>();

        var plan = planner.PlanSplit("instance-1", mapping, "instance-2");

        Assert.Equal(SplitAction.ScaleUp, plan.Action);
        Assert.Equal("instance-1", plan.SourceInstance);
    }

    [Fact]
    public void PlanSplit_AllShardsOnOtherInstances_ReturnsScaleUp()
    {
        // Test when no shards are on the target instance
        var mapping = new Dictionary<string, string>
        {
            ["s1"] = "instance-2",
            ["s2"] = "instance-2",
            ["s3"] = "instance-3",
            ["s4"] = "instance-4",
        };

        var plan = planner.PlanSplit("instance-1", mapping, "instance-5");

        Assert.Equal(SplitAction.ScaleUp, plan.Action);
        Assert.Equal("instance-1", plan.SourceInstance);
    }
}