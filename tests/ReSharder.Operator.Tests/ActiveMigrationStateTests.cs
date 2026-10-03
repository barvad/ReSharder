using System.Text.Json;
using ReSharder.Operator.Entities;

namespace ReSharder.Operator.Tests;

public class ActiveMigrationStateTests
{
    [Fact]
    public void MigrationStep_SerializesAsString()
    {
        var state = new ActiveMigrationState
        {
            Step = MigrationStep.Replicating,
            SourceInstance = "instance-1",
            TargetInstance = "instance-2",
            ShardsInFlight = ["s1", "s2"],
        };

        var json = JsonSerializer.Serialize(state);
        Assert.Contains("\"Replicating\"", json);
        Assert.Contains("\"instance-1\"", json);
        Assert.Contains("\"s1\"", json);
    }

    [Fact]
    public void MigrationStep_Roundtrip()
    {
        var original = new ActiveMigrationState
        {
            Step = MigrationStep.CaughtUp,
            SourceInstance = "src",
            TargetInstance = "tgt",
            ShardsInFlight = ["a", "b", "c"],
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<ActiveMigrationState>(json)!;

        Assert.Equal(MigrationStep.CaughtUp, deserialized.Step);
        Assert.Equal("src", deserialized.SourceInstance);
        Assert.Equal("tgt", deserialized.TargetInstance);
        Assert.Equal(3, deserialized.ShardsInFlight.Count);
        Assert.Equal(["a", "b", "c"], deserialized.ShardsInFlight);
    }

    [Fact]
    public void MigrationStep_AllValues_Serialize()
    {
        foreach (var step in Enum.GetValues<MigrationStep>())
        {
            var state = new ActiveMigrationState { Step = step };
            var json = JsonSerializer.Serialize(state);
            Assert.Contains($"\"{step}\"", json);

            var back = JsonSerializer.Deserialize<ActiveMigrationState>(json)!;
            Assert.Equal(step, back.Step);
        }
    }

    [Fact]
    public void DefaultState_IsNotStarted()
    {
        var state = new ActiveMigrationState();
        Assert.Equal(MigrationStep.NotStarted, state.Step);
        Assert.Equal(string.Empty, state.SourceInstance);
        Assert.Equal(string.Empty, state.TargetInstance);
        Assert.Empty(state.ShardsInFlight);
        Assert.Null(state.MaintenanceSetAt);
    }

    [Fact]
    public void Draining_Step_Roundtrips()
    {
        var state = new ActiveMigrationState
        {
            Step = MigrationStep.Draining,
            MaintenanceSetAt = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc),
        };

        var json = JsonSerializer.Serialize(state);
        Assert.Contains("\"Draining\"", json);

        var back = JsonSerializer.Deserialize<ActiveMigrationState>(json)!;
        Assert.Equal(MigrationStep.Draining, back.Step);
        Assert.NotNull(back.MaintenanceSetAt);
    }

    [Fact]
    public void MigrationStep_HasFiveValues()
    {
        var values = Enum.GetValues<MigrationStep>();
        Assert.Equal(5, values.Length);
        Assert.Contains(MigrationStep.NotStarted, values);
        Assert.Contains(MigrationStep.Replicating, values);
        Assert.Contains(MigrationStep.Draining, values);
        Assert.Contains(MigrationStep.CaughtUp, values);
        Assert.Contains(MigrationStep.CutoverDone, values);
    }
}
