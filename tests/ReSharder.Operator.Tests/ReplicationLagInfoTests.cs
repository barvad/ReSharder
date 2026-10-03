using ReSharder.Operator.Services;

namespace ReSharder.Operator.Tests;

public class ReplicationLagInfoTests
{
    // -- IsCaughtUp (exact zero) --

    [Fact]
    public void IsCaughtUp_WhenZeroLagAndSyncComplete_ReturnsTrue()
    {
        var info = new ReplicationLagInfo(0, true);
        Assert.True(info.IsCaughtUp);
    }

    [Fact]
    public void IsCaughtUp_WhenLagPositive_ReturnsFalse()
    {
        var info = new ReplicationLagInfo(1024, true);
        Assert.False(info.IsCaughtUp);
    }

    [Fact]
    public void IsCaughtUp_WhenSyncNotComplete_ReturnsFalse()
    {
        var info = new ReplicationLagInfo(0, false);
        Assert.False(info.IsCaughtUp);
    }

    [Fact]
    public void IsCaughtUp_WhenBothFalse_ReturnsFalse()
    {
        var info = new ReplicationLagInfo(500, false);
        Assert.False(info.IsCaughtUp);
    }

    [Fact]
    public void IsCaughtUp_NegativeLagAndSyncDone_ReturnsTrue()
    {
        var info = new ReplicationLagInfo(-1, true);
        Assert.True(info.IsCaughtUp);
    }

    // -- IsWithinThreshold (pre-drain gate) --

    [Fact]
    public void IsWithinThreshold_ExactlyAtThreshold_ReturnsTrue()
    {
        var info = new ReplicationLagInfo(1_048_576, true);
        Assert.True(info.IsWithinThreshold(1_048_576));
    }

    [Fact]
    public void IsWithinThreshold_BelowThreshold_ReturnsTrue()
    {
        var info = new ReplicationLagInfo(500_000, true);
        Assert.True(info.IsWithinThreshold(1_048_576));
    }

    [Fact]
    public void IsWithinThreshold_AboveThreshold_ReturnsFalse()
    {
        var info = new ReplicationLagInfo(2_000_000, true);
        Assert.False(info.IsWithinThreshold(1_048_576));
    }

    [Fact]
    public void IsWithinThreshold_SyncNotComplete_ReturnsFalse()
    {
        var info = new ReplicationLagInfo(100, false);
        Assert.False(info.IsWithinThreshold(1_048_576));
    }

    [Fact]
    public void IsWithinThreshold_ZeroLag_ReturnsTrue()
    {
        var info = new ReplicationLagInfo(0, true);
        Assert.True(info.IsWithinThreshold(1_048_576));
    }

    [Fact]
    public void IsWithinThreshold_ZeroThreshold_OnlyZeroLagPasses()
    {
        Assert.True(new ReplicationLagInfo(0, true).IsWithinThreshold(0));
        Assert.False(new ReplicationLagInfo(1, true).IsWithinThreshold(0));
    }
}
