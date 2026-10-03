using System.Text.Json.Serialization;
using k8s.Models;
using KubeOps.Abstractions.Entities;

namespace ReSharder.Operator.Entities;

/// <summary>
/// Declares a set of logical PostgreSQL databases (shards) to be dynamically
/// distributed across CloudNativePG clusters by the ReSharder operator.
/// </summary>
[KubernetesEntity(
    Group = "resharder.io",
    ApiVersion = "v1alpha1",
    Kind = "ShardManagedDatabase",
    PluralName = "shardmanageddatabases")]
public partial class V1Alpha1ShardManagedDatabase
    : CustomKubernetesEntity<V1Alpha1ShardManagedDatabase.V1Alpha1Spec,
                             V1Alpha1ShardManagedDatabase.V1Alpha1Status>
{
    public class V1Alpha1Spec
    {
        [JsonPropertyName("maxShardSize")]
        public string MaxShardSize { get; set; } = string.Empty;

        [JsonPropertyName("initialStorageSize")]
        public string InitialStorageSize { get; set; } = string.Empty;

        [JsonPropertyName("shards")]
        public List<string> Shards { get; set; } = [];

        [JsonPropertyName("dependentDeployments")]
        public List<string> DependentDeployments { get; set; } = [];

        /// <summary>
        /// Replication lag threshold in bytes. When all migrating shards reach
        /// lag below this value, the operator switches them to Maintenance
        /// (stops writes) and waits for lag to reach exactly 0.
        /// Default: 1048576 (1 MiB).
        /// </summary>
        [JsonPropertyName("lagThresholdBytes")]
        public long LagThresholdBytes { get; set; } = 1_048_576;

        /// <summary>
        /// How many seconds to wait after setting shards to Maintenance before
        /// checking for final zero-lag. Gives in-flight writes time to flush.
        /// Default: 5 seconds.
        /// </summary>
        [JsonPropertyName("drainWaitSeconds")]
        public int DrainWaitSeconds { get; set; } = 5;

        /// <summary>
        /// Maximum seconds to wait in Cleaning phase for disk space to drop below maxShardSize
        /// after dropping source databases.
        /// Default: 120 seconds.
        /// </summary>
        [JsonPropertyName("cleaningTimeoutSeconds")]
        public int CleaningTimeoutSeconds { get; set; } = 120;

        /// <summary>
        /// Maximum seconds to wait in Draining step before triggering rollback
        /// if lag does not drop to zero. Prevents writes staying blocked indefinitely.
        /// Default: 60 seconds.
        /// </summary>
        [JsonPropertyName("drainTimeoutSeconds")]
        public int DrainTimeoutSeconds { get; set; } = 60;

        /// <summary>
        /// Maximum total seconds allowed for a shard migration from start to finish.
        /// Default: 600 seconds (10 minutes).
        /// </summary>
        [JsonPropertyName("migrationTimeoutSeconds")]
        public int MigrationTimeoutSeconds { get; set; } = 600;

        /// <summary>
        /// Maximum consecutive retries for migration steps before triggering rollback.
        /// Default: 3.
        /// </summary>
        [JsonPropertyName("maxMigrationRetries")]
        public int MaxMigrationRetries { get; set; } = 3;
    }

    public class V1Alpha1Status
    {
        [JsonPropertyName("phase")]
        public string Phase { get; set; } = PhaseIdle;

        [JsonPropertyName("shardMapping")]
        public Dictionary<string, string> ShardMapping { get; set; } = new();

        [JsonPropertyName("observedGeneration")]
        public long? ObservedGeneration { get; set; }

        [JsonPropertyName("activeMigration")]
        public ActiveMigrationState? ActiveMigration { get; set; }

        [JsonPropertyName("cleaning")]
        public CleaningState? Cleaning { get; set; }

        public const string PhaseIdle = "Idle";
        public const string PhaseMigrating = "Migrating";
        public const string PhaseCleaning = "Cleaning";
    }
}

/// <summary>
/// Persisted state for an active cleaning phase after shard migration,
/// stored in the CR status to survive operator restarts.
/// </summary>
public class CleaningState
{
    [JsonPropertyName("instance")]
    public string Instance { get; set; } = string.Empty;

    [JsonPropertyName("startedAt")]
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Persisted state for an active shard migration, stored in the CR status.
/// Allows the operator to resume migration after restarts.
/// </summary>
public class ActiveMigrationState
{
    [JsonPropertyName("step")]
    public MigrationStep Step { get; set; } = MigrationStep.NotStarted;

    [JsonPropertyName("sourceInstance")]
    public string SourceInstance { get; set; } = string.Empty;

    [JsonPropertyName("targetInstance")]
    public string TargetInstance { get; set; } = string.Empty;

    [JsonPropertyName("shardsInFlight")]
    public List<string> ShardsInFlight { get; set; } = [];

    /// <summary>
    /// UTC timestamp when Maintenance was applied on the ConfigMap.
    /// Used to enforce the drain wait before the final zero-lag check.
    /// </summary>
    [JsonPropertyName("maintenanceSetAt")]
    public DateTime? MaintenanceSetAt { get; set; }

    /// <summary>
    /// UTC timestamp when the migration was initiated.
    /// </summary>
    [JsonPropertyName("startedAt")]
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Consecutive failure / retry count for current step.
    /// </summary>
    [JsonPropertyName("retryCount")]
    public int RetryCount { get; set; }

    /// <summary>
    /// Last error message encountered during migration, if any.
    /// </summary>
    [JsonPropertyName("lastError")]
    public string? LastError { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MigrationStep
{
    NotStarted,
    Replicating,
    Draining,
    CaughtUp,
    CutoverDone,
}
