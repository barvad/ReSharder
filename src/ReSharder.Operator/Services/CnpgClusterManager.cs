using k8s.Models;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging;
using ReSharder.Operator.Entities;

namespace ReSharder.Operator.Services;

/// <summary>
/// Manages CloudNativePG Cluster custom resources on behalf of the operator.
/// </summary>
public sealed class CnpgClusterManager(
    IKubernetesClient client,
    ILogger<CnpgClusterManager> logger)
{
    /// <summary>
    /// Ensures a CNPG Cluster CR exists for the given instance name.
    /// Creates one if missing; returns the existing resource otherwise.
    /// </summary>
    public async Task<CnpgCluster> EnsureClusterAsync(
        string instanceName,
        string ns,
        string storageSize,
        V1Alpha1ShardManagedDatabase owner,
        CancellationToken ct)
    {
        var existing = await client.GetAsync<CnpgCluster>(instanceName, ns, ct);
        if (existing is not null)
        {
            logger.LogDebug("CNPG Cluster {Name} already exists in {Namespace}.", instanceName, ns);
            return existing;
        }

        logger.LogInformation(
            "Creating CNPG Cluster {Name} in {Namespace} with storage {Size}.",
            instanceName, ns, storageSize);

        var cluster = BuildClusterManifest(instanceName, ns, storageSize, owner);
        return await client.CreateAsync(cluster, ct);
    }

    /// <summary>
    /// Increases the storage size of an existing CNPG Cluster by the specified increment.
    /// CNPG will handle the actual PVC resize via <c>resizeInUseVolumes: true</c>.
    /// </summary>
    public async Task<CnpgCluster> ScaleStorageAsync(
        string instanceName,
        string ns,
        string incrementGi,
        CancellationToken ct)
    {
        var cluster = await client.GetAsync<CnpgCluster>(instanceName, ns, ct)
            ?? throw new InvalidOperationException(
                $"CNPG Cluster {instanceName} not found in {ns}.");

        var currentBytes = ParseStorageToBytes(cluster.Spec.Storage.Size);
        var incrementBytes = ParseStorageToBytes(incrementGi);
        var newBytes = currentBytes + incrementBytes;
        var newSize = FormatBytesToGi(newBytes);

        logger.LogInformation(
            "Scaling CNPG Cluster {Name} storage from {Old} to {New}.",
            instanceName, cluster.Spec.Storage.Size, newSize);

        cluster.Spec.Storage.Size = newSize;
        return await client.UpdateAsync(cluster, ct);
    }

    /// <summary>
    /// Deletes a CNPG Cluster CR by name.
    /// </summary>
    public async Task DeleteClusterAsync(string instanceName, string ns, CancellationToken ct)
    {
        var existing = await client.GetAsync<CnpgCluster>(instanceName, ns, ct);
        if (existing is null)
        {
            logger.LogDebug("CNPG Cluster {Name} already gone.", instanceName);
            return;
        }

        logger.LogInformation("Deleting CNPG Cluster {Name} in {Namespace}.", instanceName, ns);
        await client.DeleteAsync(existing, ct);
    }

    private static CnpgCluster BuildClusterManifest(
        string instanceName,
        string ns,
        string storageSize,
        V1Alpha1ShardManagedDatabase owner)
    {
        return new CnpgCluster
        {
            Metadata = new V1ObjectMeta
            {
                Name = instanceName,
                NamespaceProperty = ns,
                Labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/managed-by"] = "resharder",
                    ["app.kubernetes.io/part-of"] = "resharder",
                    ["resharder.io/owner"] = owner.Name(),
                },
                OwnerReferences =
                [
                    new V1OwnerReference
                    {
                        ApiVersion = owner.ApiVersion,
                        Kind = owner.Kind,
                        Name = owner.Name(),
                        Uid = owner.Uid(),
                        Controller = true,
                        BlockOwnerDeletion = true,
                    },
                ],
            },
            Spec = new CnpgClusterSpec
            {
                Instances = 1,
                EnableSuperuserAccess = true,
                Storage = new CnpgStorageSpec
                {
                    Size = storageSize,
                    ResizeInUseVolumes = true,
                },
                Bootstrap = new CnpgBootstrapSpec
                {
                    InitDb = new CnpgInitDbSpec
                    {
                        Database = "app",
                        Owner = "app",
                    },
                },
            },
        };
    }

    internal static long ParseStorageToBytes(string quantity)
    {
        if (quantity.EndsWith("Ti", StringComparison.OrdinalIgnoreCase))
            return long.Parse(quantity[..^2]) * 1024L * 1024 * 1024 * 1024;
        if (quantity.EndsWith("Gi", StringComparison.OrdinalIgnoreCase))
            return long.Parse(quantity[..^2]) * 1024L * 1024 * 1024;
        if (quantity.EndsWith("Mi", StringComparison.OrdinalIgnoreCase))
            return long.Parse(quantity[..^2]) * 1024L * 1024;
        if (quantity.EndsWith("Ki", StringComparison.OrdinalIgnoreCase))
            return long.Parse(quantity[..^2]) * 1024L;

        throw new FormatException($"Unsupported storage quantity format: {quantity}");
    }

    internal static string FormatBytesToGi(long bytes)
    {
        var gi = bytes / (1024.0 * 1024 * 1024);
        var rounded = (long)Math.Ceiling(gi);
        return $"{rounded}Gi";
    }
}
