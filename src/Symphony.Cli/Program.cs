using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Symphony.Core;

var parsed = CliOptions.Parse(args);
if (parsed.ShowHelp)
{
    Console.WriteLine("""
        Usage: symphony [path-to-WORKFLOW.md] [--port <port>]

        If no workflow path is supplied, ./WORKFLOW.md is used.
        Runtime prerequisite for real agent runs: an authenticated copilot executable on PATH.
        """);
    return 0;
}

try
{
    var loader = new WorkflowLoader();
    var workflow = await loader.LoadAsync(parsed.WorkflowPath);
    var config = new ConfigLayer().Resolve(workflow);
    var effectivePort = parsed.Port ?? config.Server.Port;

    var builder = Host.CreateApplicationBuilder(args);
    builder.Logging.ClearProviders();
    builder.Logging.AddSimpleConsole(options =>
    {
        options.SingleLine = true;
        options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
    });

    builder.Services.AddSingleton(loader);
    builder.Services.AddSingleton<ConfigLayer>();
    builder.Services.AddSingleton<StrictPromptRenderer>();
    builder.Services.AddSingleton<WorkspaceManager>();
    builder.Services.AddSingleton<CopilotCliClient>();
    builder.Services.AddSingleton<ITrackerClient, LocalIssueTrackerClient>();
    builder.Services.AddSingleton<IAgentRunner, AgentRunner>();
    builder.Services.AddSingleton(sp => new SymphonyOrchestrator(
        parsed.WorkflowPath,
        sp.GetRequiredService<WorkflowLoader>(),
        sp.GetRequiredService<ConfigLayer>(),
        sp.GetRequiredService<ITrackerClient>(),
        sp.GetRequiredService<IAgentRunner>(),
        sp.GetRequiredService<WorkspaceManager>(),
        sp.GetRequiredService<ILogger<SymphonyOrchestrator>>()));
    builder.Services.AddHostedService(sp => sp.GetRequiredService<SymphonyOrchestrator>());
    if (effectivePort.HasValue)
    {
        builder.Services.AddSingleton(new HttpStateServerOptions(effectivePort.Value));
        builder.Services.AddHostedService<HttpStateServer>();
    }

    await builder.Build().RunAsync();
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"symphony startup failed: {ex.Message}");
    return 1;
}

internal sealed record CliOptions(string? WorkflowPath, int? Port, bool ShowHelp)
{
    public static CliOptions Parse(string[] args)
    {
        string? workflowPath = null;
        int? port = null;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "-h" or "--help")
            {
                return new CliOptions(null, null, true);
            }

            if (arg == "--port")
            {
                if (i + 1 >= args.Length || !int.TryParse(args[++i], out var parsed) || parsed < 0)
                {
                    throw new ArgumentException("--port requires a non-negative integer.");
                }

                port = parsed;
                continue;
            }

            if (workflowPath is not null)
            {
                throw new ArgumentException($"Unexpected argument: {arg}");
            }

            workflowPath = arg;
        }

        return new CliOptions(workflowPath, port, false);
    }
}

internal sealed record HttpStateServerOptions(int Port);

