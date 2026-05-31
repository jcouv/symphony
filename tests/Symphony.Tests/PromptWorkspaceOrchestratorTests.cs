using Microsoft.Extensions.Logging.Abstractions;
using Symphony.Core;

namespace Symphony.Tests;

public sealed class PromptWorkspaceOrchestratorTests
{
    [Fact]
    public void PromptRendererRendersIssueAndAttemptStrictly()
    {
        var issue = SampleIssue() with { Labels = ["One", "Two"] };
        var rendered = new StrictPromptRenderer().Render("{{ issue.identifier }} {{ issue.title }} {{ attempt }} {{ issue.labels }}", issue, 2);

        Assert.Contains("ABC-123", rendered, StringComparison.Ordinal);
        Assert.Contains("Fix bug", rendered, StringComparison.Ordinal);
        Assert.Contains("2", rendered, StringComparison.Ordinal);
        Assert.Contains("One", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void PromptRendererRejectsUnknownVariablesAndFilters()
    {
        var renderer = new StrictPromptRenderer();
        Assert.Equal(
            "template_render_error",
            Assert.Throws<PromptRenderException>(() => renderer.Render("{{ issue.nope }}", SampleIssue(), null)).Code);
        Assert.Equal(
            "template_render_error",
            Assert.Throws<PromptRenderException>(() => renderer.Render("{{ issue.title | upcase }}", SampleIssue(), null)).Code);
    }

    [Fact]
    public async Task WorkspaceManagerSanitizesCreatesAndRunsAfterCreateOnce()
    {
        using var temp = new TempDirectory();
        var marker = OperatingSystem.IsWindows() ? "$null = New-Item -Path .\\marker.txt -ItemType File" : "touch marker.txt";
        var config = ConfigWithWorkspace(temp.Path) with
        {
            Hooks = new HooksConfig(marker, null, null, null, 10_000)
        };
        var manager = new WorkspaceManager(NullLogger<WorkspaceManager>.Instance);

        var first = await manager.CreateForIssueAsync("ABC/123", config);
        var second = await manager.CreateForIssueAsync("ABC/123", config);

        Assert.Equal("ABC_123", first.WorkspaceKey);
        Assert.True(first.CreatedNow);
        Assert.False(second.CreatedNow);
        Assert.True(File.Exists(Path.Combine(first.Path, "marker.txt")));
    }

    [Fact]
    public void WorkspaceContainmentRejectsSiblingPrefix()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "symphony_root"));
        var sibling = root + "_evil";

        var error = Assert.Throws<WorkspaceException>(() => WorkspaceManager.EnsureUnderRoot(root, sibling));
        Assert.Equal("workspace_outside_root", error.Code);
    }

    [Fact]
    public void DispatchSortingAndBackoffFollowSpec()
    {
        var newer = SampleIssue() with { Identifier = "ABC-2", Priority = null, CreatedAt = DateTimeOffset.Parse("2026-01-02T00:00:00Z") };
        var older = SampleIssue() with { Identifier = "ABC-1", Priority = null, CreatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z") };
        var priority = SampleIssue() with { Identifier = "ABC-3", Priority = 1, CreatedAt = DateTimeOffset.Parse("2026-01-03T00:00:00Z") };

        var sorted = SymphonyOrchestrator.SortForDispatch([newer, older, priority]);

        Assert.Equal(["ABC-3", "ABC-1", "ABC-2"], sorted.Select(issue => issue.Identifier));
        Assert.Equal(TimeSpan.FromSeconds(10), SymphonyOrchestrator.FailureBackoff(1, 300_000));
        Assert.Equal(TimeSpan.FromSeconds(40), SymphonyOrchestrator.FailureBackoff(3, 300_000));
        Assert.Equal(TimeSpan.FromSeconds(30), SymphonyOrchestrator.FailureBackoff(10, 30_000));
    }

    private static ServiceConfig ConfigWithWorkspace(string workspaceRoot) => new(
        new TrackerConfig("local", Path.GetTempPath(), ["active"], ["done"]),
        new PollingConfig(30_000),
        new WorkspaceConfig(workspaceRoot),
        new HooksConfig(null, null, null, null, 60_000),
        new AgentConfig(10, 20, 300_000, new Dictionary<string, int>()),
        new AssistantConfig("copilot", "copilot", ["-p", "{{ prompt }}"], 1000, 1000),
        new ServerConfig(null));

    private static Issue SampleIssue() => new()
    {
        Id = "issue-id",
        Identifier = "ABC-123",
        Title = "Fix bug",
        State = "Todo",
        CreatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z")
    };
}
