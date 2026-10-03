using Microsoft.Extensions.Logging;

namespace ReSharder.Operator.Services;

/// <summary>
/// Orchestrates the full logical replication lifecycle for shard migration:
/// schema copy, publication, subscription, lag monitoring, sequence sync, cleanup.
/// </summary>
public sealed class LogicalReplicationManager(
    PostgresExecutor pg,
    ILogger<LogicalReplicationManager> logger)
{
    /// <summary>
    /// Sets up logical replication for a single shard from source to target instance.
    /// Returns a <see cref="ReplicationHandle"/> for monitoring and cleanup.
    /// </summary>
    public async Task<ReplicationHandle> SetupReplicationAsync(
        string sourceInstance,
        string targetInstance,
        string ns,
        string shardName,
        CancellationToken ct)
    {
        var pubName = $"pub_migration_{shardName}";
        var subName = $"sub_migration_{shardName}";

        logger.LogInformation(
            "Setting up logical replication for shard {Shard}: {Source} -> {Target}.",
            shardName, sourceInstance, targetInstance);

        // Step 1: Create the empty database on the target.
        logger.LogInformation("Creating database {Db} on target {Target}.", shardName, targetInstance);
        await pg.ExecuteSqlAsync(targetInstance, ns, "postgres",
            $"CREATE DATABASE \"{shardName}\";", ct);

        // Step 2: Migrate schema from source to target.
        var schemaOk = await pg.MigrateSchemaAsync(sourceInstance, targetInstance, ns, shardName, ct);
        if (!schemaOk)
        {
            throw new InvalidOperationException(
                $"Schema migration failed for shard {shardName}.");
        }

        // Step 3: Create PUBLICATION on source (inside the shard's database).
        logger.LogInformation("Creating publication {Pub} on source {Source}.", pubName, sourceInstance);
        await pg.ExecuteSqlAsync(sourceInstance, ns, shardName,
            $"CREATE PUBLICATION \"{pubName}\" FOR ALL TABLES;", ct);

        // Step 4: Get superuser password for connection string.
        var sourcePassword = await pg.GetSuperuserPasswordAsync(sourceInstance, ns, ct);
        var sourceHost = $"{sourceInstance}-rw.{ns}.svc";
        var connStr = $"host={sourceHost} port=5432 dbname={shardName} user=postgres password={sourcePassword} sslmode=require";

        // Step 5: Create SUBSCRIPTION on target (inside the shard's database).
        logger.LogInformation("Creating subscription {Sub} on target {Target}.", subName, targetInstance);
        var subSql = $"CREATE SUBSCRIPTION \"{subName}\" CONNECTION '{connStr}' PUBLICATION \"{pubName}\";";
        await pg.ExecuteSqlNoTransactionAsync(targetInstance, ns, shardName, subSql, ct);

        logger.LogInformation(
            "Logical replication setup complete for shard {Shard}. Initial sync started.",
            shardName);

        return new ReplicationHandle(
            shardName, sourceInstance, targetInstance, ns, pubName, subName);
    }

    /// <summary>
    /// Checks the replication lag for a shard's subscription.
    /// Returns the lag in bytes (0 = fully caught up).
    /// Returns null if the status cannot be determined.
    /// </summary>
    public async Task<ReplicationLagInfo?> CheckReplicationLagAsync(
        ReplicationHandle handle,
        CancellationToken ct)
    {
        // Check on publisher side: compare current WAL LSN with slot's confirmed_flush_lsn.
        var lagSql = $"""
            SELECT
                pg_current_wal_lsn() - confirmed_flush_lsn AS lag_bytes
            FROM pg_replication_slots
            WHERE slot_name = '{handle.SubscriptionName}'
              AND active = true;
            """;

        var result = await pg.ExecuteSqlAsync(
            handle.SourceInstance, handle.Namespace, handle.ShardName, lagSql, ct);

        if (string.IsNullOrWhiteSpace(result))
        {
            logger.LogDebug(
                "No active replication slot found for {Sub} on {Source}.",
                handle.SubscriptionName, handle.SourceInstance);
            return null;
        }

        if (!long.TryParse(result.Trim(), out var lagBytes))
        {
            logger.LogWarning(
                "Could not parse lag bytes '{Result}' for {Sub}.",
                result, handle.SubscriptionName);
            return null;
        }

        // Also check if initial sync is complete on subscriber side.
        var syncSql = $"""
            SELECT count(*)
            FROM pg_subscription_rel
            WHERE srsubstate NOT IN ('r', 's');
            """;

        var syncResult = await pg.ExecuteSqlAsync(
            handle.TargetInstance, handle.Namespace, handle.ShardName, syncSql, ct);

        var tablesStillSyncing = 0;
        if (!string.IsNullOrWhiteSpace(syncResult))
            int.TryParse(syncResult.Trim(), out tablesStillSyncing);

        var info = new ReplicationLagInfo(lagBytes, tablesStillSyncing == 0);

        logger.LogInformation(
            "Replication lag for shard {Shard}: {LagBytes} bytes, initial sync complete: {SyncDone}.",
            handle.ShardName, info.LagBytes, info.InitialSyncComplete);

        return info;
    }

    /// <summary>
    /// Copies sequence values from source to target after replication has caught up.
    /// </summary>
    public async Task SyncSequencesAsync(ReplicationHandle handle, CancellationToken ct)
    {
        await pg.CopySequencesAsync(
            handle.SourceInstance, handle.TargetInstance, handle.Namespace, handle.ShardName, ct);
    }

    /// <summary>
    /// Tears down the logical replication infrastructure for a shard:
    /// disables and drops subscription, drops replication slot, drops publication.
    /// </summary>
    public async Task TeardownReplicationAsync(ReplicationHandle handle, CancellationToken ct)
    {
        logger.LogInformation(
            "Tearing down replication for shard {Shard}.", handle.ShardName);

        // 1. Disable subscription on target.
        await SafeExecute(() => pg.ExecuteSqlNoTransactionAsync(
            handle.TargetInstance, handle.Namespace, handle.ShardName,
            $"ALTER SUBSCRIPTION \"{handle.SubscriptionName}\" DISABLE;", ct),
            "Disable subscription");

        // 2. Detach from replication slot.
        await SafeExecute(() => pg.ExecuteSqlNoTransactionAsync(
            handle.TargetInstance, handle.Namespace, handle.ShardName,
            $"ALTER SUBSCRIPTION \"{handle.SubscriptionName}\" SET (slot_name = NONE);", ct),
            "Detach slot");

        // 3. Drop subscription.
        await SafeExecute(() => pg.ExecuteSqlNoTransactionAsync(
            handle.TargetInstance, handle.Namespace, handle.ShardName,
            $"DROP SUBSCRIPTION \"{handle.SubscriptionName}\";", ct),
            "Drop subscription");

        // 4. Drop replication slot on source (if still exists).
        await SafeExecute(() => pg.ExecuteSqlAsync(
            handle.SourceInstance, handle.Namespace, "postgres",
            $"SELECT pg_drop_replication_slot('{handle.SubscriptionName}') FROM pg_replication_slots WHERE slot_name = '{handle.SubscriptionName}' AND NOT active;", ct),
            "Drop replication slot");

        // 5. Drop publication on source.
        await SafeExecute(() => pg.ExecuteSqlAsync(
            handle.SourceInstance, handle.Namespace, handle.ShardName,
            $"DROP PUBLICATION \"{handle.PublicationName}\";", ct),
            "Drop publication");

        logger.LogInformation(
            "Replication teardown complete for shard {Shard}.", handle.ShardName);
    }

    /// <summary>
    /// Drops the shard database from the source instance after migration.
    /// Terminates active connections first.
    /// </summary>
    public async Task DropSourceDatabaseAsync(ReplicationHandle handle, CancellationToken ct)
    {
        logger.LogInformation(
            "Dropping database {Db} from source instance {Source}.",
            handle.ShardName, handle.SourceInstance);

        // Terminate all connections.
        await pg.ExecuteSqlAsync(handle.SourceInstance, handle.Namespace, "postgres",
            $"""
            SELECT pg_terminate_backend(pid)
            FROM pg_stat_activity
            WHERE datname = '{handle.ShardName}' AND pid <> pg_backend_pid();
            """, ct);

        // Drop the database.
        await pg.ExecuteSqlAsync(handle.SourceInstance, handle.Namespace, "postgres",
            $"DROP DATABASE \"{handle.ShardName}\";", ct);

        logger.LogInformation("Database {Db} dropped from {Source}.", handle.ShardName, handle.SourceInstance);
    }

    /// <summary>
    /// Drops the incomplete shard database from the target instance during rollback.
    /// Terminates any stray connections first.
    /// </summary>
    public async Task DropTargetDatabaseAsync(ReplicationHandle handle, CancellationToken ct)
    {
        logger.LogInformation(
            "Dropping database {Db} from target instance {Target} (rollback).",
            handle.ShardName, handle.TargetInstance);

        await SafeExecute(async () =>
        {
            await pg.ExecuteSqlAsync(handle.TargetInstance, handle.Namespace, "postgres",
                $"""
                SELECT pg_terminate_backend(pid)
                FROM pg_stat_activity
                WHERE datname = '{handle.ShardName}' AND pid <> pg_backend_pid();
                """, ct);

            await pg.ExecuteSqlAsync(handle.TargetInstance, handle.Namespace, "postgres",
                $"DROP DATABASE IF EXISTS \"{handle.ShardName}\";", ct);
        }, "Drop target database");

        logger.LogInformation("Target database {Db} dropped from {Target} during rollback.",
            handle.ShardName, handle.TargetInstance);
    }

    private async Task SafeExecute(Func<Task> action, string stepName)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Replication teardown step '{Step}' failed. Continuing.", stepName);
        }
    }
}

/// <summary>
/// Tracks the state of an active logical replication migration for a single shard.
/// </summary>
public sealed record ReplicationHandle(
    string ShardName,
    string SourceInstance,
    string TargetInstance,
    string Namespace,
    string PublicationName,
    string SubscriptionName);

/// <summary>
/// Information about the current replication lag for a subscription.
/// </summary>
public sealed record ReplicationLagInfo(
    long LagBytes,
    bool InitialSyncComplete)
{
    /// <summary>Lag is exactly zero -- safe for final cutover.</summary>
    public bool IsCaughtUp => InitialSyncComplete && LagBytes <= 0;

    /// <summary>Lag is below the configured threshold -- safe to enter drain mode.</summary>
    public bool IsWithinThreshold(long thresholdBytes) =>
        InitialSyncComplete && LagBytes <= thresholdBytes;
}
