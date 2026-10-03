using System.Net;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ReSharder.Operator.Services;

/// <summary>
/// Lightweight HTTP server providing /healthz (liveness) and /readyz (readiness)
/// probes for Kubernetes pod lifecycle management.
/// </summary>
public sealed class OperatorHealthService : BackgroundService
{
    private HttpListener _listener = new();
    private readonly ILogger<OperatorHealthService> _logger;
    private readonly int _port;
    private volatile bool _isReady = true;

    public OperatorHealthService(ILogger<OperatorHealthService> logger, int port = 8080)
    {
        _logger = logger;
        _port = port;
        _listener.Prefixes.Add($"http://*:{_port}/");
    }

    public void SetReady(bool ready) => _isReady = ready;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _listener.Start();
            _logger.LogInformation("Operator health probes listening on port {Port}.", _port);
        }
        catch (HttpListenerException)
        {
            // On non-admin environments (e.g. Windows unit tests), fallback to localhost.
            try
            {
                _listener.Close();
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://localhost:{_port}/");
                _listener.Start();
                _logger.LogInformation("Operator health probes fallback listening on localhost:{Port}.", _port);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start fallback health probe listener on port {Port}.", _port);
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start health probe HTTP listener on port {Port}.", _port);
            return;
        }

        stoppingToken.Register(() =>
        {
            _isReady = false;
            try
            {
                if (_listener.IsListening)
                    _listener.Stop();
            }
            catch
            {
                // Ignore during shutdown
            }
        });

        while (!stoppingToken.IsCancellationRequested && _listener.IsListening)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = ProcessRequestAsync(context);
            }
            catch (HttpListenerException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error accepting health probe request.");
            }
        }
    }

    private async Task ProcessRequestAsync(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? string.Empty;
            var response = context.Response;

            if (path.Equals("/healthz", StringComparison.OrdinalIgnoreCase))
            {
                // Liveness probe: alive if listener is up
                response.StatusCode = (int)HttpStatusCode.OK;
                response.ContentType = "application/json";
                var body = Encoding.UTF8.GetBytes("{\"status\":\"ok\"}");
                await response.OutputStream.WriteAsync(body);
            }
            else if (path.Equals("/readyz", StringComparison.OrdinalIgnoreCase))
            {
                // Readiness probe: ready if operator initialized and not shutting down
                response.StatusCode = _isReady ? (int)HttpStatusCode.OK : (int)HttpStatusCode.ServiceUnavailable;
                response.ContentType = "application/json";
                var status = _isReady ? "ok" : "not_ready";
                var body = Encoding.UTF8.GetBytes($"{{\"status\":\"{status}\"}}");
                await response.OutputStream.WriteAsync(body);
            }
            else
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
            }

            response.Close();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error processing health probe response.");
        }
    }

    public override void Dispose()
    {
        try
        {
            if (_listener.IsListening)
                _listener.Stop();
            _listener.Close();
        }
        catch
        {
            // Ignore on dispose
        }
        base.Dispose();
    }
}
