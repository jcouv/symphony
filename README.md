# Symphony

Symphony turns project work into isolated, autonomous implementation runs, allowing teams to manage
work instead of supervising coding agents.

[![Symphony demo video preview](.github/media/symphony-demo-poster.jpg)](https://player.vimeo.com/video/1186371009?h=5626e4b899)

_Symphony monitors local Markdown issue files for work and spawns Copilot CLI agents to handle the tasks. Engineers manage the work at the issue-tracking level instead of supervising each coding-agent turn._

> [!WARNING]
> Symphony is a low-key engineering preview for testing in trusted environments.

## Running Symphony

### Requirements

Symphony works best in codebases that have adopted
[harness engineering](https://openai.com/index/harness-engineering/). Symphony is the next step --
moving from managing coding agents to managing work that needs to get done.

### Option 1. Make your own

Tell your favorite coding agent to build Symphony in a programming language of your choice:

> Implement Symphony according to the following spec:
> https://github.com/openai/symphony/blob/main/SPEC.md

### Option 2. Use our experimental reference implementation

Check out [elixir/README.md](elixir/README.md) for instructions on how to set up your environment
and run the Elixir-based Symphony implementation. You can also ask your favorite coding agent to
help with the setup:

> Set up Symphony for my repository based on
> https://github.com/openai/symphony/blob/main/elixir/README.md

### Option 3. Use the C# implementation

This repository includes a .NET implementation at the repository root. It implements the core
workflow/config, local Markdown-file issue tracker, workspace manager, Copilot CLI runner,
orchestrator, structured logs, and optional local HTTP status API from `SPEC.md`.

```powershell
dotnet test .\Symphony.sln
dotnet run --project .\src\Symphony.Cli\Symphony.Cli.csproj -- .\WORKFLOW.md
```

The included `WORKFLOW.md` uses local markdown issues by default. Create active issue files under
`issues\active\`, for example:

```powershell
New-Item -ItemType Directory -Force .\issues\active | Out-Null
@'
# Fix issue with UI

The button is misaligned.
'@ | Set-Content .\issues\active\fix-issue-with-ui.md
```

Moving an issue file between immediate subfolders changes its state, so moving
`issues\active\fix-issue-with-ui.md` to `issues\done\fix-issue-with-ui.md` marks it done. Add
`--port <port>` to enable the local dashboard and JSON API:

```powershell
dotnet run --project .\src\Symphony.Cli\Symphony.Cli.csproj -- .\WORKFLOW.md --port 4545
```

Runtime prerequisites for real work dispatch are an authenticated `copilot` executable on `PATH` and
any tools referenced by your `WORKFLOW.md` hooks.

---

## License

This project is licensed under the [Apache License 2.0](LICENSE).
