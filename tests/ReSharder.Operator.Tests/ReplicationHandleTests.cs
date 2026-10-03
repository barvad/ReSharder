using ReSharder.Operator.Services;

namespace ReSharder.Operator.Tests;

public class ReplicationHandleTests
{
    [Fact]
    public void Handle_PreservesAllProperties()
    {
        var handle = new ReplicationHandle(
            "s1", "source-1", "target-2", "default",
            "pub_migration_s1", "sub_migration_s1");

        Assert.Equal("s1", handle.ShardName);
        Assert.Equal("source-1", handle.SourceInstance);
        Assert.Equal("target-2", handle.TargetInstance);
        Assert.Equal("default", handle.Namespace);
        Assert.Equal("pub_migration_s1", handle.PublicationName);
        Assert.Equal("sub_migration_s1", handle.SubscriptionName);
    }

    [Fact]
    public void Handle_NamingConvention_MatchesSqlGeneration()
    {
        var shardName = "my_shard";
        var expectedPub = $"pub_migration_{shardName}";
        var expectedSub = $"sub_migration_{shardName}";

        var handle = new ReplicationHandle(
            shardName, "src", "tgt", "ns", expectedPub, expectedSub);

        Assert.Equal(expectedPub, handle.PublicationName);
        Assert.Equal(expectedSub, handle.SubscriptionName);
    }
}
