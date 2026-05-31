using Symphony.Core;

namespace Symphony.Tests;

public sealed class LocalIssueTrackerTests
{
    [Fact]
    public async Task CandidateFetchReadsMarkdownFilesFromActiveFolder()
    {
        using var temp = new TempDirectory();
        var issuesRoot = Path.Combine(temp.Path, "issues");
        var active = Path.Combine(issuesRoot, "active");
        Directory.CreateDirectory(active);
        await File.WriteAllTextAsync(Path.Combine(active, "fix-issue-with-ui.md"), """
            ---
            priority: 1
            labels:
              - UI
            blocked_by:
              - setup-project
            ---
            # Fix issue with UI

            The button is misaligned.
            """);

        var client = new LocalIssueTrackerClient();
        var issues = await client.FetchCandidateIssuesAsync(Config(issuesRoot), CancellationToken.None);

        var issue = Assert.Single(issues);
        Assert.Equal("fix-issue-with-ui", issue.Id);
        Assert.Equal("fix-issue-with-ui", issue.Identifier);
        Assert.Equal("Fix issue with UI", issue.Title);
        Assert.Equal("active", issue.State);
        Assert.Equal(1, issue.Priority);
        Assert.Equal("ui", Assert.Single(issue.Labels));
        Assert.Equal("setup-project", Assert.Single(issue.BlockedBy).Identifier);
        Assert.Contains("button is misaligned", issue.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StateRefreshFindsIssueAfterMovingBetweenFolders()
    {
        using var temp = new TempDirectory();
        var issuesRoot = Path.Combine(temp.Path, "issues");
        var active = Path.Combine(issuesRoot, "active");
        var review = Path.Combine(issuesRoot, "review");
        Directory.CreateDirectory(active);
        Directory.CreateDirectory(review);
        var activePath = Path.Combine(active, "fix-issue-with-ui.md");
        var reviewPath = Path.Combine(review, "fix-issue-with-ui.md");
        await File.WriteAllTextAsync(activePath, "# Fix issue with UI");
        File.Move(activePath, reviewPath);

        var client = new LocalIssueTrackerClient();
        var issues = await client.FetchIssueStatesByIdsAsync(Config(issuesRoot), ["fix-issue-with-ui"], CancellationToken.None);

        var issue = Assert.Single(issues);
        Assert.Equal("review", issue.State);
    }

    private static ServiceConfig Config(string issuesRoot) => new(
        new TrackerConfig("local", issuesRoot, ["active"], ["done"]),
        new PollingConfig(30_000),
        new WorkspaceConfig(Path.GetTempPath()),
        new HooksConfig(null, null, null, null, 60_000),
        new AgentConfig(10, 20, 300_000, new Dictionary<string, int>()),
        new AssistantConfig("copilot", "copilot", ["-p", "{{ prompt }}"], 1000, 1000),
        new ServerConfig(null));
}
