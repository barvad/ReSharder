using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using KubeOps.Operator;
using ReSharder.Operator.Services;
using ReSharder.E2E;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(opts =>
{
    opts.TimestampFormat = "HH:mm:ss ";
    opts.SingleLine = true;
});

builder.Services.AddKubernetesOperator();
builder.Services.AddSingleton<PostgresExecutor>();
builder.Services.AddSingleton<E2ETestRunner>();

using var host = builder.Build();
var runner = host.Services.GetRequiredService<E2ETestRunner>();

var options = E2EVerificationOptions.FromArgs(args);
var success = await runner.RunAsync(options, CancellationToken.None);

return success ? 0 : 1;
