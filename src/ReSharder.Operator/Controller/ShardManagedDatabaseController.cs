using k8s.Models;
using KubeOps.Abstractions.Rbac;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging;
using ReSharder.Operator.Entities;

namespace ReSharder.Operator.Controller;

/// <summary>
/// Main reconciliation controller for <see cref="V1Alpha1ShardManagedDatabase"/> resources.
/// Implements the core lifecycle: initial provisioning, PVC monitoring, split/scale-up triggers.
/// </summary>
[EntityRbac(typeof(V1Alpha1ShardManagedDatabase), Verbs = RbacVerb.All)]
[EntityRbac(typeof(V1ConfigMap), Verbs = RbacVerb.Get | RbacVerb.List | RbacVerb.Create | RbacVerb.Update | RbacVerb.Patch | RbacVerb.Delete)]
public sealed class ShardManagedDatabaseController(
    IKubernetesClient client,
    ILogger<ShardManagedDatabaseController> logger)
    : IEntityController<V1Alpha1ShardManagedDatabase>
{
    private const string TopologyConfigMapName = "app-shard-topology";
    private const string DefaultInstancePrefix = "smd-instance";

    public async Task<ReconciliationResult<V1Alpha1ShardManagedDatabase>> ReconcileAsync(
        V1Alpha1ShardManagedDatabase entity,
        CancellationToken cancellationToken)
    {
        var ns = entity.Namespace();
        var name = entity.Name();
        logger.LogInformation(
            "Reconciling ShardManagedDatabase {Namespace}/{Name}, generation={Generation}, phase={Phase}.",
            ns, name, entity.Metadata.Generation, entity.Status.Phase);

        // Step 1: Ensure the initial shard mapping is populated.
        if (entity.Status.ShardMapping.Count == 0)
        {
            entity = await InitializeShardMapping(entity, cancellationToken);
        }

        // Step 2: Ensure the topology ConfigMap exists and is consistent.
        await EnsureTopologyConfigMap(entity, cancellationToken);

        // Step 3: Mark observed generation.
        entity.Status.ObservedGeneration = entity.Metadata.Generation;
        entity = await client.UpdateStatusAsync(entity, cancellationToken);

        // Requeue every 30 seconds for PVC monitoring.
        // Iteration 3+ will add actual PVC size checks here.
        return ReconciliationResult<V1Alpha1ShardManagedDatabase>.Success(
            entity, requeueAfter: TimeSpan.FromSeconds(30));
    }

    public Task<ReconciliationResult<V1Alpha1ShardManagedDatabase>> DeletedAsync(
        V1Alpha1ShardManagedDatabase entity,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "ShardManagedDatabase {Namespace}/{Name} deleted.",
            entity.Namespace(), entity.Name());
        return Task.FromResult(
            ReconciliationResult<V1Alpha1ShardManagedDatabase>.Success(entity));
    }

    /// <summary>
    /// Assigns all declared shards to the first CNPG instance and populates shardMapping.
    /// </summary>
    private async Task<V1Alpha1ShardManagedDatabase> InitializeShardMapping(
        V1Alpha1ShardManagedDatabase entity,
        CancellationToken cancellationToken)
    {
        var instanceName = $"{DefaultInstancePrefix}-{entity.Name()}-1";
        logger.LogInformation(
            "Initializing shard mapping: all {Count} shards → {Instance}.",
            entity.Spec.Shards.Count, instanceName);

        foreach (var shard in entity.Spec.Shards)
        {
            entity.Status.ShardMapping[shard] = instanceName;
        }

        entity.Status.Phase = V1Alpha1ShardManagedDatabase.V1Alpha1Status.PhaseIdle;
        entity = await client.UpdateStatusAsync(entity, cancellationToken);

        // TODO (Iteration 3): Create the actual CNPG Cluster CR here.

        return entity;
    }

    /// <summary>
    /// Creates or updates the topology ConfigMap that application pods consume
    /// to discover shard connection strings.
    /// </summary>
    private async Task EnsureTopologyConfigMap(
        V1Alpha1ShardManagedDatabase entity,
        CancellationToken cancellationToken)
    {
        var ns = entity.Namespace();
        var configMapName = $"{TopologyConfigMapName}-{entity.Name()}";

        var data = new Dictionary<string, string>();
        foreach (var (shard, instance) in entity.Status.ShardMapping)
        {
            // Each shard gets two keys: its connection host and its status.
            // The CNPG service for an instance follows the naming: {instance}-rw.{namespace}.svc
            data[$"{shard}.host"] = $"{instance}-rw.{ns}.svc";
            data[$"{shard}.port"] = "5432";
            data[$"{shard}.database"] = shard;
            data[$"{shard}.status"] = "Active";
        }

        var existing = await client.GetAsync<V1ConfigMap>(configMapName, ns, cancellationToken);
        if (existing is null)
        {
            logger.LogInformation("Creating topology ConfigMap {Name} in {Namespace}.", configMapName, ns);
            var cm = new V1ConfigMap
            {
                Metadata = new V1ObjectMeta
                {
                    Name = configMapName,
                    NamespaceProperty = ns,
                    Labels = new Dictionary<string, string>
                    {
                        ["app.kubernetes.io/managed-by"] = "resharder",
                        ["app.kubernetes.io/part-of"] = "resharder",
                        ["resharder.io/owner"] = entity.Name(),
                    },
                    OwnerReferences =
                    [
                        new V1OwnerReference
                        {
                            ApiVersion = entity.ApiVersion,
                            Kind = entity.Kind,
                            Name = entity.Name(),
                            Uid = entity.Uid(),
                            Controller = true,
                            BlockOwnerDeletion = true,
                        },
                    ],
                },
                Data = data,
            };
            await client.CreateAsync(cm, cancellationToken);
        }
        else
        {
            if (!DataEquals(existing.Data, data))
            {
                logger.LogInformation("Updating topology ConfigMap {Name} in {Namespace}.", configMapName, ns);
                existing.Data = data;
                await client.UpdateAsync(existing, cancellationToken);
            }
        }
    }

    private static bool DataEquals(IDictionary<string, string>? a, IDictionary<string, string>? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        if (a.Count != b.Count) return false;
        foreach (var (key, value) in a)
        {
            if (!b.TryGetValue(key, out var other) || value != other)
                return false;
        }
        return true;
    }
}
