using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Symphony.Core;

public interface IAgentRunner
{
    Task<WorkerResult> RunAsync(
        Issue issue,
        int? attempt,
        WorkflowDefinition workflow,
        ServiceConfig config,
        Func<AgentEvent, Task> onEvent,
        CancellationToken cancellationToken);
}

public sealed class AgentRunner(
    WorkspaceManager workspaceManager,
    StrictPromptRenderer promptRenderer,
    CopilotCliClient copilotCliClient,
    ITrackerClient trackerClient,
    ILogger<AgentRunner> logger) : IAgentRunner
{
    public async Task<WorkerResult> RunAsync(
        Issue issue,
        int? attempt,
        WorkflowDefinition workflow,
        ServiceConfig config,
        Func<AgentEvent, Task> onEvent,
        CancellationToken cancellationToken)
    {
        WorkspaceInfo? workspace = null;
        try
        {
            workspace = await workspaceManager.CreateForIssueAsync(issue.Identifier, config, cancellationToken).ConfigureAwait(false);
            await workspaceManager.RunBeforeRunAsync(config, workspace.Path, cancellationToken).ConfigureAwait(false);
            WorkspaceManager.EnsureWorkspaceCwd(config.Workspace.Root, workspace.Path);

            var currentIssue = issue;
            for (var turn = 1; turn <= config.Agent.MaxTurns; turn++)
            {
                var prompt = turn == 1
                    ? promptRenderer.Render(workflow.PromptTemplate, currentIssue, attempt)
                    : $"Continue working on {currentIssue.Identifier}. Do not resend the original task; proceed from the existing thread context.";

                var result = await copilotCliClient.RunTurnAsync(config, workspace.Path, prompt, currentIssue, turn, onEvent, cancellationToken)
                    .ConfigureAwait(false);
                if (result.Reason != WorkerExitReason.Normal)
                {
                    return result;
                }

                var refreshed = await trackerClient.FetchIssueStatesByIdsAsync(config, [currentIssue.Id], cancellationToken).ConfigureAwait(false);
                currentIssue = refreshed.FirstOrDefault() ?? currentIssue;
                if (!config.Tracker.ActiveStates.Select(IssueState.Normalize).Contains(IssueState.Normalize(currentIssue.State)))
                {
                    break;
                }
            }

            return new WorkerResult(WorkerExitReason.Normal);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new WorkerResult(WorkerExitReason.CanceledByReconciliation, "worker cancelled");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "issue_id={IssueId} issue_identifier={IssueIdentifier} status=failed reason={Reason}", issue.Id, issue.Identifier, ex.Message);
            return new WorkerResult(WorkerExitReason.Failed, ex.Message);
        }
        finally
        {
            if (workspace is not null)
            {
                await workspaceManager.RunAfterRunBestEffortAsync(config, workspace.Path, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }
}

public sealed class CopilotCliClient(ILogger<CopilotCliClient> logger)
{
    public async Task<WorkerResult> RunTurnAsync(
        ServiceConfig config,
        string workspacePath,
        string prompt,
        Issue issue,
        int turnNumber,
        Func<AgentEvent, Task> onEvent,
        CancellationToken cancellationToken)
    {
        WorkspaceManager.EnsureWorkspaceCwd(config.Workspace.Root, workspacePath);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(config.Assistant.TurnTimeoutMs);

        var sessionId = $"{issue.Identifier}-{Guid.NewGuid():N}";
        using var process = new Process
        {
            StartInfo = CreateStartInfo(config.Assistant, prompt, workspacePath),
            EnableRaisingEvents = true
        };

        try
        {
            process.Start();
            await onEvent(new AgentEvent(
                "session_started",
                DateTimeOffset.UtcNow,
                sessionId,
                sessionId,
                turnNumber.ToString(),
                process.Id,
                $"copilot turn {turnNumber} started")).ConfigureAwait(false);

            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);

            if (process.ExitCode == 0)
            {
                await onEvent(new AgentEvent(
                    "turn_completed",
                    DateTimeOffset.UtcNow,
                    sessionId,
                    sessionId,
                    turnNumber.ToString(),
                    process.Id,
                    Truncate(output))).ConfigureAwait(false);
                return new WorkerResult(WorkerExitReason.Normal);
            }

            var message = string.IsNullOrWhiteSpace(error) ? output : error;
            await onEvent(new AgentEvent(
                "turn_failed",
                DateTimeOffset.UtcNow,
                sessionId,
                sessionId,
                turnNumber.ToString(),
                process.Id,
                Truncate(message))).ConfigureAwait(false);
            return new WorkerResult(WorkerExitReason.Failed, $"copilot exited with code {process.ExitCode}: {Truncate(message, 500)}");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            TryKill(process);
            await onEvent(new AgentEvent(
                "turn_failed",
                DateTimeOffset.UtcNow,
                sessionId,
                sessionId,
                turnNumber.ToString(),
                process.Id,
                "turn_timeout")).ConfigureAwait(false);
            return new WorkerResult(WorkerExitReason.TimedOut, "turn_timeout");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "issue_id={IssueId} issue_identifier={IssueIdentifier} status=copilot_failed", issue.Id, issue.Identifier);
            return new WorkerResult(WorkerExitReason.Failed, ex.Message);
        }
    }

    private static ProcessStartInfo CreateStartInfo(AssistantConfig config, string prompt, string workspacePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = config.Command,
            WorkingDirectory = workspacePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in config.Arguments.Count == 0 ? ["-p", "{{ prompt }}", "--allow-all", "--no-ask-user", "--silent"] : config.Arguments)
        {
            startInfo.ArgumentList.Add(argument.Replace("{{ prompt }}", prompt, StringComparison.Ordinal));
        }

        return startInfo;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort process cleanup.
        }
    }

    private static string Truncate(string value, int max = 4000)
        => value.Length <= max ? value : value[..max] + "...";
}
