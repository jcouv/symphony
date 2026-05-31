# Symphony Service Specification

Status: Draft v2 (local Markdown tracker + GitHub Copilot CLI profile)

Purpose: Define a service that orchestrates Copilot CLI agents to get project work done from local
Markdown issue files.

## 1. Problem Statement

Symphony is a long-running automation service that reads work from a local Markdown-file issue
tracker, creates an isolated workspace for each eligible issue, and runs GitHub Copilot CLI inside
that workspace.

The service solves four operational problems:

- It turns issue execution into a repeatable daemon workflow instead of manual scripts.
- It isolates agent execution in per-issue workspaces.
- It keeps workflow policy in-repo through `WORKFLOW.md`.
- It provides structured observability for concurrent agent runs.

Symphony is a scheduler, local tracker reader, workspace manager, and Copilot CLI runner. Ticket
writes, pull requests, validation, and handoff policy are handled by the prompt and tools available
to Copilot in the workspace.

## 2. Supported Scope

Supported:

- Local Markdown issue tracking.
- GitHub Copilot CLI in non-interactive mode.
- YAML-front-matter configuration in `WORKFLOW.md`.
- Per-issue workspaces under a configured workspace root.
- Workspace lifecycle hooks.
- Polling, dispatch, reconciliation, retry, and structured logs.
- Optional local HTTP status surface.

Not supported:

- Remote issue trackers.
- Protocol-based agent runners.
- Rich multi-tenant UI/control plane.
- A durable scheduler database.

## 3. Components

1. `Workflow Loader`
   - Reads `WORKFLOW.md`.
   - Parses optional YAML front matter and the Markdown prompt body.
2. `Config Layer`
   - Applies defaults, resolves path environment variables, and validates dispatch config.
3. `Local Issue Tracker`
   - Reads Markdown files from state-named folders under `tracker.root`.
   - Normalizes files into issue records.
4. `Orchestrator`
   - Polls, reconciles active runs, applies concurrency limits, and dispatches eligible issues.
5. `Workspace Manager`
   - Creates, reuses, validates, hooks, and removes per-issue workspaces.
6. `Agent Runner`
   - Renders prompts and launches Copilot CLI turns in the issue workspace.
7. `Observability`
   - Emits structured logs and may expose a local HTTP status API.

## 4. Local Issue Tracker

`tracker.kind` MUST be `local`; omitted `tracker.kind` defaults to `local`.

Issues are Markdown files under immediate child directories of `tracker.root`. The child directory
name is the issue state. For example:

```text
issues/
  active/
    fix-button.md
  done/
    update-docs.md
```

Moving a file between child folders changes its state. Terminal issues are used for startup
workspace cleanup.

Issue normalization:

- `id`: absolute Markdown file path.
- `identifier`: file name without extension.
- `title`: first Markdown heading, otherwise the identifier.
- `description`: Markdown body after the first heading.
- `state`: immediate parent directory name.
- `priority`: optional YAML/front-matter value if present.
- `labels`: optional YAML/front-matter list if present, normalized to lowercase.
- `blocked_by`: optional YAML/front-matter list if present.
- `created_at` and `updated_at`: file metadata when available.

## 5. `WORKFLOW.md`

`WORKFLOW.md` is a Markdown file with optional YAML front matter:

```markdown
---
tracker:
  kind: local
  root: ./issues
workspace:
  root: ./workspaces
---

Work on {{ issue.identifier }}: {{ issue.title }}.
```

If front matter is absent, the entire file is the prompt body and defaults apply. If front matter is
present, it MUST be a YAML map.

Top-level config keys:

- `tracker`
- `polling`
- `workspace`
- `hooks`
- `agent`
- `assistant`
- `server`

Unknown keys SHOULD be ignored for forward compatibility.

## 6. Configuration

Configuration is resolved by parsing front matter, applying defaults, resolving supported
environment-variable indirections, and validating typed values.

Core fields:

