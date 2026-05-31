using System.Text.Json.Serialization;

namespace Symphony.Core;

public sealed record BlockerRef(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("identifier")] string? Identifier,
    [property: JsonPropertyName("state")] string? State);

public sealed record Issue
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("identifier")]
    public required string Identifier { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("priority")]
    public int? Priority { get; init; }

    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("branch_name")]
    public string? BranchName { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("labels")]
    public IReadOnlyList<string> Labels { get; init; } = [];

    [JsonPropertyName("blocked_by")]
    public IReadOnlyList<BlockerRef> BlockedBy { get; init; } = [];

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset? UpdatedAt { get; init; }
}

public sealed record WorkflowDefinition(
    IReadOnlyDictionary<string, object?> Config,
    string PromptTemplate,
    string Path,
    DateTimeOffset LoadedAt);

public sealed record WorkspaceInfo(string Path, string WorkspaceKey, bool CreatedNow);

public sealed record TokenTotals(long InputTokens, long OutputTokens, long TotalTokens, double SecondsRunning);

public sealed record AgentEvent(
    string Event,
    DateTimeOffset Timestamp,
    string? SessionId = null,
    string? ThreadId = null,
    string? TurnId = null,
    int? AgentPid = null,
    string? Message = null,
    long? InputTokens = null,
    long? OutputTokens = null,
    long? TotalTokens = null,
    object? RateLimits = null);

public enum WorkerExitReason
{
    Normal,
    Failed,
    TimedOut,
    Stalled,
    CanceledByReconciliation
}

public sealed record WorkerResult(WorkerExitReason Reason, string? Error = null);

public sealed record RunningIssueSnapshot
{
    public required string IssueId { get; init; }
    public required string IssueIdentifier { get; init; }
    public required string State { get; init; }
    public string? SessionId { get; init; }
    public int TurnCount { get; init; }
    public string? LastEvent { get; init; }
    public string? LastMessage { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? LastEventAt { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long TotalTokens { get; init; }
    public string? WorkspacePath { get; init; }
}

public sealed record RetrySnapshot
{
    public required string IssueId { get; init; }
    public required string IssueIdentifier { get; init; }
    public int Attempt { get; init; }
    public DateTimeOffset DueAt { get; init; }
    public string? Error { get; init; }
}

public sealed record RuntimeSnapshot
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<RunningIssueSnapshot> Running { get; init; } = [];
    public IReadOnlyList<RetrySnapshot> Retrying { get; init; } = [];
    public TokenTotals AgentTotals { get; init; } = new(0, 0, 0, 0);
    public object? RateLimits { get; init; }
    public IReadOnlyDictionary<string, object?> Tracked { get; init; } = new Dictionary<string, object?>();
}

public static class IssueState
{
    public static string Normalize(string state) => state.Trim().ToLowerInvariant();
}
