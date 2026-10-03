using ReSharder.Operator.Services;

namespace ReSharder.Operator.Tests;

public class PvcMonitorParsingTests
{
    private const string SampleKubeletStats = """
    {
      "node": { "nodeName": "node-1" },
      "pods": [
        {
          "podRef": {
            "name": "smd-instance-my-db-1-1",
            "namespace": "default"
          },
          "volume": [
            {
              "name": "pgdata",
              "pvcRef": {
                "name": "smd-instance-my-db-1-1",
                "namespace": "default"
              },
              "usedBytes": 5368709120,
              "capacityBytes": 10737418240,
              "availableBytes": 5368709120
            },
            {
              "name": "kube-api-access",
              "usedBytes": 12288,
              "capacityBytes": 0,
              "availableBytes": 0
            }
          ]
        },
        {
          "podRef": {
            "name": "other-pod",
            "namespace": "default"
          },
          "volume": [
            {
              "name": "data",
              "pvcRef": {
                "name": "other-pvc",
                "namespace": "default"
              },
              "usedBytes": 1000000,
              "capacityBytes": 2000000,
              "availableBytes": 1000000
            }
          ]
        }
      ]
    }
    """;

    [Fact]
    public void ParseVolumeStats_FindsMatchingPvc()
    {
        var result = PvcMonitor.ParseVolumeStats(
            SampleKubeletStats, "smd-instance-my-db-1-1", "default");

        Assert.NotNull(result);
        Assert.Equal("smd-instance-my-db-1-1", result.PvcName);
        Assert.Equal(5368709120L, result.UsedBytes);
        Assert.Equal(10737418240L, result.CapacityBytes);
        Assert.Equal(5368709120L, result.AvailableBytes);
        Assert.InRange(result.UsagePercent, 49.9, 50.1);
    }

    [Fact]
    public void ParseVolumeStats_ReturnsNullForMissingPvc()
    {
        var result = PvcMonitor.ParseVolumeStats(
            SampleKubeletStats, "nonexistent-pvc", "default");

        Assert.Null(result);
    }

    [Fact]
    public void ParseVolumeStats_ReturnsNullForWrongNamespace()
    {
        var result = PvcMonitor.ParseVolumeStats(
            SampleKubeletStats, "smd-instance-my-db-1-1", "other-ns");

        Assert.Null(result);
    }

    [Fact]
    public void PvcUsage_UsagePercent_ZeroCapacity_ReturnsZero()
    {
        var usage = new PvcUsage("pvc", "ns", 100, 0, 0);
        Assert.Equal(0.0, usage.UsagePercent);
    }
}
