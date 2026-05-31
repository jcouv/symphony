using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Symphony.Core;

public sealed class SymphonyOrchestrator(
    string? workflowPath,
    WorkflowLoader workflowLoader,
    ConfigLayer configLayer,
    ITrackerClient trackerClient,
    IAgentRunner agentRunner,
    WorkspaceManager workspaceManager,
    ILogger<SymphonyOrchestrator> logger) : BackgroundService
{
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly Dictionary<string, RunningEntry> _running = [];
    private readonly HashSet<string> _claimed = [];
    private readonly Dictionary<string, RetryEntry> _retryAttempts = [];
    private readonly HashSet<string> _completed = [];
    private readonly TokenAccumulator _tokens = new();
    private WorkflowDefinition? _workflow;
    private ServiceConfig? _config;
    private WorkflowReloader? _reloader;
    private double _endedRuntimeSeconds;
    private object? _rateLimits;

    public async Task RequestRefreshAsync(CancellationToken cancellationToken = default)
        => await TickAsync(cancellationToken).ConfigureAwait(false);

    public RuntimeSnapshot Snapshot()
    {
        _stateLock.Wait();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var activeSeconds = _running.Values.Sum(entry => Math.Max(0, (now - entry.StartedAt).TotalSeconds));
            return new RuntimeSnapshot
            {
                GeneratedAt = now,
                Running = _running.Values.Select(entry => new RunningIssueSnapshot
                {
                    IssueId = entry.Issue.Id,
                    IssueIdentifier = entry.Issue.Identifier,
                    State = entry.Issue.State,
                    SessionId = entry.SessionId,
                    TurnCount = entry.TurnCount,
                    LastEvent = entry.LastAgentEvent,
                    LastMessage = entry.LastAgentMessage,
                    LastEventAt = entry.LastAgentTimestamp,
                    StartedAt = entry.StartedAt,
                    InputTokens = entry.AgentInputTokens,
                    OutputTokens = entry.AgentOutputTokens,
                    TotalTokens = entry.AgentTotalTokens,
                    WorkspacePath = entry.WorkspacePath
                }).ToList(),
                Retrying = _retryAttempts.Values.Select(entry => new RetrySnapshot
                {
                    IssueId = entry.IssueId,
                    IssueIdentifier = entry.Identifier,
                    Attempt = entry.Attempt,
                    DueAt = entry.DueAtUtc,
                    Error = entry.Error
                }).ToList(),
                AgentTotals = new TokenTotals(
                    _tokens.InputTokens,
                    _tokens.OutputTokens,
                    _tokens.TotalTokens,
                    _endedRuntimeSeconds + activeSeconds),
                RateLimits = _rateLimits,
                Tracked = new Dictionary<string, object?>
                {
                    ["workflow_path"] = _workflow?.Path,
                    ["last_reload_error"] = _reloader?.LastReloadError?.Message
                }
            };
        }
        finally
        {
            _stateLock.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var selectedPath = workflowLoader.SelectPath(workflowPath);
        _reloader = new WorkflowReloader(workflowLoader, selectedPath);
        _workflow = await _reloader.LoadInitialAsync(stoppingToken).ConfigureAwait(false);
        _config = configLayer.Resolve(_workflow);
        configLayer.ValidateForDispatch(_config);
        _reloader.Reloaded += (_, workflow) =>
        {
            try
            {
                var config = configLayer.Resolve(workflow);
                _stateLock.Wait(stoppingToken);
                try
                {
                    _workflow = workflow;
                    _config = config;
                }
                finally
                {
                    _stateLock.Release();
                }

                logger.LogInformation("workflow_path={WorkflowPath} status=reloaded", workflow.Path);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "workflow_path={WorkflowPath} status=reload_failed", workflow.Path);
            }
        };
        _reloader.Start();

        await StartupTerminalWorkspaceCleanupAsync(_config, stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            await TickAsync(stoppingToken).ConfigureAwait(false);
            var delay = CurrentConfig()?.Polling.IntervalMs ?? 30_000;
            await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        if (_reloader is not null)
        {
            await _reloader.ReloadIfChangedAsync(cancellationToken).ConfigureAwait(false);
        }

        var config = CurrentConfig();
        if (config is null)
        {
            return;
        }

        await ReconcileRunningIssuesAsync(config, cancellationToken).ConfigureAwait(false);

        try
        {
            configLayer.ValidateForDispatch(config);
        }
        catch (ConfigValidationException ex)
        {
            logger.LogError("status=dispatch_skipped error_code={Code} reason={Reason}", ex.Code, ex.Message);
            return;
        }

        IReadOnlyList<Issue> candidates;
        try
        {
            candidates = await trackerClient.FetchCandidateIssuesAsync(config, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "status=dispatch_skipped reason=tracker_fetch_failed");
            return;
        }

        foreach (var issue in SortForDispatch(candidates))
        {
            if (!await ShouldDispatchAsync(issue, config, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            if (!await DispatchIssueAsync(issue, null, config, cancellationToken).ConfigureAwait(false))
            {
                break;
            }
        }
    }

    private async Task<bool> ShouldDispatchAsync(Issue issue, ServiceConfig config, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(issue.Id)
            || string.IsNullOrWhiteSpace(issue.Identifier)
            || string.IsNullOrWhiteSpace(issue.Title)
            || string.IsNullOrWhiteSpace(issue.State))
        {
            return false;
        }

        var active = config.Tracker.ActiveStates.Select(IssueState.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var terminal = config.Tracker.TerminalStates.Select(IssueState.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var state = IssueState.Normalize(issue.State);
        if (!active.Contains(state) || terminal.Contains(state))
        {
            return false;
        }

        if (state == "todo" && issue.BlockedBy.Any(blocker => blocker.State is null || !terminal.Contains(IssueState.Normalize(blocker.State))))
        {
            return false;
        }

        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_running.ContainsKey(issue.Id) || _claimed.Contains(issue.Id))
            {
                return false;
            }

            if (_running.Count >= config.Agent.MaxConcurrentAgents)
            {
                return false;
            }

            var perStateLimit = config.Agent.MaxConcurrentAgentsByState.TryGetValue(state, out var limit)
                ? limit
                : config.Agent.MaxConcurrentAgents;
            var runningInState = _running.Values.Count(entry => IssueState.Normalize(entry.Issue.State) == state);
            return runningInState < perStateLimit;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private async Task<bool> DispatchIssueAsync(Issue issue, int? attempt, ServiceConfig config, CancellationToken cancellationToken)
    {
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        RunningEntry entry;
        try
        {
            if (_running.Count >= config.Agent.MaxConcurrentAgents || _claimed.Contains(issue.Id))
            {
                return false;
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            entry = new RunningEntry(issue, cts)
            {
                RetryAttempt = attempt,
                WorkspacePath = Path.Combine(config.Workspace.Root, WorkspaceManager.SanitizeWorkspaceKey(issue.Identifier))
            };
            _running[issue.Id] = entry;
            _claimed.Add(issue.Id);
            if (_retryAttempts.Remove(issue.Id, out var retry))
            {
                retry.Timer.Dispose();
            }
        }
        finally
        {
            _stateLock.Release();
        }

        var workflow = CurrentWorkflow();
        if (workflow is null)
        {
            return false;
        }

        entry.Task = Task.Run(async () =>
        {
            logger.LogInformation("issue_id={IssueId} issue_identifier={IssueIdentifier} status=started", issue.Id, issue.Identifier);
            var result = await agentRunner.RunAsync(
                issue,
                attempt,
                workflow,
                config,
                ev => OnAgentEventAsync(issue.Id, ev),
                entry.Cancellation.Token).ConfigureAwait(false);
            await OnWorkerExitAsync(issue.Id, result, config, CancellationToken.None).ConfigureAwait(false);
        }, CancellationToken.None);

        return true;
    }

    private async Task OnAgentEventAsync(string issueId, AgentEvent ev)
    {
        await _stateLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_running.TryGetValue(issueId, out var entry))
            {
                return;
            }

            entry.SessionId = ev.SessionId ?? entry.SessionId;
            entry.AgentPid = ev.AgentPid;
            entry.LastAgentEvent = ev.Event;
            entry.LastAgentTimestamp = ev.Timestamp;
            entry.LastAgentMessage = ev.Message;
            if (ev.Event == "session_started")
            {
                entry.TurnCount++;
            }

            var delta = entry.UpdateAbsoluteTokens(ev.InputTokens, ev.OutputTokens, ev.TotalTokens);
            _tokens.Add(delta.Input, delta.Output, delta.Total);
            if (ev.RateLimits is not null)
            {
                _rateLimits = ev.RateLimits;
            }
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private async Task OnWorkerExitAsync(string issueId, WorkerResult result, ServiceConfig config, CancellationToken cancellationToken)
    {
        RunningEntry? entry;
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_running.Remove(issueId, out entry))
            {
                return;
            }

            _endedRuntimeSeconds += Math.Max(0, (DateTimeOffset.UtcNow - entry.StartedAt).TotalSeconds);
            entry.Cancellation.Dispose();
            if (result.Reason == WorkerExitReason.Normal)
            {
                _completed.Add(issueId);
            }
        }
        finally
        {
            _stateLock.Release();
        }

        if (entry is null)
        {
            return;
        }

        if (result.Reason == WorkerExitReason.Normal)
        {
            ScheduleRetry(issueId, entry.Issue.Identifier, 1, TimeSpan.FromSeconds(1), null, config);
            logger.LogInformation("issue_id={IssueId} issue_identifier={IssueIdentifier} status=completed retrying=continuation", issueId, entry.Issue.Identifier);
        }
        else
        {
            var nextAttempt = Math.Max((entry.RetryAttempt ?? 0) + 1, 1);
            var delay = FailureBackoff(nextAttempt, config.Agent.MaxRetryBackoffMs);
            ScheduleRetry(issueId, entry.Issue.Identifier, nextAttempt, delay, result.Error ?? result.Reason.ToString(), config);
            logger.LogWarning("issue_id={IssueId} issue_identifier={IssueIdentifier} status=failed retry_attempt={Attempt} reason={Reason}", issueId, entry.Issue.Identifier, nextAttempt, result.Error);
        }
    }

    private void ScheduleRetry(string issueId, string identifier, int attempt, TimeSpan delay, string? error, ServiceConfig config)
    {
        _stateLock.Wait();
        try
        {
            if (_retryAttempts.Remove(issueId, out var existing))
            {
                existing.Timer.Dispose();
            }

            var due = DateTimeOffset.UtcNow.Add(delay);
            Timer? timer = null;
            timer = new Timer(_ => _ = OnRetryTimerAsync(issueId, config, CancellationToken.None), null, delay, Timeout.InfiniteTimeSpan);
            _retryAttempts[issueId] = new RetryEntry(issueId, identifier, attempt, due, error, timer);
            _claimed.Add(issueId);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private async Task OnRetryTimerAsync(string issueId, ServiceConfig config, CancellationToken cancellationToken)
    {
        RetryEntry? retry;
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_retryAttempts.Remove(issueId, out retry))
            {
                return;
            }

            retry.Timer.Dispose();
        }
        finally
        {
            _stateLock.Release();
        }

        IReadOnlyList<Issue> candidates;
        try
        {
            candidates = await trackerClient.FetchCandidateIssuesAsync(config, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ScheduleRetry(issueId, retry.Identifier, retry.Attempt + 1, FailureBackoff(retry.Attempt + 1, config.Agent.MaxRetryBackoffMs), "retry poll failed", config);
            return;
        }

        var issue = candidates.FirstOrDefault(candidate => candidate.Id == issueId);
        if (issue is null)
        {
            await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _claimed.Remove(issueId);
            }
            finally
            {
                _stateLock.Release();
            }

            return;
        }

        if (!await ShouldDispatchRetryAsync(issue, config, cancellationToken).ConfigureAwait(false))
        {
            ScheduleRetry(issueId, issue.Identifier, retry.Attempt + 1, FailureBackoff(retry.Attempt + 1, config.Agent.MaxRetryBackoffMs), "no available orchestrator slots", config);
            return;
        }

        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _claimed.Remove(issueId);
        }
        finally
        {
            _stateLock.Release();
        }

        await DispatchIssueAsync(issue, retry.Attempt, config, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> ShouldDispatchRetryAsync(Issue issue, ServiceConfig config, CancellationToken cancellationToken)
    {
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_running.ContainsKey(issue.Id) || _running.Count >= config.Agent.MaxConcurrentAgents)
            {
                return false;
            }

            var state = IssueState.Normalize(issue.State);
            var perStateLimit = config.Agent.MaxConcurrentAgentsByState.TryGetValue(state, out var limit)
                ? limit
                : config.Agent.MaxConcurrentAgents;
            return _running.Values.Count(entry => IssueState.Normalize(entry.Issue.State) == state) < perStateLimit;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private async Task ReconcileRunningIssuesAsync(ServiceConfig config, CancellationToken cancellationToken)
    {
        IReadOnlyList<RunningEntry> running;
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            running = _running.Values.ToList();
        }
        finally
        {
            _stateLock.Release();
        }

        if (running.Count == 0)
        {
            return;
        }

        if (config.Assistant.StallTimeoutMs > 0)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var entry in running)
            {
                var since = entry.LastAgentTimestamp ?? entry.StartedAt;
                if ((now - since).TotalMilliseconds > config.Assistant.StallTimeoutMs)
                {
                    entry.Cancellation.Cancel();
                    await OnWorkerExitAsync(entry.Issue.Id, new WorkerResult(WorkerExitReason.Stalled, "stall_timeout"), config, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        IReadOnlyList<Issue> refreshed;
        try
        {
            refreshed = await trackerClient.FetchIssueStatesByIdsAsync(config, running.Select(entry => entry.Issue.Id).ToList(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "status=reconcile_skipped reason=state_refresh_failed");
            return;
        }

        var active = config.Tracker.ActiveStates.Select(IssueState.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var terminal = config.Tracker.TerminalStates.Select(IssueState.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var issue in refreshed)
        {
            var state = IssueState.Normalize(issue.State);
            if (terminal.Contains(state))
            {
                await TerminateRunningIssueAsync(issue, config, cleanupWorkspace: true, cancellationToken).ConfigureAwait(false);
            }
            else if (active.Contains(state))
            {
                await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (_running.TryGetValue(issue.Id, out var entry))
                    {
                        entry.Issue = issue;
                    }
                }
                finally
                {
                    _stateLock.Release();
                }
            }
            else
            {
                await TerminateRunningIssueAsync(issue, config, cleanupWorkspace: false, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task TerminateRunningIssueAsync(Issue issue, ServiceConfig config, bool cleanupWorkspace, CancellationToken cancellationToken)
    {
        RunningEntry? entry;
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_running.Remove(issue.Id, out entry))
            {
                return;
            }

            _claimed.Remove(issue.Id);
            entry.Cancellation.Cancel();
            entry.Cancellation.Dispose();
        }
        finally
        {
            _stateLock.Release();
        }

        if (cleanupWorkspace)
        {
            await workspaceManager.RemoveForIssueAsync(issue.Identifier, config, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task StartupTerminalWorkspaceCleanupAsync(ServiceConfig config, CancellationToken cancellationToken)
    {
        try
        {
            var terminalIssues = await trackerClient.FetchIssuesByStatesAsync(config, config.Tracker.TerminalStates, cancellationToken).ConfigureAwait(false);
            foreach (var issue in terminalIssues)
            {
                await workspaceManager.RemoveForIssueAsync(issue.Identifier, config, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "status=startup_cleanup_failed");
        }
    }

    public static IReadOnlyList<Issue> SortForDispatch(IEnumerable<Issue> issues)
    {
        return issues
            .OrderBy(issue => issue.Priority ?? int.MaxValue)
            .ThenBy(issue => issue.CreatedAt ?? DateTimeOffset.MaxValue)
            .ThenBy(issue => issue.Identifier, StringComparer.Ordinal)
            .ToList();
    }

    public static TimeSpan FailureBackoff(int attempt, int maxRetryBackoffMs)
    {
        var exponent = Math.Min(attempt - 1, 30);
        var delay = 10_000d * Math.Pow(2, exponent);
        return TimeSpan.FromMilliseconds(Math.Min(delay, maxRetryBackoffMs));
    }

    private WorkflowDefinition? CurrentWorkflow()
    {
        _stateLock.Wait();
        try
        {
            return _workflow;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private ServiceConfig? CurrentConfig()
    {
        _stateLock.Wait();
        try
        {
            return _config;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private sealed class RunningEntry
    {
        public RunningEntry(Issue issue, CancellationTokenSource cancellation)
        {
            Issue = issue;
            Cancellation = cancellation;
        }

        public Issue Issue { get; set; }
        public CancellationTokenSource Cancellation { get; }
        public Task? Task { get; set; }
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        public int? RetryAttempt { get; init; }
        public string? WorkspacePath { get; init; }
        public string? SessionId { get; set; }
        public int? AgentPid { get; set; }
        public string? LastAgentEvent { get; set; }
        public DateTimeOffset? LastAgentTimestamp { get; set; }
        public string? LastAgentMessage { get; set; }
        public int TurnCount { get; set; }
        public long AgentInputTokens { get; set; }
        public long AgentOutputTokens { get; set; }
        public long AgentTotalTokens { get; set; }
        public long LastReportedInputTokens { get; set; }
        public long LastReportedOutputTokens { get; set; }
        public long LastReportedTotalTokens { get; set; }

        public (long Input, long Output, long Total) UpdateAbsoluteTokens(long? input, long? output, long? total)
        {
            var inputDelta = Delta(input, LastReportedInputTokens);
            if (input.HasValue)
            {
                LastReportedInputTokens = input.Value;
                AgentInputTokens = input.Value;
            }

            var outputDelta = Delta(output, LastReportedOutputTokens);
            if (output.HasValue)
            {
                LastReportedOutputTokens = output.Value;
                AgentOutputTokens = output.Value;
            }

            var totalDelta = Delta(total, LastReportedTotalTokens);
            if (total.HasValue)
            {
                LastReportedTotalTokens = total.Value;
                AgentTotalTokens = total.Value;
            }

            return (inputDelta, outputDelta, totalDelta);
        }

        private static long Delta(long? value, long last)
        {
            if (!value.HasValue)
            {
                return 0;
            }

            var delta = Math.Max(0, value.Value - last);
            return delta;
        }
    }

    private sealed record RetryEntry(string IssueId, string Identifier, int Attempt, DateTimeOffset DueAtUtc, string? Error, Timer Timer);

    private sealed class TokenAccumulator
    {
        public long InputTokens { get; private set; }
        public long OutputTokens { get; private set; }
        public long TotalTokens { get; private set; }

        public void Add(long input, long output, long total)
        {
            InputTokens += input;
            OutputTokens += output;
            TotalTokens += total;
        }
    }
}
