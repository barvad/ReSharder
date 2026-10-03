using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;
using ReSharder.Operator.Entities;

namespace ReSharder.Operator.Services;

/// <summary>
/// State machine that orchestrates the full shard migration lifecycle.
/// Two-phase lag approach:
///   Replicating  -- wait for lag < threshold
///   Draining     -- set ConfigMap to Maintenance, wait drainWaitSeconds, then verify lag = 0
///   CaughtUp     -- sync sequences, teardown replication
///   CutoverDone  -- drop source databases
/// </summary>
public sealed class MigrationOrchestrator(
    LogicalReplicationManager replication,
    ILogger<MigrationOrchestrator> logger)
{
    /// <summary>
    /// Processes the current migration step.
    /// Called on each reconciliation tick while phase is Migrating.
    /// </summary>
    public async Task<MigrationStepResult> ProcessMigrationStepAsync(
        V1Alpha1ShardManagedDatabase entity,
        SplitPlan plan,
        CancellationToken ct)
    {
        var migration = entity.Status.ActiveMigration;

        if (migration is null || migration.Step == MigrationStep.NotStarted)
            return await StartReplication(entity, plan, ct);

        if (migration.Step == MigrationStep.Replicating)
            return await CheckReplicationProgress(entity, ct);

        if (migration.Step == MigrationStep.Draining)
            return await CheckDrainComplete(entity, ct);

        if (migration.Step == MigrationStep.CaughtUp)
            return await PerformCutover(entity, ct);

        if (migration.Step == MigrationStep.CutoverDone)
            return await CleanupMigration(entity, ct);

        logger.LogWarning("Unknown migration step {Step}.", migration?.Step);
        return MigrationStepResult.Continue;
    }

    /// <summary>
    /// Sets the shard status in the topology ConfigMap.
    /// Injected by the controller so the orchestrator doesn't own ConfigMap logic.
    /// </summary>
    public Func<IReadOnlyList<string>, string, CancellationToken, Task>? SetShardTopologyStatus { get; set; }

    // -- Step 1: Start --

    private async Task<MigrationStepResult> StartReplication(
        V1Alpha1ShardManagedDatabase entity,
        SplitPlan plan,
        CancellationToken ct)
    {
        var ns = entity.Namespace();
        var handles = new List<string>();

        foreach (var shard in plan.ShardsToMove)
        {
            logger.LogInformation("Starting replication for shard {Shard}.", shard);
            await replication.SetupReplicationAsync(
                plan.SourceInstance, plan.TargetInstance!, ns, shard, ct);
            handles.Add(shard);
        }

        entity.Status.ActiveMigration = new ActiveMigrationState
        {
            Step = MigrationStep.Replicating,
            SourceInstance = plan.SourceInstance,
            TargetInstance = plan.TargetInstance!,
            ShardsInFlight = handles,
        };

        return MigrationStepResult.Continue;
    }

    // -- Step 2: Wait for lag < threshold --

    private async Task<MigrationStepResult> CheckReplicationProgress(
        V1Alpha1ShardManagedDatabase entity,
        CancellationToken ct)
    {
        var ns = entity.Namespace();
        var migration = entity.Status.ActiveMigration!;
        var threshold = entity.Spec.LagThresholdBytes;
        var allWithinThreshold = true;

        foreach (var shard in migration.ShardsInFlight)
        {
            var handle = BuildHandle(shard, migration, ns);
            var lag = await replication.CheckReplicationLagAsync(handle, ct);

            if (lag is null || !lag.IsWithinThreshold(threshold))
            {
                logger.LogInformation(
                    "Shard {Shard} lag not within threshold: lag={Lag}b, threshold={Thresh}b, syncDone={Sync}.",
                    shard, lag?.LagBytes ?? -1, threshold, lag?.InitialSyncComplete ?? false);
                allWithinThreshold = false;
            }
        }

        if (allWithinThreshold)
        {
            logger.LogInformation(
                "All shards within lag threshold ({Threshold}b). Setting Maintenance and entering Drain.",
                threshold);

            // Set shards to Maintenance in the topology ConfigMap.
            if (SetShardTopologyStatus is not null)
                await SetShardTopologyStatus(migration.ShardsInFlight, "Maintenance", ct);

            migration.Step = MigrationStep.Draining;
            migration.MaintenanceSetAt = DateTime.UtcNow;
        }

        return MigrationStepResult.Continue;
    }

    // -- Step 3: Drain -- wait for writes to stop, then verify lag = 0 --

    private async Task<MigrationStepResult> CheckDrainComplete(
        V1Alpha1ShardManagedDatabase entity,
        CancellationToken ct)
    {
        var ns = entity.Namespace();
        var migration = entity.Status.ActiveMigration!;
        var drainWait = TimeSpan.FromSeconds(entity.Spec.DrainWaitSeconds);

        // Enforce drain wait period.
        var elapsed = DateTime.UtcNow - (migration.MaintenanceSetAt ?? DateTime.UtcNow);
        if (elapsed < drainWait)
        {
            logger.LogInformation(
                "Drain wait: {Elapsed:F1}s / {Required}s elapsed. Waiting.",
                elapsed.TotalSeconds, drainWait.TotalSeconds);
            return MigrationStepResult.Continue;
        }

        // After drain wait, check for exact zero lag.
        var allCaughtUp = true;
        foreach (var shard in migration.ShardsInFlight)
        {
            var handle = BuildHandle(shard, migration, ns);
            var lag = await replication.CheckReplicationLagAsync(handle, ct);

            if (lag is null || !lag.IsCaughtUp)
            {
                logger.LogInformation(
                    "Shard {Shard} not yet at zero lag after drain: lag={Lag}b.",
                    shard, lag?.LagBytes ?? -1);
                allCaughtUp = false;
            }
        }

        if (allCaughtUp)
        {
            logger.LogInformation("All shards at zero lag after drain. Transitioning to CaughtUp.");
            migration.Step = MigrationStep.CaughtUp;
        }
        else
        {
            logger.LogInformation("Lag not zero yet after drain. Will retry next tick.");
        }

        return MigrationStepResult.Continue;
    }

    // -- Step 4: Cutover -- sync sequences, teardown replication --

    private async Task<MigrationStepResult> PerformCutover(
        V1Alpha1ShardManagedDatabase entity,
        CancellationToken ct)
    {
        var ns = entity.Namespace();
        var migration = entity.Status.ActiveMigration!;

        logger.LogInformation("Performing cutover for shards [{Shards}].",
            string.Join(", ", migration.ShardsInFlight));

        foreach (var shard in migration.ShardsInFlight)
        {
            var handle = BuildHandle(shard, migration, ns);
            await replication.SyncSequencesAsync(handle, ct);
            await replication.TeardownReplicationAsync(handle, ct);
        }

        migration.Step = MigrationStep.CutoverDone;
        return MigrationStepResult.Continue;
    }

    // -- Step 5: Cleanup -- drop source databases --

    private async Task<MigrationStepResult> CleanupMigration(
        V1Alpha1ShardManagedDatabase entity,
        CancellationToken ct)
    {
        var ns = entity.Namespace();
        var migration = entity.Status.ActiveMigration!;

        logger.LogInformation("Cleaning up source databases for shards [{Shards}].",
            string.Join(", ", migration.ShardsInFlight));

        foreach (var shard in migration.ShardsInFlight)
        {
            var handle = BuildHandle(shard, migration, ns);
            await replication.DropSourceDatabaseAsync(handle, ct);
        }

        entity.Status.ActiveMigration = null;
        return MigrationStepResult.Completed;
    }

    // -- Helpers --

    private static ReplicationHandle BuildHandle(
        string shard, ActiveMigrationState migration, string ns) =>
        new(shard, migration.SourceInstance, migration.TargetInstance, ns,
            $"pub_migration_{shard}", $"sub_migration_{shard}");
}

public enum MigrationStepResult
{
    Continue,
    Completed,
}
