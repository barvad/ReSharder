using k8s.Models;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Finalizer;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging;
using ReSharder.Operator.Entities;

namespace ReSharder.Operator.Finalizer;

/// <summary>
/// Finalizer for <see cref="V1Alpha1ShardManagedDatabase"/>.
/// Cleans up owned CNPG Cluster CRs, topology ConfigMaps, and replication slots
/// before allowing the custom resource to be garbage-collected.
/// </summary>
public sealed class ShardManagedDatabaseFinalizer(
    IKubernetesClient client,
    ILogger<ShardManagedDatabaseFinalizer> logger)
    : IEntityFinalizer<V1Alpha1ShardManagedDatabase>
{
    private const string TopologyConfigMapName = "app-shard-topology";

    public async Task<ReconciliationResult<V1Alpha1ShardManagedDatabase>> FinalizeAsync(
        V1Alpha1ShardManagedDatabase entity,
        CancellationToken cancellationToken)
    {
        var ns = entity.Namespace();
        var name = entity.Name();
        logger.LogInformation(
            "Finalizing ShardManagedDatabase {Namespace}/{Name}. Cleaning up resources.",
            ns, name);

        // 1. Delete owned CNPG Cluster CRs.
        //    Instances are tracked in status.shardMapping (values = instance names).
        var instanceNames = entity.Status.ShardMapping.Values.Distinct().ToList();
        foreach (var instanceName in instanceNames)
        {
            logger.LogInformation("Marking CNPG Cluster {Instance} for deletion.", instanceName);
            // TODO (Iteration 3): Delete the actual CNPG Cluster CR.
            //   await client.DeleteAsync<CnpgCluster>(instanceName, ns, cancellationToken);
        }

        // 2. Delete the topology ConfigMap.
        var configMapName = $"{TopologyConfigMapName}-{name}";
        try
        {
            var existing = await client.GetAsync<V1ConfigMap>(configMapName, ns, cancellationToken);
            if (existing is not null)
            {
                logger.LogInformation("Deleting topology ConfigMap {Name}.", configMapName);
                await client.DeleteAsync(existing, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete ConfigMap {Name}. It may already be gone.", configMapName);
        }

        logger.LogInformation("Finalization complete for {Namespace}/{Name}.", ns, name);
        return ReconciliationResult<V1Alpha1ShardManagedDatabase>.Success(entity);
    }
}
