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
/// Implements the core lifecycle: provisioning, PVC monitoring, split/scale-up, migration.
/// </summary>
[EntityRbac(typeof(V1Alpha1ShardManagedDatabase), Verbs = RbacVerb.All)]
[EntityRbac(typeof(V1ConfigMap), Verbs = RbacVerb.Get | RbacVerb.List | RbacVerb.Create | RbacVerb.Update | RbacVerb.Patch | RbacVerb.Delete)]
[EntityRbac(typeof(V1Secret), Verbs = RbacVerb.Get | RbacVerb.List)]
public sealed class ShardManagedDatabaseController(
    IKubernetesClient client,
    CnpgClusterManager cnpg,
    PvcMonitor pvcMonitor,
    MigrationOrchestrator migrationOrchestrator,
    ILogger<ShardManagedDatabaseController> logger)
    : IEntityController<V1Alpha1ShardManagedDatabase>
{
    private const string TopologyConfigMapPrefix = "app-shard-topology";
    private const string DefaultInstancePrefix = "smd-instance";

    private static readonly TimeSpan RequeueInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MigrationPollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CleaningPollInterval = TimeSpan.FromSeconds(10);
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

        // Handle active migration (Migrating phase).
        if (entity.Status.Phase == Status.PhaseMigrating)
        {
            entity = await HandleMigrationPhase(entity, cancellationToken);
            entity = await client.UpdateStatusAsync(entity, cancellationToken);
            return ReconciliationResult<V1Alpha1ShardManagedDatabase>.Success(
                entity, requeueAfter: MigrationPollInterval);
        }

        // Handle Cleaning phase -- wait for disk space to be freed.
        if (entity.Status.Phase == Status.PhaseCleaning)
        {
            entity = await HandleCleaningPhase(entity, cancellationToken);
            entity = await client.UpdateStatusAsync(entity, cancellationToken);
            return ReconciliationResult<V1Alpha1ShardManagedDatabase>.Success(
                entity, requeueAfter: CleaningPollInterval);
        }

        // -- Idle phase --

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

    // -- Migration Phase --

    private async Task<V1Alpha1ShardManagedDatabase> HandleMigrationPhase(
        V1Alpha1ShardManagedDatabase entity,
        CancellationToken ct)
    {
        var migration = entity.Status.ActiveMigration;
        if (migration is null)
        {
            logger.LogWarning("Phase is Migrating but no ActiveMigration state. Resetting to Idle.");
            entity.Status.Phase = Status.PhaseIdle;
            return entity;
        }

        var plan = SplitPlan.Split(
            migration.SourceInstance,
            migration.TargetInstance,
            [],
            migration.ShardsInFlight);

        // Wire up the topology status callback so the orchestrator can
        // set shards to "Maintenance" without owning ConfigMap logic.
        migrationOrchestrator.SetShardTopologyStatus = (shards, status, token) =>
            UpdateShardTopologyStatus(entity, shards, status, token);

        var sourceInstance = migration.SourceInstance;

        var result = await migrationOrchestrator.ProcessMigrationStepAsync(entity, plan, ct);

        if (result == MigrationStepResult.Completed)
        {
            logger.LogInformation(
                "Migration completed. Transitioning to Cleaning phase for source instance {Instance}.",
                sourceInstance);
            entity.Status.Phase = Status.PhaseCleaning;
            entity.Status.Cleaning = new CleaningState
            {
                Instance = sourceInstance,
                StartedAt = DateTime.UtcNow,
            };

            // Restore all shards to Active with new connection info.
            await EnsureTopologyConfigMap(entity, ct);
        }

        return entity;
    }

    // -- Cleaning Phase --

    private async Task<V1Alpha1ShardManagedDatabase> HandleCleaningPhase(
        V1Alpha1ShardManagedDatabase entity,
        CancellationToken ct)
    {
        var cleaning = entity.Status.Cleaning;
        if (cleaning is null || string.IsNullOrEmpty(cleaning.Instance))
        {
            logger.LogWarning("Phase is Cleaning but no Cleaning state found. Transitioning to Idle.");
            entity.Status.Cleaning = null;
            entity.Status.Phase = Status.PhaseIdle;
            return entity;
        }

        var ns = entity.Namespace();
        var maxBytes = CnpgClusterManager.ParseStorageToBytes(entity.Spec.MaxShardSize);
        var usage = await pvcMonitor.GetCnpgInstanceUsageAsync(cleaning.Instance, ns, ct);

        if (usage is null)
        {
            logger.LogWarning(
                "Cleaning phase: could not fetch PVC usage for instance {Instance}. Will retry.",
                cleaning.Instance);
            return entity;
        }

        logger.LogInformation(
            "Cleaning phase instance {Instance}: used={UsedMi}Mi, threshold={ThreshMi}Mi.",
            cleaning.Instance,
            usage.UsedBytes / (1024 * 1024),
            maxBytes / (1024 * 1024));

        if (usage.UsedBytes < maxBytes)
        {
            logger.LogInformation(
                "Cleaning phase complete: instance {Instance} disk usage ({UsedMi}Mi) dropped below maxShardSize ({ThreshMi}Mi). Transitioning to Idle.",
                cleaning.Instance,
                usage.UsedBytes / (1024 * 1024),
                maxBytes / (1024 * 1024));

            entity.Status.Cleaning = null;
            entity.Status.Phase = Status.PhaseIdle;
            return entity;
        }

        var elapsed = DateTime.UtcNow - cleaning.StartedAt;
        var timeout = TimeSpan.FromSeconds(entity.Spec.CleaningTimeoutSeconds);

        if (elapsed >= timeout)
        {
            logger.LogWarning(
                "Cleaning phase timed out after {Elapsed:F0}s (timeout {Timeout}s) for instance {Instance}. Disk used {UsedMi}Mi >= threshold {ThreshMi}Mi. Transitioning to Idle.",
                elapsed.TotalSeconds,
                timeout.TotalSeconds,
                cleaning.Instance,
                usage.UsedBytes / (1024 * 1024),
                maxBytes / (1024 * 1024));

            entity.Status.Cleaning = null;
            entity.Status.Phase = Status.PhaseIdle;
            return entity;
        }

        logger.LogInformation(
            "Cleaning phase in progress for instance {Instance} ({Elapsed:F0}s/{Timeout:F0}s elapsed). Waiting for disk space to be reclaimed.",
            cleaning.Instance,
            elapsed.TotalSeconds,
            timeout.TotalSeconds);

        return entity;
    }

    // -- Initialization --

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

    // -- Split / Scale-Up Trigger --

    /// <summary>
    /// Checks PVC usage for every CNPG instance and triggers a split or scale-up
    /// for the FIRST overloaded instance found.
    /// <para>
    /// Only one split/scale-up per reconciliation tick -- this is intentional:
    /// <list type="bullet">
    ///   <item>Avoids massive concurrent network and disk load.</item>
    ///   <item>After this split completes (Migrating -> Cleaning -> Idle), the next
    ///         tick will find the next overloaded instance and split that one.</item>
    ///   <item>Each instance is checked independently -- instance-2 that was created
    ///         by a previous split will be checked and split again if it overflows.</item>
    /// </list>
    /// </para>
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
                    // Return immediately -- one split at a time.
                    return entity;

                case SplitAction.ScaleUp:
                    await ExecuteScaleUp(entity, plan, ct);
                    // Return immediately -- one action at a time.
                    return entity;
            }
        }

        return entity;
    }

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

        // 3. Initialize migration state and transition to Migrating phase.
        entity.Status.ActiveMigration = new ActiveMigrationState
        {
            Step = MigrationStep.NotStarted,
            SourceInstance = plan.SourceInstance,
            TargetInstance = plan.TargetInstance!,
            ShardsInFlight = plan.ShardsToMove.ToList(),
        };
        entity.Status.Phase = Status.PhaseMigrating;
        entity = await client.UpdateStatusAsync(entity, ct);

        logger.LogInformation(
            "Split initiated. Phase set to Migrating. Next reconcile will start replication.");

        return entity;
    }

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

    // -- Topology ConfigMap --

    /// <summary>
    /// Sets the status of specific shards in the topology ConfigMap
    /// (e.g. "Maintenance" to stop writes, "Active" to resume).
    /// </summary>
    private async Task UpdateShardTopologyStatus(
        V1Alpha1ShardManagedDatabase entity,
        IReadOnlyList<string> shards,
        string status,
        CancellationToken ct)
    {
        var ns = entity.Namespace();
        var configMapName = $"{TopologyConfigMapPrefix}-{entity.Name()}";

        var existing = await client.GetAsync<V1ConfigMap>(configMapName, ns, ct);
        if (existing?.Data is null) return;

        var changed = false;
        foreach (var shard in shards)
        {
            var key = $"{shard}.status";
            if (existing.Data.TryGetValue(key, out var current) && current != status)
            {
                existing.Data[key] = status;
                changed = true;
            }
        }

        if (changed)
        {
            logger.LogInformation(
                "Setting shards [{Shards}] to '{Status}' in ConfigMap {Name}.",
                string.Join(", ", shards), status, configMapName);
            await client.UpdateAsync(existing, ct);
        }
    }

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