- `tracker.kind`: string, default `local`, only supported value `local`.
- `tracker.root`: path or `$VAR`, default `./issues` relative to `WORKFLOW.md`.
- `tracker.active_states`: list of strings, default `["active"]`.
- `tracker.terminal_states`: list of strings, default `["done", "closed", "cancelled", "canceled", "duplicate"]`.
- `polling.interval_ms`: positive integer, default `30000`.
- `workspace.root`: path or `$VAR`, default `<system-temp>/symphony_workspaces`.
- `hooks.after_create`: shell script or null.
- `hooks.before_run`: shell script or null.
- `hooks.after_run`: shell script or null.
- `hooks.before_remove`: shell script or null.
- `hooks.timeout_ms`: positive integer, default `60000`.
- `agent.max_concurrent_agents`: positive integer, default `10`.
- `agent.max_turns`: positive integer, default `20`.
- `agent.max_retry_backoff_ms`: positive integer, default `300000`.
- `agent.max_concurrent_agents_by_state`: map of normalized state name to positive integer, default `{}`.
- `assistant.kind`: string, default `copilot`, only supported value `copilot`.
- `assistant.command`: executable, default `copilot`.
- `assistant.arguments`: list of process arguments, default `["-p", "{{ prompt }}", "--allow-all", "--no-ask-user", "--silent"]`.
- `assistant.turn_timeout_ms`: positive integer, default `3600000`.
- `assistant.stall_timeout_ms`: integer, default `300000`; values `<= 0` disable stall detection.
- `server.port`: optional integer port for the local status API.

Path fields expand `~`, resolve `$VAR` values that are exactly environment-variable references, and
normalize relative paths against the directory containing `WORKFLOW.md`.

Dispatch validation MUST reject any tracker kind other than `local` and any assistant kind other than
`copilot`.

## 7. Prompt Rendering

The Markdown body is the per-issue prompt template. Rendering is strict:

- Unknown variables fail the affected run attempt.
- Unknown filters fail the affected run attempt.
- Empty prompt bodies may use a minimal default prompt.

Template variables:

- `issue.id`
- `issue.identifier`
- `issue.title`
- `issue.description`
- `issue.priority`
- `issue.state`
- `issue.branch_name`
- `issue.url`
- `issue.labels`
- `issue.blocked_by`
- `issue.created_at`
- `issue.updated_at`
- `attempt`

## 8. Orchestration

Each poll tick:

1. Reconcile running issues.
2. Validate dispatch config.
3. Fetch local issues in active states.
4. Sort candidates by priority, creation time, and identifier.
5. Dispatch eligible issues while global and per-state slots are available.

An issue is eligible only when:

- It has `id`, `identifier`, `title`, and `state`.
- Its normalized state is active and not terminal.
- It is not already running, claimed, or retry-queued.
- Concurrency limits allow another run.
- For `Todo` state, blockers are terminal.

Worker outcomes:

- Normal exit releases the running entry and may schedule a short continuation retry.
- Failure or timeout schedules exponential-backoff retry.
- Cancellation by reconciliation stops the active run and releases or cleans up according to state.

## 9. Workspace Management

Workspace root is `workspace.root`. Each issue uses:

```text
<workspace.root>/<sanitized issue.identifier>
```

The workspace key replaces characters outside `[A-Za-z0-9._-]` with `_`.

Safety invariants:

- The Copilot CLI process MUST start with `cwd` equal to the issue workspace path.
- The workspace path MUST stay inside `workspace.root`.
- Workspaces are reused across attempts for the same issue.
- Terminal-state startup cleanup removes stale workspaces for terminal issues.

Hooks run in the workspace directory:

- `after_create`: fatal on failure.
- `before_run`: fatal for the current attempt on failure.
- `after_run`: best effort.
- `before_remove`: best effort.

## 10. Copilot CLI Runner

The default runner launches:

```text
copilot -p "{{ prompt }}" --allow-all --no-ask-user --silent
```

`{{ prompt }}` is replaced as one process argument, not shell-concatenated. The configured command
and arguments may override the default, but `assistant.kind` remains `copilot`.

Each turn:

1. Creates or reuses the issue workspace.
2. Runs `before_run`.
3. Starts Copilot CLI in the workspace.
4. Emits `session_started`.
5. Waits for process completion or timeout.
6. Emits `turn_completed` or `turn_failed`.
7. Runs `after_run` best-effort.

The first turn uses the full rendered issue prompt. Continuation turns use short continuation
guidance and do not resend the original task prompt.

## 11. Observability

Implementations SHOULD emit structured logs with:

- Event type.
- Timestamp.
- Issue ID and identifier when applicable.
- Workspace path when applicable.
- Agent process ID when applicable.
- Worker exit reason and error message when applicable.

If a local status API is enabled, it SHOULD expose current running, claimed, retry, and aggregate
runtime state for operator inspection.