internal sealed class HttpStateServer(
    HttpStateServerOptions options,
    SymphonyOrchestrator orchestrator,
    ILogger<HttpStateServer> logger) : BackgroundService
{
    private readonly HttpListener _listener = new();
    private int _effectivePort;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _effectivePort = options.Port == 0 ? GetEphemeralPort() : options.Port;
        _listener.Prefixes.Add($"http://127.0.0.1:{_effectivePort}/");
        _listener.Start();
        logger.LogInformation("http_status=started url=http://127.0.0.1:{Port}/", _effectivePort);

        while (!stoppingToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            _ = Task.Run(() => HandleAsync(context, stoppingToken), stoppingToken);
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _listener.Close();
        return base.StopAsync(cancellationToken);
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            var request = context.Request;
            if (request.HttpMethod == "GET" && request.Url?.AbsolutePath == "/")
            {
                await WriteHtmlAsync(context.Response, RenderDashboard(), cancellationToken).ConfigureAwait(false);
                return;
            }

            if (request.HttpMethod == "GET" && request.Url?.AbsolutePath == "/api/v1/state")
            {
                await WriteJsonAsync(context.Response, ToApiState(orchestrator.Snapshot()), cancellationToken).ConfigureAwait(false);
                return;
            }

            if (request.HttpMethod == "POST" && request.Url?.AbsolutePath == "/api/v1/refresh")
            {
                await orchestrator.RequestRefreshAsync(cancellationToken).ConfigureAwait(false);
                context.Response.StatusCode = 202;
                await WriteJsonAsync(context.Response, new
                {
                    queued = true,
                    coalesced = false,
                    requested_at = DateTimeOffset.UtcNow,
                    operations = new[] { "poll", "reconcile" }
                }, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (request.HttpMethod == "GET" && request.Url?.AbsolutePath.StartsWith("/api/v1/", StringComparison.Ordinal) == true)
            {
                var identifier = Uri.UnescapeDataString(request.Url.AbsolutePath["/api/v1/".Length..]);
                var snapshot = orchestrator.Snapshot();
                var running = snapshot.Running.FirstOrDefault(row => string.Equals(row.IssueIdentifier, identifier, StringComparison.OrdinalIgnoreCase));
                var retry = snapshot.Retrying.FirstOrDefault(row => string.Equals(row.IssueIdentifier, identifier, StringComparison.OrdinalIgnoreCase));
                if (running is null && retry is null)
                {
                    context.Response.StatusCode = 404;
                    await WriteJsonAsync(context.Response, new { error = new { code = "issue_not_found", message = "Issue is unknown to the current in-memory state." } }, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                await WriteJsonAsync(context.Response, new
                {
                    issue_identifier = identifier,
                    issue_id = running?.IssueId ?? retry?.IssueId,
                    status = running is not null ? "running" : "retrying",
                    workspace = new { path = running?.WorkspacePath },
                    running,
                    retry
                }, cancellationToken).ConfigureAwait(false);
                return;
            }

            context.Response.StatusCode = request.Url?.AbsolutePath.StartsWith("/api/v1/", StringComparison.Ordinal) == true ? 405 : 404;
            await WriteJsonAsync(context.Response, new { error = new { code = "not_found", message = "Route not found." } }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "http_status=request_failed");
        }
        finally
        {
            context.Response.Close();
        }
    }

    private string RenderDashboard()
    {
        var snapshot = orchestrator.Snapshot();
        return $$"""
            <!doctype html>
            <html>
            <head><title>Symphony</title><meta charset="utf-8"></head>
            <body>
            <h1>Symphony</h1>
            <p>Generated at {{WebUtility.HtmlEncode(snapshot.GeneratedAt.ToString("O"))}}</p>
            <h2>Running ({{snapshot.Running.Count}})</h2>
            <pre>{{WebUtility.HtmlEncode(JsonSerializer.Serialize(snapshot.Running, JsonOptions))}}</pre>
            <h2>Retrying ({{snapshot.Retrying.Count}})</h2>
            <pre>{{WebUtility.HtmlEncode(JsonSerializer.Serialize(snapshot.Retrying, JsonOptions))}}</pre>
            </body>
            </html>
            """;
    }

    private static object ToApiState(RuntimeSnapshot snapshot) => new
    {
        generated_at = snapshot.GeneratedAt,
        counts = new { running = snapshot.Running.Count, retrying = snapshot.Retrying.Count },
        running = snapshot.Running,
        retrying = snapshot.Retrying,
        agent_totals = snapshot.AgentTotals,
        rate_limits = snapshot.RateLimits,
        tracked = snapshot.Tracked
    };

    private static async Task WriteJsonAsync(HttpListenerResponse response, object payload, CancellationToken cancellationToken)
    {
        response.ContentType = "application/json";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteHtmlAsync(HttpListenerResponse response, string payload, CancellationToken cancellationToken)
    {
        response.ContentType = "text/html; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(payload);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static int GetEphemeralPort()
    {
        using var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}
