using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Symphony.Core;

public sealed class WorkspaceException(string code, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;
}

public sealed class WorkspaceManager
{
    private static readonly Regex UnsafeWorkspaceCharacter = new(@"[^A-Za-z0-9._-]", RegexOptions.Compiled);
    private readonly ILogger<WorkspaceManager> _logger;

    public WorkspaceManager(ILogger<WorkspaceManager> logger)
    {
        _logger = logger;
    }

    public static string SanitizeWorkspaceKey(string issueIdentifier)
        => UnsafeWorkspaceCharacter.Replace(issueIdentifier, "_");

    public async Task<WorkspaceInfo> CreateForIssueAsync(
        string issueIdentifier,
        ServiceConfig config,
        CancellationToken cancellationToken = default)
    {
        var workspaceRoot = Path.GetFullPath(config.Workspace.Root);
        Directory.CreateDirectory(workspaceRoot);

        var key = SanitizeWorkspaceKey(issueIdentifier);
        var workspacePath = Path.GetFullPath(Path.Combine(workspaceRoot, key));
        EnsureUnderRoot(workspaceRoot, workspacePath);

        if (File.Exists(workspacePath) && !Directory.Exists(workspacePath))
        {
            throw new WorkspaceException("workspace_path_not_directory", $"Workspace path exists but is not a directory: {workspacePath}");
        }

        var createdNow = !Directory.Exists(workspacePath);
        if (createdNow)
        {
            Directory.CreateDirectory(workspacePath);
            if (!string.IsNullOrWhiteSpace(config.Hooks.AfterCreate))
            {
                var result = await RunHookAsync(
                    "after_create",
                    config.Hooks.AfterCreate,
                    workspacePath,
                    config.Hooks.TimeoutMs,
                    fatal: true,
                    cancellationToken).ConfigureAwait(false);

                if (!result.Succeeded)
                {
                    throw new WorkspaceException("after_create_hook_failed", result.Error ?? "after_create hook failed.");
                }
            }
        }

        return new WorkspaceInfo(workspacePath, key, createdNow);
    }

    public async Task RunBeforeRunAsync(ServiceConfig config, string workspacePath, CancellationToken cancellationToken)
    {
        EnsureWorkspaceCwd(config.Workspace.Root, workspacePath);
        if (string.IsNullOrWhiteSpace(config.Hooks.BeforeRun))
        {
            return;
        }

        var result = await RunHookAsync(
            "before_run",
            config.Hooks.BeforeRun,
            workspacePath,
            config.Hooks.TimeoutMs,
            fatal: true,
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new WorkspaceException("before_run_hook_failed", result.Error ?? "before_run hook failed.");
        }
    }

    public Task RunAfterRunBestEffortAsync(ServiceConfig config, string workspacePath, CancellationToken cancellationToken)
        => string.IsNullOrWhiteSpace(config.Hooks.AfterRun)
            ? Task.CompletedTask
            : RunHookAsync("after_run", config.Hooks.AfterRun, workspacePath, config.Hooks.TimeoutMs, fatal: false, cancellationToken);

    public async Task RemoveForIssueAsync(
        string issueIdentifier,
        ServiceConfig config,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(config.Workspace.Root);
        var path = Path.GetFullPath(Path.Combine(root, SanitizeWorkspaceKey(issueIdentifier)));
        EnsureUnderRoot(root, path);
        if (!Directory.Exists(path))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(config.Hooks.BeforeRemove))
        {
            await RunHookAsync("before_remove", config.Hooks.BeforeRemove, path, config.Hooks.TimeoutMs, fatal: false, cancellationToken)
                .ConfigureAwait(false);
        }

        Directory.Delete(path, recursive: true);
    }

    public static void EnsureWorkspaceCwd(string workspaceRoot, string workspacePath)
    {
        var fullRoot = Path.GetFullPath(workspaceRoot);
        var fullPath = Path.GetFullPath(workspacePath);
        EnsureUnderRoot(fullRoot, fullPath);
        if (!Directory.Exists(fullPath))
        {
            throw new WorkspaceException("invalid_workspace_cwd", $"Workspace cwd does not exist: {fullPath}");
        }
    }

    public static void EnsureUnderRoot(string workspaceRoot, string workspacePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        var path = Path.GetFullPath(workspacePath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.Equals(root, comparison)
            && !path.StartsWith(root + Path.DirectorySeparatorChar, comparison)
            && !path.StartsWith(root + Path.AltDirectorySeparatorChar, comparison))
        {
            throw new WorkspaceException("workspace_outside_root", $"Workspace path is outside workspace root: {path}");
        }
    }

    private async Task<HookResult> RunHookAsync(
        string hookName,
        string script,
        string cwd,
        int timeoutMs,
        bool fatal,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("hook={Hook} status=starting cwd={Cwd}", hookName, cwd);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMs);

        var startInfo = CreateShellStartInfo(script, cwd);
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var stderr = await errorTask.ConfigureAwait(false);
            _ = await outputTask.ConfigureAwait(false);
            if (process.ExitCode == 0)
            {
                _logger.LogInformation("hook={Hook} status=completed", hookName);
                return HookResult.Success();
            }

            var error = $"hook {hookName} failed with exit_code={process.ExitCode}: {Truncate(stderr)}";
            _logger.Log(fatal ? LogLevel.Error : LogLevel.Warning, "{Error}", error);
            return HookResult.Failure(error);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            TryKill(process);
            var error = $"hook {hookName} timed out after {timeoutMs}ms";
            _logger.Log(fatal ? LogLevel.Error : LogLevel.Warning, "{Error}", error);
            return HookResult.Failure(error);
        }
        catch (Exception ex)
        {
            var error = $"hook {hookName} failed: {ex.Message}";
            _logger.Log(fatal ? LogLevel.Error : LogLevel.Warning, ex, "hook={Hook} status=failed", hookName);
            return HookResult.Failure(error);
        }
    }

    private static ProcessStartInfo CreateShellStartInfo(string script, string cwd)
    {
        if (OperatingSystem.IsWindows())
        {
            var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
            return new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
        }

        return new ProcessStartInfo("/bin/sh", $"-lc {QuotePosix(script)}")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
    }

    private static string QuotePosix(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

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
            // Best-effort cleanup after timeout.
        }
    }

    private static string Truncate(string value, int max = 4000)
        => value.Length <= max ? value : value[..max] + "...";

    private sealed record HookResult(bool Succeeded, string? Error)
    {
        public static HookResult Success() => new(true, null);
        public static HookResult Failure(string error) => new(false, error);
    }
}
