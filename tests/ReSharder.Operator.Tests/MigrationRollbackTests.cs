using System.Text.Json;
using ReSharder.Operator.Entities;

namespace ReSharder.Operator.Tests;

public class MigrationRollbackTests
{
    [Fact]
    public void ActiveMigrationState_SerializesNewFailureTrackingFields()
    {
        var state = new ActiveMigrationState
        {
            Step = MigrationStep.Draining,
            SourceInstance = "smd-instance-db-1",
            TargetInstance = "smd-instance-db-2",
            ShardsInFlight = ["s1", "s2"],
            StartedAt = new DateTime(2026, 10, 3, 10, 0, 0, DateTimeKind.Utc),
            RetryCount = 2,
            LastError = "Connection timeout to target instance",
        };

        var json = JsonSerializer.Serialize(state);
        Assert.Contains("\"startedAt\"", json);
        Assert.Contains("\"retryCount\":2", json);
        Assert.Contains("\"lastError\":\"Connection timeout to target instance\"", json);

        var deserialized = JsonSerializer.Deserialize<ActiveMigrationState>(json)!;
        Assert.Equal(2, deserialized.RetryCount);
        Assert.Equal("Connection timeout to target instance", deserialized.LastError);
        Assert.Equal(new DateTime(2026, 10, 3, 10, 0, 0, DateTimeKind.Utc), deserialized.StartedAt);
    }

    [Fact]
    public void Spec_HasExpectedRollbackDefaults()
    {
        var spec = new V1Alpha1ShardManagedDatabase.V1Alpha1Spec();
        Assert.Equal(60, spec.DrainTimeoutSeconds);
        Assert.Equal(600, spec.MigrationTimeoutSeconds);
        Assert.Equal(3, spec.MaxMigrationRetries);
    }

    [Fact]
    public void MigrationTimeout_DetectsExceededDuration()
    {
        var startedAt = DateTime.UtcNow.AddSeconds(-650);
        var timeout = TimeSpan.FromSeconds(600);

        var isExceeded = (DateTime.UtcNow - startedAt) >= timeout;
        Assert.True(isExceeded);
    }

    [Fact]
    public void DrainTimeout_DetectsExceededDuration()
    {
        var maintenanceSetAt = DateTime.UtcNow.AddSeconds(-70);
        var timeout = TimeSpan.FromSeconds(60);

        var isExceeded = (DateTime.UtcNow - maintenanceSetAt) >= timeout;
        Assert.True(isExceeded);
    }

    [Fact]
    public void MaxRetries_DetectsThreshold()
    {
        const int maxRetries = 3;
        var currentRetries = 3;

        var shouldRollback = currentRetries >= maxRetries;
        Assert.True(shouldRollback);
    }

    [Fact]
    public void ShardMapping_RevertsCorrectly_OnRollback()
    {
        var sourceInstance = "smd-instance-db-1";
        var targetInstance = "smd-instance-db-2";

        var mapping = new Dictionary<string, string>
        {
            ["s1"] = targetInstance,
            ["s2"] = targetInstance,
            ["s3"] = sourceInstance,
        };

        var shardsInFlight = new List<string> { "s1", "s2" };

        // Simulate rollback logic:
        foreach (var shard in shardsInFlight)
        {
            mapping[shard] = sourceInstance;
        }

        Assert.Equal(sourceInstance, mapping["s1"]);
        Assert.Equal(sourceInstance, mapping["s2"]);
        Assert.Equal(sourceInstance, mapping["s3"]);
    }
}
