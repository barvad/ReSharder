using System.Diagnostics;
using System.Text.Json;
using k8s.Models;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging;
using ReSharder.Operator.Entities;
using ReSharder.Operator.Services;

namespace ReSharder.E2E;

public sealed record ShardBaseline(long RowCount, string Checksum, long MaxId);

public sealed class E2ETestRunner(
    IKubernetesClient client,
    PostgresExecutor pg,
    ILogger<E2ETestRunner> logger)
{
    private const string TableName = "e2e_orders";

    public async Task<bool> RunAsync(E2EVerificationOptions opts, CancellationToken ct)
    {
        Console.WriteLine();
        Console.WriteLine("============================================================");
        Console.WriteLine("       ReSharder End-to-End Shard Split & Data Verification ");
        Console.WriteLine("============================================================");
        Console.WriteLine($"Target Namespace : {opts.Namespace}");
        Console.WriteLine($"Database CR Name : {opts.CrName}");
        Console.WriteLine($"Max Shard Size   : {opts.MaxShardSize}");
        Console.WriteLine($"Shards           : [{string.Join(", ", opts.Shards)}]");
        Console.WriteLine($"Timeout          : {opts.Timeout.TotalSeconds:F0}s");
        Console.WriteLine("============================================================");
        Console.WriteLine();

        var sw = Stopwatch.StartNew();

        try
        {
            // Step 1: Ensure CR exists and initial cluster is ready
            var cr = await EnsureShardManagedDatabaseAsync(opts, ct);
            var initialInstance = cr.Status.ShardMapping[opts.Shards[0]];
            await WaitForPodReadyAsync(initialInstance, opts.Namespace, ct);

            // Step 2: Seed baseline test data and compute checksums
            var baselines = await SeedBaselineDataAsync(initialInstance, opts, ct);

            // Step 3: Trigger shard split by writing heavy volume into the last shard
            var overloadShard = opts.Shards[^1];
            await OverloadShardToTriggerSplitAsync(initialInstance, overloadShard, opts, baselines, ct);

            // Step 4: Monitor migration phases and verify Maintenance during Draining
            var splitResult = await MonitorMigrationPhasesAsync(opts, ct);
            if (!splitResult)
            {
                LogError("Migration did not complete successfully within the timeout window.");
                return false;
            }

            // Step 5: Verify zero data loss and data integrity across both instances
            var verifyResult = await VerifyDataIntegrityAsync(opts, baselines, ct);
            if (!verifyResult)
            {
                LogError("Data verification failed! Data mismatch or sequence desynchronization detected.");
                return false;
            }

            // Step 6: Print Final Success Summary
            PrintFinalReport(sw.Elapsed, opts, baselines);
            return true;
        }
        catch (Exception ex)
        {
            LogError($"Unhandled exception during E2E test execution: {ex.Message}");
            logger.LogError(ex, "E2E run failed with exception.");
            return false;
        }
    }

    private async Task<V1Alpha1ShardManagedDatabase> EnsureShardManagedDatabaseAsync(
        E2EVerificationOptions opts,
        CancellationToken ct)
    {
        LogSection("Step 1: Checking ShardManagedDatabase Resource");

        var existing = await client.GetAsync<V1Alpha1ShardManagedDatabase>(opts.CrName, opts.Namespace, ct);
        if (existing is null)
        {
            LogInfo($"Creating ShardManagedDatabase CR '{opts.CrName}'...");
            var cr = new V1Alpha1ShardManagedDatabase
            {
                Metadata = new V1ObjectMeta
                {
                    Name = opts.CrName,
                    NamespaceProperty = opts.Namespace,
                },
                Spec = new V1Alpha1ShardManagedDatabase.V1Alpha1Spec
                {
                    MaxShardSize = opts.MaxShardSize,
                    InitialStorageSize = opts.InitialStorageSize,
                    Shards = opts.Shards.ToList(),
                    LagThresholdBytes = 1048576,
                    DrainWaitSeconds = 5,
                    DrainTimeoutSeconds = 60,
                },
            };

            await client.CreateAsync(cr, ct);
            LogSuccess($"CR '{opts.CrName}' created successfully.");
        }
        else
        {
            LogInfo($"CR '{opts.CrName}' already exists.");
        }

        LogInfo("Waiting for operator to initialize shard mapping and initial CNPG cluster...");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);

        while (DateTime.UtcNow < deadline)
        {
            var cr = await client.GetAsync<V1Alpha1ShardManagedDatabase>(opts.CrName, opts.Namespace, ct);
            if (cr?.Status.ShardMapping.Count >= opts.Shards.Count && cr.Status.Phase == "Idle")
            {
                LogSuccess($"Initial cluster initialized. Mapping: {JsonSerializer.Serialize(cr.Status.ShardMapping)}");
                return cr;
            }

            await Task.Delay(2000, ct);
        }

        throw new TimeoutException("Timed out waiting for initial ShardManagedDatabase initialization.");
    }

    private async Task WaitForPodReadyAsync(string instanceName, string ns, CancellationToken ct)
    {
        var podName = $"{instanceName}-1";
        LogInfo($"Waiting for CNPG primary pod '{podName}' to be ready...");

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var check = await pg.ExecuteSqlAsync(instanceName, ns, "postgres", "SELECT 1;", ct);
                if (check.Trim() == "1")
                {
                    LogSuccess($"Pod '{podName}' is responding to queries.");
                    return;
                }
            }
            catch
            {
                // Pod not ready yet
            }

            await Task.Delay(3000, ct);
        }

        throw new TimeoutException($"Pod '{podName}' failed to reach ready state within 120s.");
    }

    private async Task<Dictionary<string, ShardBaseline>> SeedBaselineDataAsync(
        string instanceName,
        E2EVerificationOptions opts,
        CancellationToken ct)
    {
        LogSection("Step 2: Seeding Baseline Test Data and Checksums");
        var baselines = new Dictionary<string, ShardBaseline>();

        const string createTableSql = $"""
            CREATE TABLE IF NOT EXISTS {TableName} (
                id SERIAL PRIMARY KEY,
                customer_name VARCHAR(100) NOT NULL,
                amount NUMERIC(10,2) NOT NULL,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            """;

        const string insertSql = $"""
            INSERT INTO {TableName} (customer_name, amount)
            SELECT 'customer_' || g, (g * 10.50)::NUMERIC(10,2)
            FROM generate_series(1, 1000) g;
            """;

        foreach (var shard in opts.Shards)
        {
            LogInfo($"Initializing shard '{shard}' on instance '{instanceName}'...");

            await pg.ExecuteSqlAsync(instanceName, opts.Namespace, shard, createTableSql, ct);
            await pg.ExecuteSqlAsync(instanceName, opts.Namespace, shard, insertSql, ct);

            var baseline = await ComputeShardBaselineAsync(instanceName, opts.Namespace, shard, ct);
            baselines[shard] = baseline;

            LogSuccess($"Shard '{shard}' baseline: {baseline.RowCount} rows, md5={baseline.Checksum}, maxId={baseline.MaxId}");
        }

        return baselines;
    }

    private async Task OverloadShardToTriggerSplitAsync(
        string instanceName,
        string shard,
        E2EVerificationOptions opts,
        Dictionary<string, ShardBaseline> baselines,
        CancellationToken ct)
    {
        LogSection($"Step 3: Generating Heavy Workload on '{shard}' to Trip maxShardSize ({opts.MaxShardSize})");

        LogInfo($"Inserting 100,000 heavy rows with padding into '{shard}'...");

        // Generate ~35-40 MiB of data with padding to exceed 30Mi threshold
        const string heavyInsertSql = $"""
            INSERT INTO {TableName} (customer_name, amount)
            SELECT 'heavy_workload_' || g || '_' || repeat('A', 250), (g * 1.25)::NUMERIC(10,2)
            FROM generate_series(1, 100000) g;
            """;

        await pg.ExecuteSqlAsync(instanceName, opts.Namespace, shard, heavyInsertSql, ct);

        // Update baseline for overloaded shard
        var updatedBaseline = await ComputeShardBaselineAsync(instanceName, opts.Namespace, shard, ct);
        baselines[shard] = updatedBaseline;

        LogSuccess($"Workload generated! Updated '{shard}': {updatedBaseline.RowCount} rows, md5={updatedBaseline.Checksum}, maxId={updatedBaseline.MaxId}");
        LogInfo("Waiting for operator reconciliation tick to detect disk usage and initiate split...");
    }

    private async Task<bool> MonitorMigrationPhasesAsync(
        E2EVerificationOptions opts,
        CancellationToken ct)
    {
        LogSection("Step 4: Monitoring Operator Phases and ConfigMap Transitions");

        var deadline = DateTime.UtcNow + opts.Timeout;
        var sawMigrating = false;
        var sawDrainingMaintenance = false;
        var sawCleaning = false;
        var lastPhase = "";
        var lastStep = "";

        while (DateTime.UtcNow < deadline)
        {
            var cr = await client.GetAsync<V1Alpha1ShardManagedDatabase>(opts.CrName, opts.Namespace, ct);
            if (cr is null)
            {
                await Task.Delay(2000, ct);
                continue;
            }

            var currentPhase = cr.Status.Phase;
            var currentStep = cr.Status.ActiveMigration?.Step.ToString() ?? "None";

            if (currentPhase != lastPhase || currentStep != lastStep)
            {
                LogInfo($"Operator Phase: [{currentPhase}] | Migration Step: [{currentStep}]");
                lastPhase = currentPhase;
                lastStep = currentStep;
            }

            if (currentPhase == "Migrating")
            {
                sawMigrating = true;

                if (currentStep == "Draining")
                {
                    // Verify ConfigMap Maintenance mode in real-time
                    var cmName = $"app-shard-topology-{opts.CrName}";
                    var cm = await client.GetAsync<V1ConfigMap>(cmName, opts.Namespace, ct);
                    if (cm?.Data is not null)
                    {
                        var inFlight = cr.Status.ActiveMigration?.ShardsInFlight ?? [];
                        var allMaintenance = inFlight.Count > 0 &&
                                             inFlight.All(s => cm.Data.TryGetValue($"{s}.status", out var st) && st == "Maintenance");

                        if (allMaintenance && !sawDrainingMaintenance)
                        {
                            sawDrainingMaintenance = true;
                            LogSuccess($"[ConfigMap Check] Migrating shards [{string.Join(", ", inFlight)}] verified in 'Maintenance' mode during Draining!");
                        }
                    }
                }
            }
            else if (currentPhase == "Cleaning")
            {
                sawCleaning = true;
            }
            else if (currentPhase == "Idle" && sawMigrating)
            {
                LogSuccess($"Operator returned to 'Idle' phase! Migration lifecycle fully completed (cleaning observed: {sawCleaning}).");
                return true;
            }

            await Task.Delay(2500, ct);
        }

        return false;
    }

    private async Task<bool> VerifyDataIntegrityAsync(
        E2EVerificationOptions opts,
        Dictionary<string, ShardBaseline> baselines,
        CancellationToken ct)
    {
        LogSection("Step 5: Verifying Data Integrity, Checksums and Zero Data Loss");

        var cr = await client.GetAsync<V1Alpha1ShardManagedDatabase>(opts.CrName, opts.Namespace, ct);
        if (cr is null)
        {
            LogError("Could not fetch ShardManagedDatabase CR for verification.");
            return false;
        }

        var instances = cr.Status.ShardMapping.Values.Distinct().ToList();
        LogInfo($"Clusters in topology after split: [{string.Join(", ", instances)}]");

        if (instances.Count < 2)
        {
            LogError($"Expected at least 2 instances after split, but found {instances.Count}.");
            return false;
        }

        var sourceInstance = instances[0];
        var targetInstance = instances[1];

        // 1. Verify ConfigMap topology entries
        var cmName = $"app-shard-topology-{opts.CrName}";
        var cm = await client.GetAsync<V1ConfigMap>(cmName, opts.Namespace, ct);
        if (cm?.Data is null)
        {
            LogError($"ConfigMap '{cmName}' not found or empty.");
            return false;
        }

        foreach (var shard in opts.Shards)
        {
            if (!cm.Data.TryGetValue($"{shard}.status", out var status) || status != "Active")
            {
                LogError($"ConfigMap status for shard '{shard}' is not 'Active' (got '{status}').");
                return false;
            }
        }
        LogSuccess("All shards confirmed with status='Active' in topology ConfigMap.");

        // 2. Wait for target primary pod ready
        await WaitForPodReadyAsync(targetInstance, opts.Namespace, ct);

        // 3. Verify each shard's checksum and row count on its current instance
        foreach (var (shard, currentInstance) in cr.Status.ShardMapping)
        {
            var expected = baselines[shard];
            var actual = await ComputeShardBaselineAsync(currentInstance, opts.Namespace, shard, ct);

            LogInfo($"Checking shard '{shard}' on instance '{currentInstance}'...");

            if (actual.RowCount != expected.RowCount)
            {
                LogError($"Row count mismatch on '{shard}': expected {expected.RowCount}, got {actual.RowCount}!");
                return false;
            }

            if (actual.Checksum != expected.Checksum)
            {
                LogError($"MD5 checksum mismatch on '{shard}': expected {expected.Checksum}, got {actual.Checksum}!");
                return false;
            }

            LogSuccess($"Shard '{shard}' integrity verified: {actual.RowCount:N0} rows match, md5={actual.Checksum} [MATCH]");

            // 4. Sequence sync check: ensure nextval works and doesn't collide
            const string seqCheckSql = $"""
                INSERT INTO {TableName} (customer_name, amount)
                VALUES ('post_split_seq_check', 999.99)
                RETURNING id;
                """;

            var newIdStr = await pg.ExecuteSqlAsync(currentInstance, opts.Namespace, shard, seqCheckSql, ct);
            if (long.TryParse(newIdStr.Trim(), out var newId))
            {
                if (newId <= expected.MaxId)
                {
                    LogError($"Sequence desync on '{shard}': new id {newId} <= previous max {expected.MaxId}!");
                    return false;
                }
                LogSuccess($"Shard '{shard}' sequence verified: newId={newId} > previousMax={expected.MaxId} [OK]");
            }
        }

        // 5. Verify source instance dropped migrated shards
        var movedShards = cr.Status.ShardMapping
            .Where(kv => kv.Value == targetInstance)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var shard in movedShards)
        {
            var checkSql = $"SELECT count(*) FROM pg_database WHERE datname = '{shard}';";
            var result = await pg.ExecuteSqlAsync(sourceInstance, opts.Namespace, "postgres", checkSql, ct);
            if (result.Trim() != "0")
            {
                LogError($"Migrated database '{shard}' still exists on source instance '{sourceInstance}'!");
                return false;
            }
            LogSuccess($"Source instance '{sourceInstance}' verified: migrated database '{shard}' was dropped [OK]");
        }

        return true;
    }

    private async Task<ShardBaseline> ComputeShardBaselineAsync(
        string instanceName,
        string ns,
        string database,
        CancellationToken ct)
    {
        const string countSql = $"SELECT count(*) FROM {TableName};";
        var countStr = await pg.ExecuteSqlAsync(instanceName, ns, database, countSql, ct);
        var rowCount = long.Parse(countStr.Trim());

        const string md5Sql = $"""
            SELECT md5(COALESCE(string_agg(id || ':' || customer_name || ':' || amount, ',' ORDER BY id), 'empty'))
            FROM {TableName};
            """;
        var md5 = (await pg.ExecuteSqlAsync(instanceName, ns, database, md5Sql, ct)).Trim();

        const string maxIdSql = $"SELECT COALESCE(max(id), 0) FROM {TableName};";
        var maxIdStr = await pg.ExecuteSqlAsync(instanceName, ns, database, maxIdSql, ct);
        var maxId = long.Parse(maxIdStr.Trim());

        return new ShardBaseline(rowCount, md5, maxId);
    }

    private static void PrintFinalReport(
        TimeSpan duration,
        E2EVerificationOptions opts,
        Dictionary<string, ShardBaseline> baselines)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("============================================================");
        Console.WriteLine("          ALL CHECKS PASSED: ZERO DATA LOSS CONFIRMED!      ");
        Console.WriteLine("============================================================");
        Console.ResetColor();
        Console.WriteLine($"Total Duration     : {duration.TotalSeconds:F1}s");
        Console.WriteLine($"Verified Shards    : {baselines.Count}");
        foreach (var (shard, baseline) in baselines)
        {
            Console.WriteLine($"  * {shard,-14} : {baseline.RowCount,9:N0} rows verified | md5={baseline.Checksum}");
        }
        Console.WriteLine("Data Integrity     : 100% (MD5 checksum byte-for-byte exact)");
        Console.WriteLine("Sequences          : Synchronized without ID collisions");
        Console.WriteLine("Topology ConfigMap : Switched and Active");
        Console.WriteLine("Source Reclamation : Dropped cleanly on source");
        Console.WriteLine("============================================================");
        Console.WriteLine();
    }

    private static void LogSection(string title)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"--- {title} ---");
        Console.ResetColor();
    }

    private static void LogInfo(string msg)
    {
        Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] [INFO] {msg}");
    }

    private static void LogSuccess(string msg)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] [PASS] {msg}");
        Console.ResetColor();
    }

    private static void LogError(string msg)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] [FAIL] {msg}");
        Console.ResetColor();
    }
}
