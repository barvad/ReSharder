using System.Text;
using k8s;
using k8s.Models;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging;

namespace ReSharder.Operator.Services;

/// <summary>
/// Executes SQL and shell commands inside CNPG pods via the Kubernetes exec API.
/// Used for logical replication setup, schema migration, sequence copying,
/// and replication lag monitoring.
/// </summary>
public sealed class PostgresExecutor(
    IKubernetesClient client,
    ILogger<PostgresExecutor> logger)
{
    /// <summary>
    /// Executes a SQL statement inside a CNPG primary pod via <c>psql</c>.
    /// Returns stdout as a string.
    /// </summary>
    public async Task<string> ExecuteSqlAsync(
        string instanceName,
        string ns,
        string database,
        string sql,
        CancellationToken ct)
    {
        var podName = $"{instanceName}-1";
        var command = new[] { "psql", "-h", "localhost", "-U", "postgres", "-d", database, "-t", "-A", "-c", sql };

        logger.LogDebug(
            "Executing SQL on {Pod}/{Db}: {Sql}",
            podName, database, sql.Length > 200 ? sql[..200] + "..." : sql);

        return await ExecInPodAsync(podName, ns, "postgres", command, null, ct);
    }

    /// <summary>
    /// Executes a SQL statement that must run outside a transaction block
    /// (e.g. CREATE SUBSCRIPTION, DROP SUBSCRIPTION).
    /// </summary>
    public async Task<string> ExecuteSqlNoTransactionAsync(
        string instanceName,
        string ns,
        string database,
        string sql,
        CancellationToken ct)
    {
        return await ExecuteSqlAsync(instanceName, ns, database, sql, ct);
    }

    /// <summary>
    /// Dumps the schema of a database from source and applies it to target.
    /// </summary>
    public async Task<bool> MigrateSchemaAsync(
        string sourceInstance,
        string targetInstance,
        string ns,
        string database,
        CancellationToken ct)
    {
        var sourcePod = $"{sourceInstance}-1";
        var targetPod = $"{targetInstance}-1";

        logger.LogInformation(
            "Migrating schema for database {Db} from {Source} to {Target}.",
            database, sourceInstance, targetInstance);

        // Dump schema from source.
        var dumpCommand = new[]
        {
            "pg_dump", "--schema-only", "--no-owner", "--no-privileges",
            "-h", "localhost", "-U", "postgres", "-d", database,
        };
        var schemaSql = await ExecInPodAsync(sourcePod, ns, "postgres", dumpCommand, null, ct);

        if (string.IsNullOrWhiteSpace(schemaSql))
        {
            logger.LogWarning("Schema dump for {Db} on {Source} returned empty output.", database, sourceInstance);
            return false;
        }

        // Apply schema to target via stdin.
        var applyCommand = new[] { "psql", "-h", "localhost", "-U", "postgres", "-d", database };
        await ExecInPodAsync(targetPod, ns, "postgres", applyCommand, schemaSql, ct);

        logger.LogInformation("Schema migration complete for database {Db}.", database);
        return true;
    }

    /// <summary>
    /// Copies sequence values from source to target after replication catch-up.
    /// Sequences are NOT replicated by logical replication.
    /// </summary>
    public async Task CopySequencesAsync(
        string sourceInstance,
        string targetInstance,
        string ns,
        string database,
        CancellationToken ct)
    {
        logger.LogInformation(
            "Copying sequence values for {Db} from {Source} to {Target}.",
            database, sourceInstance, targetInstance);

        var genSql = """
            SELECT
                'SELECT setval(' ||
                quote_literal(quote_ident(schemaname) || '.' || quote_ident(sequencename)) ||
                ', ' || last_value || ', true);'
            FROM pg_sequences
            WHERE last_value IS NOT NULL;
            """;

        var setvalCommands = await ExecuteSqlAsync(sourceInstance, ns, database, genSql, ct);

        if (string.IsNullOrWhiteSpace(setvalCommands))
        {
            logger.LogDebug("No sequences to copy for {Db}.", database);
            return;
        }

        await ExecuteSqlAsync(targetInstance, ns, database, setvalCommands, ct);
        logger.LogInformation("Sequence copy complete for {Db}.", database);
    }

    /// <summary>
    /// Retrieves the superuser password from the CNPG cluster's superuser secret.
    /// CNPG names it <c>{cluster-name}-superuser</c>.
    /// </summary>
    public async Task<string> GetSuperuserPasswordAsync(
        string instanceName,
        string ns,
        CancellationToken ct)
    {
        var secretName = $"{instanceName}-superuser";
        var secret = await client.GetAsync<V1Secret>(secretName, ns, ct)
            ?? throw new InvalidOperationException(
                $"Superuser secret {secretName} not found in {ns}.");

        if (secret.Data is null || !secret.Data.TryGetValue("password", out var passwordBytes))
            throw new InvalidOperationException(
                $"Secret {secretName} does not contain 'password' key.");

        return Encoding.UTF8.GetString(passwordBytes);
    }

    /// <summary>
    /// Executes a command inside a pod container. If <paramref name="stdin"/> is provided,
    /// it is piped to the process stdin.
    /// </summary>
    private async Task<string> ExecInPodAsync(
        string podName,
        string ns,
        string container,
        string[] command,
        string? stdin,
        CancellationToken ct)
    {
        var apiClient = client.ApiClient;
        var needStdin = !string.IsNullOrEmpty(stdin);

        var ws = await apiClient.WebSocketNamespacedPodExecAsync(
            name: podName,
            @namespace: ns,
            command: command,
            container: container,
            stderr: true,
            stdin: needStdin,
            stdout: true,
            tty: false,
            webSocketSubProtocol: WebSocketProtocol.V4BinaryWebsocketProtocol,
            customHeaders: null,
            cancellationToken: ct);

        using var demux = new StreamDemuxer(ws);
        demux.Start();

        // Write stdin if provided.
        if (needStdin)
        {
            await using var stdinStream = demux.GetStream(ChannelIndex.StdIn, null);
            var inputBytes = Encoding.UTF8.GetBytes(stdin!);
            await stdinStream.WriteAsync(inputBytes, ct);
        }

        // Read stdout and stderr.
        var stdout = new MemoryStream();
        var stderr = new MemoryStream();

        await using (var stdoutStream = demux.GetStream(null, ChannelIndex.StdOut))
        await using (var stderrStream = demux.GetStream(null, ChannelIndex.StdErr))
        {
            await Task.WhenAll(
                stdoutStream.CopyToAsync(stdout, ct),
                stderrStream.CopyToAsync(stderr, ct));
        }

        var stdoutStr = Encoding.UTF8.GetString(stdout.ToArray()).Trim();
        var stderrStr = Encoding.UTF8.GetString(stderr.ToArray()).Trim();

        if (!string.IsNullOrEmpty(stderrStr))
        {
            logger.LogWarning("Exec stderr on {Pod}: {Stderr}", podName, stderrStr);
        }

        return stdoutStr;
    }
}
