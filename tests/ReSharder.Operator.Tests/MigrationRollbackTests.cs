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
}
