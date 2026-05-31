---
tracker:
  kind: local
  root: ./issues
workspace:
  root: ./workspaces
assistant:
  kind: copilot
---

You are working on {{ issue.identifier }}.

Title: {{ issue.title }}

Body:
{{ issue.description }}

Use the issue markdown file as the source of truth. When the work is ready for review, move the
issue file out of `issues/active/` into the workflow state folder that best represents the result.
