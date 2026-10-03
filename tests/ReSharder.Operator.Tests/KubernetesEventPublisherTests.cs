using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using ReSharder.Operator.Services;

namespace ReSharder.Operator.Tests;

public class KubernetesEventPublisherTests
{
    [Fact]
    public void EventConstants_AreStandardKubernetesFormat()
    {
        Assert.Equal("Normal", KubernetesEventPublisher.TypeNormal);
        Assert.Equal("Warning", KubernetesEventPublisher.TypeWarning);

        Assert.Equal("ShardSplitStarted", KubernetesEventPublisher.ReasonSplitStarted);
        Assert.Equal("StorageScaledUp", KubernetesEventPublisher.ReasonStorageScaledUp);
        Assert.Equal("DrainStarted", KubernetesEventPublisher.ReasonDrainStarted);
        Assert.Equal("CutoverCompleted", KubernetesEventPublisher.ReasonCutoverCompleted);
        Assert.Equal("CleaningCompleted", KubernetesEventPublisher.ReasonCleaningCompleted);
        Assert.Equal("MigrationRolledBack", KubernetesEventPublisher.ReasonMigrationRolledBack);
    }

    [Fact]
    public async Task HealthService_RespondsToLivenessAndReadinessProbes()
    {
        // Pick an available high port for testing.
        const int port = 18080;
        using var service = new OperatorHealthService(NullLogger<OperatorHealthService>.Instance, port);

        using var cts = new CancellationTokenSource();
        var runTask = service.StartAsync(cts.Token);

        // Give listener a moment to bind.
        await Task.Delay(100);

        using var http = new HttpClient();

        try
        {
            // 1. Liveness probe test:
            var healthResp = await http.GetAsync($"http://localhost:{port}/healthz");
            Assert.Equal(HttpStatusCode.OK, healthResp.StatusCode);
            var healthBody = await healthResp.Content.ReadAsStringAsync();
            Assert.Contains("\"ok\"", healthBody);

            // 2. Readiness probe test:
            var readyResp = await http.GetAsync($"http://localhost:{port}/readyz");
            Assert.Equal(HttpStatusCode.OK, readyResp.StatusCode);
            var readyBody = await readyResp.Content.ReadAsStringAsync();
            Assert.Contains("\"ok\"", readyBody);

            // 3. Set not ready:
            service.SetReady(false);
            var notReadyResp = await http.GetAsync($"http://localhost:{port}/readyz");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, notReadyResp.StatusCode);

            // 4. Non-existing path:
            var notFoundResp = await http.GetAsync($"http://localhost:{port}/unknown");
            Assert.Equal(HttpStatusCode.NotFound, notFoundResp.StatusCode);
        }
        finally
        {
            await cts.CancelAsync();
            await service.StopAsync(CancellationToken.None);
        }
    }
}
