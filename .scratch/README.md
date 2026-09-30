# .scratch — local markdown issue tracker data

This directory is this repo's **issue tracker** (backend: Local Markdown). Nothing here is source code; it is the working data for the engineering skills.

Full convention: [`../docs/agents/issue-tracker.md`](../docs/agents/issue-tracker.md).

## Layout

- `.scratch/<feature-slug>/` — one directory per feature / effort
  - `spec.md` — the spec for that feature
  - `issues/<NN>-<slug>.md` — one file per ticket, numbered from `01`
  - `map.md` — the wayfinder map for that effort (Notes / Decisions so far / Fog)
  - `review/` — evidence gathered while reviewing the reference codebase

## Ticket file format

Each ticket is a standalone markdown file. Fields are read from the first lines of the body:

- `Status:` — triage state (`needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`) or wayfinder claim state (`claimed`, `resolved`)
- `Type:` — wayfinder ticket type: `research` / `prototype` / `grilling` / `task`
- `Blocked by: NN, NN` — ticket numbers that must be `resolved` first
- `Labels:` — comma-separated label names (see [`../docs/agents/triage-labels.md`](../docs/agents/triage-labels.md); colours live in [`../docs/agents/label-colors.json`](../docs/agents/label-colors.json))
- `## Comments` — conversation history, appended at the bottom

## Notes

- Do not hand-edit `docs/agents/label-colors.json` from here; the panel owns that file.
