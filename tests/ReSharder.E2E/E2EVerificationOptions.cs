namespace ReSharder.E2E;

/// <summary>
/// Configuration options for the E2E integration test run.
/// </summary>
public sealed class E2EVerificationOptions
{
    public string Namespace { get; set; } = "default";
    public string CrName { get; set; } = "e2e-sharded-db";
    public string MaxShardSize { get; set; } = "30Mi";
    public string InitialStorageSize { get; set; } = "1Gi";
    public List<string> Shards { get; set; } = ["s1", "s2", "s3", "s4"];
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(300);
    public bool CleanupOnSuccess { get; set; } = false;

    public static E2EVerificationOptions FromArgs(string[] args)
    {
        var opts = new E2EVerificationOptions();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.Equals("--namespace", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                opts.Namespace = args[++i];
            }
            else if (arg.Equals("--cr-name", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                opts.CrName = args[++i];
            }
            else if (arg.Equals("--max-shard-size", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                opts.MaxShardSize = args[++i];
            }
            else if (arg.Equals("--timeout-seconds", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && int.TryParse(args[++i], out var sec))
            {
                opts.Timeout = TimeSpan.FromSeconds(sec);
            }
            else if (arg.Equals("--cleanup", StringComparison.OrdinalIgnoreCase))
            {
                opts.CleanupOnSuccess = true;
            }
        }

        return opts;
    }
}
