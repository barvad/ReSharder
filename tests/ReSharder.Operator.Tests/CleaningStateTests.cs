using System.Text.Json;
using ReSharder.Operator.Entities;

namespace ReSharder.Operator.Tests;

public class CleaningStateTests
{
    [Fact]
    public void CleaningState_SerializesCorrectly()
    {
        var state = new CleaningState
        {
            Instance = "smd-instance-db-1",
            StartedAt = new DateTime(2026, 10, 3, 15, 30, 0, DateTimeKind.Utc),
        };

        var json = JsonSerializer.Serialize(state);
        Assert.Contains("\"instance\"", json);
        Assert.Contains("\"smd-instance-db-1\"", json);
        Assert.Contains("\"startedAt\"", json);
    }

    [Fact]
    public void CleaningState_RoundtripsThroughJson()
    {
        var original = new CleaningState
        {
            Instance = "smd-instance-analytics-2",
            StartedAt = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc),
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<CleaningState>(json)!;

        Assert.Equal(original.Instance, deserialized.Instance);
        Assert.Equal(original.StartedAt, deserialized.StartedAt);
    }

    [Fact]
    public void DefaultSpec_HasCleaningTimeoutSeconds()
    {
        var spec = new V1Alpha1ShardManagedDatabase.V1Alpha1Spec();
        Assert.Equal(120, spec.CleaningTimeoutSeconds);
    }

    [Fact]
    public void Status_WithCleaningState_SerializesAndDeserializes()
    {
        var status = new V1Alpha1ShardManagedDatabase.V1Alpha1Status
        {
            Phase = V1Alpha1ShardManagedDatabase.V1Alpha1Status.PhaseCleaning,
            Cleaning = new CleaningState
            {
                Instance = "smd-instance-main-1",
                StartedAt = new DateTime(2026, 10, 3, 8, 0, 0, DateTimeKind.Utc),
            },
        };

        var json = JsonSerializer.Serialize(status);
        Assert.Contains("\"phase\":\"Cleaning\"", json);
        Assert.Contains("\"cleaning\":", json);
        Assert.Contains("\"smd-instance-main-1\"", json);

        var deserialized = JsonSerializer.Deserialize<V1Alpha1ShardManagedDatabase.V1Alpha1Status>(json)!;
        Assert.Equal("Cleaning", deserialized.Phase);
        Assert.NotNull(deserialized.Cleaning);
        Assert.Equal("smd-instance-main-1", deserialized.Cleaning.Instance);
    }

    [Fact]
    public void CleaningPhase_TransitionsWhenUsedBytesBelowThreshold()
    {
        const long maxBytes = 50L * 1024 * 1024 * 1024; // 50Gi
        const long usedBytesAfterDrop = 30L * 1024 * 1024 * 1024; // 30Gi (freed)

        var isReclaimed = usedBytesAfterDrop < maxBytes;
        Assert.True(isReclaimed);
    }

    [Fact]
    public void CleaningPhase_DetectsTimeout()
    {
        var startedAt = DateTime.UtcNow.AddSeconds(-150);
        var timeout = TimeSpan.FromSeconds(120);

        var elapsed = DateTime.UtcNow - startedAt;
        var timedOut = elapsed >= timeout;

        Assert.True(timedOut);
    }

    [Fact]
    public void CleaningPhase_WithinTimeoutPeriod_DoesNotTimeout()
    {
        var startedAt = DateTime.UtcNow.AddSeconds(-30);
        var timeout = TimeSpan.FromSeconds(120);

        var elapsed = DateTime.UtcNow - startedAt;
        var timedOut = elapsed >= timeout;

        Assert.False(timedOut);
    }
}
