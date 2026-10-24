using System;
using System.Text.Json;
using ReSharder.Operator.Entities;

Console.WriteLine("Serialization smoke test starting...");

bool Fail(string msg)
{
    Console.Error.WriteLine(msg);
    return false;
}

try
{
    var activeJson = JsonSerializer.Serialize(new ActiveMigrationState
    {
        Step = MigrationStep.Replicating,
        SourceInstance = "src",
        TargetInstance = "tgt",
        ShardsInFlight = new List<string>{ "shard1" },
        StartedAt = DateTime.UtcNow,
        RetryCount = 0,
    });

    var desAct = JsonSerializer.Deserialize<ActiveMigrationState>(activeJson);
    if (desAct is null) return Fail("ActiveMigrationState deserialized to null");

    var clean = new CleaningState{ Instance = "inst", StartedAt = DateTime.UtcNow };
    var cleanJson = JsonSerializer.Serialize(clean);
    var desClean = JsonSerializer.Deserialize<CleaningState>(cleanJson);
    if (desClean is null) return Fail("CleaningState deserialized to null");

    var cr = new V1Alpha1ShardManagedDatabase
    {
        Spec = new V1Alpha1ShardManagedDatabase.V1Alpha1Spec
        {
            MaxShardSize = "10Gi",
            InitialStorageSize = "10Gi",
            Shards = new List<string>{ "s1" }
        }
    };
    var crJson = JsonSerializer.Serialize(cr);
    var desCr = JsonSerializer.Deserialize<V1Alpha1ShardManagedDatabase>(crJson);
    if (desCr is null) return Fail("V1Alpha1ShardManagedDatabase deserialized to null");

    Console.WriteLine("All deserializations OK");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Exception: {ex}");
    return 2;
}
