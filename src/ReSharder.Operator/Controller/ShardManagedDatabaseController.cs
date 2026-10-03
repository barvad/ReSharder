using k8s.Models;
using KubeOps.Abstractions.Rbac;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging;
using ReSharder.Operator.Entities;
using ReSharder.Operator.Services;

namespace ReSharder.Operator.Controller;

using Status = V1Alpha1ShardManagedDatabase.V1Alpha1Status;

/// <summary>
/// Main reconciliation controller for <see cref="V1Alpha1ShardManagedDatabase"/> resources.
/// Implements the core lifecycle: initial provisioning, PVC monitoring, split/scale-up triggers.
/// </summary>
[EntityRbac(typeof(V1Alpha1ShardManagedDatabase), Verbs = RbacVerb.All)]
[EntityRbac(typeof(V1ConfigMap), Verbs = RbacVerb.Get | RbacVerb.List | RbacVerb.Create | RbacVerb.Update | RbacVerb.Patch | RbacVerb.Delete)]
public sealed class ShardManagedDatabaseController(
    IKubernetesClient client,
    CnpgClusterManager cnpg,
    PvcMonitor pvcMonitor,
    ILogger<ShardManagedDatabaseController> logger)
    : IEntityController<V1Alpha1ShardManagedDatabase>
{
    private const string TopologyConfigMapPrefix = "app-shard-topology";
    private const string DefaultInstancePrefix = "smd-instance";

    private static readonly TimeSpan RequeueInterval = TimeSpan.FromSeconds(30);
    private const string StorageScaleIncrement = "20Gi";

    public async Task<ReconciliationResult<V1Alpha1ShardManagedDatabase>> ReconcileAsync(
        V1Alpha1ShardManagedDatabase entity,
        CancellationToken cancellationToken)
    {
        var ns = entity.Namespace();
        var name = entity.Name();
        logger.LogInformation(
            "Reconciling ShardManagedDatabase {Namespace}/{Name}, generation={Generation}, phase={Phase}.",
            ns, name, entity.Metadata.Generation, entity.Status.Phase);

        // If a migration or cleanup is already in progress, don't interfere.
        if (entity.Status.Phase is Status.PhaseMigrating or Status.PhaseCleaning)
        {
            logger.LogInformation("Phase is {Phase}, skipping reconciliation.", entity.Status.Phase);
            return ReconciliationResult<V1Alpha1ShardManagedDatabase>.Success(
                entity, requeueAfter: RequeueInterval);
        }

        // Step 1: Ensure the initial shard mapping and CNPG cluster exist.
        if (entity.Status.ShardMapping.Count == 0)
        {
            entity = await InitializeShardMapping(entity, cancellationToken);
        }

        // Step 2: Ensure all CNPG clusters referenced in the mapping exist.
        await EnsureCnpgClusters(entity, cancellationToken);

        // Step 3: Check PVC usage for each instance and trigger split if needed.
        entity = await CheckAndTriggerSplit(entity, cancellationToken);

        // Step 4: Ensure the topology ConfigMap is consistent.
        await EnsureTopologyConfigMap(entity, cancellationToken);

        // Step 5: Mark observed generation.
        entity.Status.ObservedGeneration = entity.Metadata.Generation;
        entity = await client.UpdateStatusAsync(entity, cancellationToken);

        return ReconciliationResult<V1Alpha1ShardManagedDatabase>.Success(
            entity, requeueAfter: RequeueInterval);
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
    /// Assigns all declared shards to the first CNPG instance.
    /// </summary>
    private async Task<V1Alpha1ShardManagedDatabase> InitializeShardMapping(
        V1Alpha1ShardManagedDatabase entity,
        CancellationToken ct)
    {
        var instanceName = $"{DefaultInstancePrefix}-{entity.Name()}-1";
        logger.LogInformation(
            "Initializing shard mapping: all {Count} shards -> {Instance}.",
            entity.Spec.Shards.Count, instanceName);

        foreach (var shard in entity.Spec.Shards)
        {
            entity.Status.ShardMapping[shard] = instanceName;
        }

        entity.Status.Phase = Status.PhaseIdle;
        return await client.UpdateStatusAsync(entity, ct);
    }

    /// <summary>
    /// Ensures that a CNPG Cluster CR exists for every unique instance in the shard mapping.
    /// </summary>
    private async Task EnsureCnpgClusters(
        V1Alpha1ShardManagedDatabase entity,
        CancellationToken ct)
    {
        var ns = entity.Namespace();
        var instances = entity.Status.ShardMapping.Values.Distinct();

        foreach (var instanceName in instances)
        {
            await cnpg.EnsureClusterAsync(
                instanceName, ns, entity.Spec.InitialStorageSize, entity, ct);
        }
    }

    /// <summary>
    /// Checks PVC usage for every CNPG instance and triggers a split or scale-up if needed.
    /// </summary>
    private async Task<V1Alpha1ShardManagedDatabase> CheckAndTriggerSplit(
        V1Alpha1ShardManagedDatabase entity,
        CancellationToken ct)
    {
        var ns = entity.Namespace();
        var maxBytes = CnpgClusterManager.ParseStorageToBytes(entity.Spec.MaxShardSize);
        var instances = entity.Status.ShardMapping.Values.Distinct().ToList();

        foreach (var instanceName in instances)
        {
            var usage = await pvcMonitor.GetCnpgInstanceUsageAsync(instanceName, ns, ct);
            if (usage is null)
            {
                logger.LogDebug(
                    "Could not get PVC usage for instance {Instance}. Skipping.",
                    instanceName);
                continue;
            }

            logger.LogInformation(
                "Instance {Instance}: used={UsedMi}Mi, capacity={CapMi}Mi, threshold={ThreshMi}Mi.",
                instanceName,
                usage.UsedBytes / (1024 * 1024),
                usage.CapacityBytes / (1024 * 1024),
                maxBytes / (1024 * 1024));

            if (usage.UsedBytes < maxBytes)
                continue;

            logger.LogWarning(
                "Instance {Instance} exceeds maxShardSize ({Used} >= {Max}). Planning split.",
                instanceName, usage.UsedBytes, maxBytes);

            var plan = ShardSplitPlanner.PlanSplit(
                instanceName,
                entity.Status.ShardMapping,
                ShardSplitPlanner.GenerateNextInstanceName(
                    entity.Name(), entity.Status.ShardMapping.Values.Distinct()));

            switch (plan.Action)
            {
                case SplitAction.Split:
                    entity = await ExecuteSplitPlan(entity, plan, ct);
                    return entity;

                case SplitAction.ScaleUp:
                    await ExecuteScaleUp(entity, plan, ct);
                    return entity;
            }
        }

        return entity;
    }

    /// <summary>
    /// Executes a shard split: creates a new CNPG instance and transitions to Migrating phase.
    /// The actual data migration (logical replication) will be handled in Iteration 4.
    /// </summary>
    private async Task<V1Alpha1ShardManagedDatabase> ExecuteSplitPlan(
        V1Alpha1ShardManagedDatabase entity,
        SplitPlan plan,
        CancellationToken ct)
    {
        var ns = entity.Namespace();

        logger.LogInformation(
            "Executing SPLIT: moving shards [{Shards}] from {Source} to {Target}.",
            string.Join(", ", plan.ShardsToMove),
            plan.SourceInstance,
            plan.TargetInstance);

        // 1. Create the new CNPG Cluster.
        await cnpg.EnsureClusterAsync(
            plan.TargetInstance!, ns, entity.Spec.InitialStorageSize, entity, ct);

        // 2. Update shard mapping to point moved shards to the new instance.
        foreach (var shard in plan.ShardsToMove)
        {
            entity.Status.ShardMapping[shard] = plan.TargetInstance!;
        }

        // 3. Transition to Migrating phase.
        //    Iteration 4 will add PUBLICATION/SUBSCRIPTION logical replication here.
        //    For now, we update the mapping and mark the phase.
        entity.Status.Phase = Status.PhaseMigrating;
        entity = await client.UpdateStatusAsync(entity, ct);

        logger.LogInformation(
            "Split planned. Phase set to Migrating. Shard mapping updated.");

        // TODO (Iteration 4): Start logical replication for plan.ShardsToMove.
        // TODO (Iteration 5): Traffic cutover via ConfigMap.
        // For now, immediately transition back to Idle since no real migration happens yet.
        entity.Status.Phase = Status.PhaseIdle;
        entity = await client.UpdateStatusAsync(entity, ct);

        return entity;
    }

    /// <summary>
    /// Executes vertical scaling: increases PVC size for a single-shard instance.
    /// </summary>
    private async Task ExecuteScaleUp(
        V1Alpha1ShardManagedDatabase entity,
        SplitPlan plan,
        CancellationToken ct)
    {
        var ns = entity.Namespace();

        logger.LogInformation(
            "Executing SCALE-UP: expanding storage for single-shard instance {Instance} by +{Increment}.",
            plan.SourceInstance, StorageScaleIncrement);

        await cnpg.ScaleStorageAsync(plan.SourceInstance, ns, StorageScaleIncrement, ct);

        logger.LogInformation("Scale-up complete for instance {Instance}.", plan.SourceInstance);
    }

    /// <summary>
    /// Creates or updates the topology ConfigMap consumed by application pods.
    /// </summary>
    private async Task EnsureTopologyConfigMap(
        V1Alpha1ShardManagedDatabase entity,
        CancellationToken ct)
    {
        var ns = entity.Namespace();
        var configMapName = $"{TopologyConfigMapPrefix}-{entity.Name()}";

        var data = new Dictionary<string, string>();
        foreach (var (shard, instance) in entity.Status.ShardMapping)
        {
            data[$"{shard}.host"] = $"{instance}-rw.{ns}.svc";
            data[$"{shard}.port"] = "5432";
            data[$"{shard}.database"] = shard;
            data[$"{shard}.status"] = "Active";
        }

        var existing = await client.GetAsync<V1ConfigMap>(configMapName, ns, ct);
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
            await client.CreateAsync(cm, ct);
        }
        else
        {
            if (!DataEquals(existing.Data, data))
            {
                logger.LogInformation("Updating topology ConfigMap {Name} in {Namespace}.", configMapName, ns);
                existing.Data = data;
                await client.UpdateAsync(existing, ct);
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
