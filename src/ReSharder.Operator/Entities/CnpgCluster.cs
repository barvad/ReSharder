using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

using k8s.Models;

using KubeOps.Abstractions.Entities;

namespace ReSharder.Operator.Entities;

/// <summary>
/// Minimal typed representation of a CloudNativePG <c>Cluster</c> custom resource.
/// Only the fields managed by ReSharder are modelled; everything else passes
/// through as unstructured JSON via the <c>additionalProperties</c> on the CRD.
/// </summary>
[KubernetesEntity(
    Group = "postgresql.cnpg.io",
    ApiVersion = "v1",
    Kind = "Cluster",
    PluralName = "clusters")]
public class CnpgCluster : CustomKubernetesEntity<CnpgClusterSpec, CnpgClusterStatus>
{
    // Inherit Spec/Status/Metadata from CustomKubernetesEntity to make the
    // type a valid KubeOps entity. Fields are modelled in separate classes.
}

public class CnpgClusterSpec
{
    [JsonPropertyName("instances")]
    public int Instances { get; set; } = 1;

    [JsonPropertyName("storage")]
    public CnpgStorageSpec Storage { get; set; } = new();

    [JsonPropertyName("bootstrap")]
    public CnpgBootstrapSpec? Bootstrap { get; set; }

    [JsonPropertyName("enableSuperuserAccess")]
    public bool EnableSuperuserAccess { get; set; }
}

public class CnpgStorageSpec
{
    [JsonPropertyName("size")]
    public string Size { get; set; } = "10Gi";

    [JsonPropertyName("storageClass")]
    public string? StorageClass { get; set; }

    [JsonPropertyName("resizeInUseVolumes")]
    public bool ResizeInUseVolumes { get; set; } = true;
}

public class CnpgBootstrapSpec
{
    [JsonPropertyName("initdb")]
    public CnpgInitDbSpec? InitDb { get; set; }
}

public class CnpgInitDbSpec
{
    [JsonPropertyName("database")]
    public string Database { get; set; } = "app";

    [JsonPropertyName("owner")]
    public string Owner { get; set; } = "app";
}

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicConstructors)]
public class CnpgClusterStatus
{
    [JsonPropertyName("phase")]
    public string? Phase { get; set; }

    [JsonPropertyName("instances")]
    public int? Instances { get; set; }

    [JsonPropertyName("readyInstances")]
    public int? ReadyInstances { get; set; }
}