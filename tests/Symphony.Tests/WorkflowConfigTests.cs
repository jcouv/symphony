using Symphony.Core;

namespace Symphony.Tests;

public sealed class WorkflowConfigTests
{
    [Fact]
    public async Task WorkflowLoaderParsesYamlFrontMatterAndPrompt()
    {
        using var temp = new TempDirectory();
        var workflowPath = Path.Combine(temp.Path, "WORKFLOW.md");
        await File.WriteAllTextAsync(workflowPath, """
            ---
            tracker:
              kind: local
              root: tasks
            workspace:
              root: ./work
            ---

            Work on {{ issue.identifier }}.
            """);

        var workflow = await new WorkflowLoader().LoadAsync(workflowPath);
        var config = new ConfigLayer().Resolve(workflow);

        Assert.Equal("Work on {{ issue.identifier }}.", workflow.PromptTemplate);
        Assert.Equal("local", config.Tracker.Kind);
        Assert.Equal(Path.GetFullPath(Path.Combine(temp.Path, "tasks")), config.Tracker.IssuesRoot);
        Assert.Equal(Path.GetFullPath(Path.Combine(temp.Path, "work")), config.Workspace.Root);
    }

    [Fact]
    public async Task WorkflowLoaderRejectsNonMapFrontMatter()
    {
        using var temp = new TempDirectory();
        var workflowPath = Path.Combine(temp.Path, "WORKFLOW.md");
        await File.WriteAllTextAsync(workflowPath, """
            ---
            - nope
            ---
            prompt
            """);

        var error = await Assert.ThrowsAsync<WorkflowException>(() => new WorkflowLoader().LoadAsync(workflowPath));
        Assert.Equal("workflow_front_matter_not_a_map", error.Code);
    }

    [Fact]
    public async Task ConfigLayerAppliesDefaultsAndEnvResolution()
    {
        using var temp = new TempDirectory();
        var workflowPath = Path.Combine(temp.Path, "WORKFLOW.md");
        Environment.SetEnvironmentVariable("SYMPHONY_TEST_ISSUES_ROOT", Path.Combine(temp.Path, "custom-issues"));
        await File.WriteAllTextAsync(workflowPath, """
            ---
            tracker:
              root: $SYMPHONY_TEST_ISSUES_ROOT
            agent:
              max_concurrent_agents_by_state:
                Todo: 2
                Bad: 0
            ---
            """);

        var workflow = await new WorkflowLoader().LoadAsync(workflowPath);
        var config = new ConfigLayer().Resolve(workflow);

        Assert.Equal(Path.Combine(temp.Path, "custom-issues"), config.Tracker.IssuesRoot);
        Assert.Equal(30_000, config.Polling.IntervalMs);
        Assert.Equal(20, config.Agent.MaxTurns);
        Assert.Equal(2, config.Agent.MaxConcurrentAgentsByState["todo"]);
        Assert.False(config.Agent.MaxConcurrentAgentsByState.ContainsKey("bad"));
    }

    [Fact]
    public void DispatchValidationRejectsUnsupportedTracker()
    {
        var config = new ServiceConfig(
            new TrackerConfig("remote", Path.GetTempPath(), ["Todo"], ["Done"]),
            new PollingConfig(30_000),
            new WorkspaceConfig(Path.GetTempPath()),
            new HooksConfig(null, null, null, null, 60_000),
            new AgentConfig(10, 20, 300_000, new Dictionary<string, int>()),
            new AssistantConfig("copilot", "copilot", ["-p", "{{ prompt }}"], 1000, 1000),
            new ServerConfig(null));

        var error = Assert.Throws<ConfigValidationException>(() => new ConfigLayer().ValidateForDispatch(config));
        Assert.Equal("unsupported_tracker_kind", error.Code);
    }

    [Fact]
    public async Task LocalTrackerConfigDefaultsToIssuesFolderAndActiveState()
    {
        using var temp = new TempDirectory();
        var workflowPath = Path.Combine(temp.Path, "WORKFLOW.md");
        await File.WriteAllTextAsync(workflowPath, """
            ---
            tracker:
              kind: local
            ---
            {{ issue.title }}
            """);

        var workflow = await new WorkflowLoader().LoadAsync(workflowPath);
        var config = new ConfigLayer().Resolve(workflow);

        Assert.Equal("local", config.Tracker.Kind);
        Assert.Equal(Path.Combine(temp.Path, "issues"), config.Tracker.IssuesRoot);
        Assert.Equal(["active"], config.Tracker.ActiveStates);
        new ConfigLayer().ValidateForDispatch(config);
    }

    [Fact]
    public async Task AssistantConfigDefaultsToCopilot()
    {
        using var temp = new TempDirectory();
        var workflowPath = Path.Combine(temp.Path, "WORKFLOW.md");
        await File.WriteAllTextAsync(workflowPath, """
            ---
            tracker:
              kind: local
            ---
            {{ issue.title }}
            """);

        var workflow = await new WorkflowLoader().LoadAsync(workflowPath);
        var config = new ConfigLayer().Resolve(workflow);

        Assert.Equal("copilot", config.Assistant.Kind);
        Assert.Equal("copilot", config.Assistant.Command);
        Assert.Equal(["-p", "{{ prompt }}", "--allow-all", "--no-ask-user", "--silent"], config.Assistant.Arguments);
    }

    [Fact]
    public void DispatchValidationRejectsUnsupportedAssistant()
    {
        var config = new ServiceConfig(
            new TrackerConfig("local", Path.GetTempPath(), ["Todo"], ["Done"]),
            new PollingConfig(30_000),
            new WorkspaceConfig(Path.GetTempPath()),
            new HooksConfig(null, null, null, null, 60_000),
            new AgentConfig(10, 20, 300_000, new Dictionary<string, int>()),
            new AssistantConfig("other", "other", [], 1000, 1000),
            new ServerConfig(null));

        var error = Assert.Throws<ConfigValidationException>(() => new ConfigLayer().ValidateForDispatch(config));
        Assert.Equal("unsupported_assistant_kind", error.Code);
    }
}
