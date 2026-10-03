using k8s.Models;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging;
using ReSharder.Operator.Entities;

namespace ReSharder.Operator.Services;

/// <summary>
/// Emits standard Kubernetes Events on ShardManagedDatabase resources
/// so operators and DBAs can monitor shard lifecycle via 'kubectl describe'.
/// </summary>
public sealed class KubernetesEventPublisher(
    IKubernetesClient client,
    ILogger<KubernetesEventPublisher> logger)
{
    public const string TypeNormal = "Normal";
    public const string TypeWarning = "Warning";

    // Standard event reasons
    public const string ReasonSplitStarted = "ShardSplitStarted";
    public const string ReasonStorageScaledUp = "StorageScaledUp";
    public const string ReasonDrainStarted = "DrainStarted";
    public const string ReasonCutoverCompleted = "CutoverCompleted";
    public const string ReasonCleaningCompleted = "CleaningCompleted";
    public const string ReasonMigrationRolledBack = "MigrationRolledBack";

    public async Task PublishEventAsync(
        V1Alpha1ShardManagedDatabase entity,
        string reason,
        string message,
        string type = TypeNormal,
        CancellationToken ct = default)
    {
        try
        {
            var ns = entity.Namespace();
            var evt = new Corev1Event
            {
                Metadata = new V1ObjectMeta
                {
                    GenerateName = $"{entity.Name()}-evt-",
                    NamespaceProperty = ns,
                },
                InvolvedObject = new V1ObjectReference
                {
                    Kind = entity.Kind,
                    ApiVersion = entity.ApiVersion,
                    Name = entity.Name(),
                    NamespaceProperty = ns,
                    Uid = entity.Uid(),
                },
                Reason = reason,
                Message = message,
                Type = type,
                FirstTimestamp = DateTime.UtcNow,
                LastTimestamp = DateTime.UtcNow,
                Count = 1,
                Source = new V1EventSource
                {
                    Component = "resharder-operator",
                },
            };

            await client.CreateAsync(evt, ct);
            logger.LogDebug("Emitted event {Reason} for {Entity}: {Message}", reason, entity.Name(), message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not emit Kubernetes event {Reason} for {Entity}.", reason, entity.Name());
        }
    }
}
