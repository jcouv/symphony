namespace Symphony.Core;

public sealed record TrackerConfig(
    string? Kind,
    string IssuesRoot,
    IReadOnlyList<string> ActiveStates,
    IReadOnlyList<string> TerminalStates);

public sealed record PollingConfig(int IntervalMs);

public sealed record WorkspaceConfig(string Root);

public sealed record HooksConfig(
    string? AfterCreate,
    string? BeforeRun,
    string? AfterRun,
    string? BeforeRemove,
    int TimeoutMs);

public sealed record AgentConfig(
    int MaxConcurrentAgents,
    int MaxTurns,
    int MaxRetryBackoffMs,
    IReadOnlyDictionary<string, int> MaxConcurrentAgentsByState);

public sealed record AssistantConfig(
    string Kind,
    string Command,
    IReadOnlyList<string> Arguments,
    int TurnTimeoutMs,
    int StallTimeoutMs);

public sealed record ServerConfig(int? Port);

public sealed record ServiceConfig(
    TrackerConfig Tracker,
    PollingConfig Polling,
    WorkspaceConfig Workspace,
    HooksConfig Hooks,
    AgentConfig Agent,
    AssistantConfig Assistant,
    ServerConfig Server);

public sealed class ConfigValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class ConfigLayer
{
    public ServiceConfig Resolve(WorkflowDefinition workflow)
    {
        var root = workflow.Config;
        var tracker = Map(root, "tracker");
        var polling = Map(root, "polling");
        var workspace = Map(root, "workspace");
        var hooks = Map(root, "hooks");
        var agent = Map(root, "agent");
        var assistant = Map(root, "assistant");
        var copilot = Map(root, "copilot");
        var server = Map(root, "server");

        var trackerKind = String(tracker, "kind") ?? "local";

        var workflowDirectory = Path.GetDirectoryName(workflow.Path) ?? Environment.CurrentDirectory;
        var issuesRoot = ResolvePath(String(tracker, "root") ?? "issues", workflowDirectory);
        var workspaceRoot = ResolveWorkspaceRoot(String(workspace, "root"), workflowDirectory);

        return new ServiceConfig(
            new TrackerConfig(
                trackerKind,
                issuesRoot,
                StringList(tracker, "active_states", ["active"]),
                StringList(tracker, "terminal_states", ["done", "closed", "cancelled", "canceled", "duplicate"])),
            new PollingConfig(Int(polling, "interval_ms", 30_000, min: 1)),
            new WorkspaceConfig(workspaceRoot),
            new HooksConfig(
                String(hooks, "after_create"),
                String(hooks, "before_run"),
                String(hooks, "after_run"),
                String(hooks, "before_remove"),
                Int(hooks, "timeout_ms", 60_000, min: 1)),
            new AgentConfig(
                Int(agent, "max_concurrent_agents", 10, min: 1),
                Int(agent, "max_turns", 20, min: 1),
                Int(agent, "max_retry_backoff_ms", 300_000, min: 1),
                PositiveStateMap(Map(agent, "max_concurrent_agents_by_state"))),
            ResolveAssistantConfig(assistant, copilot),
            new ServerConfig(NullableInt(server, "port", min: 0)));
    }

    private static AssistantConfig ResolveAssistantConfig(
        IReadOnlyDictionary<string, object?> assistant,
        IReadOnlyDictionary<string, object?> copilot)
    {
        var kind = String(assistant, "kind") ?? "copilot";
        var command = String(assistant, "command")
            ?? String(copilot, "command")
            ?? "copilot";
        var arguments = StringList(assistant, "arguments", StringList(copilot, "arguments", DefaultAssistantArguments()));

        return new AssistantConfig(
                kind,
                command,
                arguments,
                Int(assistant, "turn_timeout_ms", Int(copilot, "turn_timeout_ms", 3_600_000, min: 1), min: 1),
                Int(assistant, "stall_timeout_ms", Int(copilot, "stall_timeout_ms", 300_000)));
    }

    public void ValidateForDispatch(ServiceConfig config)
    {
        if (!string.Equals(config.Tracker.Kind, "local", StringComparison.OrdinalIgnoreCase))
        {
            throw new ConfigValidationException("unsupported_tracker_kind", "tracker.kind must be 'local'.");
        }

        if (!string.Equals(config.Assistant.Kind, "copilot", StringComparison.OrdinalIgnoreCase))
        {
            throw new ConfigValidationException("unsupported_assistant_kind", "assistant.kind must be 'copilot'.");
        }

        if (string.IsNullOrWhiteSpace(config.Assistant.Command))
        {
            throw new ConfigValidationException("missing_assistant_command", "assistant.command is required.");
        }
    }

    private static IReadOnlyDictionary<string, object?> Map(IReadOnlyDictionary<string, object?> root, string key)
    {
        return root.TryGetValue(key, out var value) && value is IReadOnlyDictionary<string, object?> map
            ? map
            : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    }

    private static string? String(IReadOnlyDictionary<string, object?> root, string key)
        => root.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static int Int(IReadOnlyDictionary<string, object?> root, string key, int fallback, int? min = null)
    {
        var value = NullableInt(root, key, min);
        return value ?? fallback;
    }

    private static int? NullableInt(IReadOnlyDictionary<string, object?> root, string key, int? min = null)
    {
        if (!root.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        if (!int.TryParse(value.ToString(), out var parsed) || (min.HasValue && parsed < min.Value))
        {
            throw new ConfigValidationException("invalid_config_value", $"{key} must be an integer >= {min ?? int.MinValue}.");
        }

        return parsed;
    }

    private static IReadOnlyList<string> StringList(
        IReadOnlyDictionary<string, object?> root,
        string key,
        IReadOnlyList<string> fallback)
    {
        if (!root.TryGetValue(key, out var value) || value is null)
        {
            return fallback;
        }

        if (value is IEnumerable<object?> list)
        {
            return list.Select(item => item?.ToString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Cast<string>()
                .ToList();
        }

        throw new ConfigValidationException("invalid_config_value", $"{key} must be a list of strings.");
    }

    private static IReadOnlyDictionary<string, int> PositiveStateMap(IReadOnlyDictionary<string, object?> root)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in root)
        {
            if (int.TryParse(value?.ToString(), out var parsed) && parsed > 0)
            {
                result[IssueState.Normalize(key)] = parsed;
            }
        }

        return result;
    }

    private static string ResolveWorkspaceRoot(string? raw, string workflowDirectory)
    {
        var value = string.IsNullOrWhiteSpace(raw)
            ? Path.Combine(Path.GetTempPath(), "symphony_workspaces")
            : ResolveEnv(raw);

        return ResolvePath(value, workflowDirectory);
    }

    private static string ResolvePath(string raw, string workflowDirectory)
    {
        var value = ExpandHome(ResolveEnv(raw));
        return Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(workflowDirectory, value));
    }

    private static string ResolveEnv(string raw)
    {
        if (raw.StartsWith('$') && raw.Length > 1 && raw.Skip(1).All(c => char.IsLetterOrDigit(c) || c == '_'))
        {
            return Environment.GetEnvironmentVariable(raw[1..]) ?? string.Empty;
        }

        return raw;
    }

    private static string ExpandHome(string path)
    {
        if (path == "~")
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        }

        return path;
    }

    private static IReadOnlyList<string> DefaultAssistantArguments()
        => ["-p", "{{ prompt }}", "--allow-all", "--no-ask-user", "--silent"];
}
