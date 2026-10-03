using System.Text.Json;
using k8s;
using k8s.Models;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging;

namespace ReSharder.Operator.Services;

/// <summary>
/// Monitors PVC disk usage by querying the kubelet stats summary API
/// through the Kubernetes API server proxy.
/// </summary>
public sealed class PvcMonitor(
    IKubernetesClient client,
    ILogger<PvcMonitor> logger)
{
    /// <summary>
    /// Gets the actual disk usage (in bytes) for a given PVC.
    /// Returns <c>null</c> if usage cannot be determined.
    /// </summary>
    public async Task<PvcUsage?> GetPvcUsageAsync(
        string pvcName,
        string ns,
        CancellationToken ct)
    {
        // Step 1: Find the pod that mounts this PVC.
        var pods = await client.ListAsync<V1Pod>(ns, cancellationToken: ct);
        var targetPod = pods.FirstOrDefault(pod =>
            pod.Spec?.Volumes?.Any(v =>
                v.PersistentVolumeClaim?.ClaimName == pvcName) == true);

        if (targetPod is null)
        {
            logger.LogWarning("No pod found mounting PVC {PvcName} in {Namespace}.", pvcName, ns);
            return null;
        }

        var nodeName = targetPod.Spec.NodeName;
        if (string.IsNullOrEmpty(nodeName))
        {
            logger.LogWarning("Pod {Pod} has no node assignment yet.", targetPod.Name());
            return null;
        }

        // Step 2: Query kubelet stats summary via API server proxy.
        try
        {
            var apiClient = client.ApiClient;
            var result = await apiClient.CoreV1.ConnectGetNodeProxyWithPathWithHttpMessagesAsync(
                nodeName, "stats/summary", cancellationToken: ct);

            var json = await result.Response.Content.ReadAsStringAsync(ct);
            return ParseVolumeStats(json, pvcName, ns);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to query kubelet stats for node {Node} (PVC {Pvc}).",
                nodeName, pvcName);
            return null;
        }
    }

    /// <summary>
    /// Gets disk usage for a CNPG cluster instance.
    /// CNPG names the PVC the same as the first pod: <c>{cluster-name}-1</c>.
    /// </summary>
    public async Task<PvcUsage?> GetCnpgInstanceUsageAsync(
        string instanceName,
        string ns,
        CancellationToken ct)
    {
        // CNPG PVC naming: {cluster-name}-1 for the primary instance
        var pvcName = $"{instanceName}-1";
        return await GetPvcUsageAsync(pvcName, ns, ct);
    }

    internal static PvcUsage? ParseVolumeStats(string json, string pvcName, string ns)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("pods", out var pods))
            return null;

        foreach (var pod in pods.EnumerateArray())
        {
            if (!pod.TryGetProperty("volume", out var volumes))
                continue;

            foreach (var vol in volumes.EnumerateArray())
            {
                if (!vol.TryGetProperty("pvcRef", out var pvcRef))
                    continue;

                var name = pvcRef.GetProperty("name").GetString();
                var volNs = pvcRef.GetProperty("namespace").GetString();

                if (name != pvcName || volNs != ns)
                    continue;

                var usedBytes = vol.TryGetProperty("usedBytes", out var ub)
                    ? ub.GetInt64() : 0L;
                var capacityBytes = vol.TryGetProperty("capacityBytes", out var cb)
                    ? cb.GetInt64() : 0L;
                var availableBytes = vol.TryGetProperty("availableBytes", out var ab)
                    ? ab.GetInt64() : 0L;

                return new PvcUsage(pvcName, ns, usedBytes, capacityBytes, availableBytes);
            }
        }

        return null;
    }
}

/// <summary>
/// Disk usage statistics for a single PVC.
/// </summary>
public sealed record PvcUsage(
    string PvcName,
    string Namespace,
    long UsedBytes,
    long CapacityBytes,
    long AvailableBytes)
{
    public double UsagePercent => CapacityBytes > 0
        ? (double)UsedBytes / CapacityBytes * 100.0
        : 0.0;
}
