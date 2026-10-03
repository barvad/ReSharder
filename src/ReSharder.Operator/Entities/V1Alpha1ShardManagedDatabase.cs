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
    /// <summary>
    /// Desired state for a ShardManagedDatabase resource.
    /// </summary>
    public class V1Alpha1Spec
    {
        /// <summary>
        /// Disk-usage threshold per CNPG instance that triggers a shard split.
        /// Uses Kubernetes resource quantity notation (e.g. "50Gi").
        /// </summary>
        [JsonPropertyName("maxShardSize")]
        public string MaxShardSize { get; set; } = string.Empty;

        /// <summary>
        /// Initial PVC size allocated for every new CNPG cluster instance
        /// created by the operator.
        /// </summary>
        [JsonPropertyName("initialStorageSize")]
        public string InitialStorageSize { get; set; } = string.Empty;

        /// <summary>
        /// Full list of logical database (shard) names to be managed.
        /// Each name becomes a physical PostgreSQL database inside one of
        /// the CNPG instances.
        /// </summary>
        [JsonPropertyName("shards")]
        public List<string> Shards { get; set; } = [];

        /// <summary>
        /// Names of Kubernetes Deployments that consume shard connections.
        /// The operator will manage ConfigMap-based topology updates so
        /// these workloads can react to shard migrations without restarts.
        /// </summary>
        [JsonPropertyName("dependentDeployments")]
        public List<string> DependentDeployments { get; set; } = [];
    }

    /// <summary>
    /// Observed state managed by the operator.
    /// </summary>
    public class V1Alpha1Status
    {
        /// <summary>
        /// Current lifecycle phase of the operator for this resource.
        /// </summary>
        [JsonPropertyName("phase")]
        public string Phase { get; set; } = PhaseIdle;

        /// <summary>
        /// Point-in-time mapping of each shard name to the CNPG cluster
        /// instance that physically hosts it.
        /// Key = shard (database) name, Value = CNPG Cluster CR name.
        /// </summary>
        [JsonPropertyName("shardMapping")]
        public Dictionary<string, string> ShardMapping { get; set; } = new();

        /// <summary>
        /// The metadata.generation observed by the operator during the
        /// last successful reconciliation.
        /// </summary>
        [JsonPropertyName("observedGeneration")]
        public long? ObservedGeneration { get; set; }

        public const string PhaseIdle = "Idle";
        public const string PhaseMigrating = "Migrating";
        public const string PhaseCleaning = "Cleaning";
    }
}
