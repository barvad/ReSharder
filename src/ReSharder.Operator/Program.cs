using KubeOps.Abstractions.Crds;
using KubeOps.Operator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReSharder.Operator.Services;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging
    .SetMinimumLevel(LogLevel.Information)
    .AddSimpleConsole(opts =>
    {
        opts.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
        opts.SingleLine = true;
    });

builder.Services.AddSingleton<CnpgClusterManager>();
builder.Services.AddSingleton<PvcMonitor>();

builder.Services
    .AddKubernetesOperator()
#if DEBUG
    .AddCrdInstaller(c => c
        .WithOverwriteExisting(true)
        .WithDeleteOnShutdown(true))
#endif
    .RegisterComponents();

using var host = builder.Build();
await host.RunAsync();
