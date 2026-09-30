# Domain Docs

How the engineering skills should consume this repo's domain documentation when exploring the codebase.

## Before exploring, read these

- **`CONTEXT-MAP.md`** at the repo root — it points at one `CONTEXT.md` per bounded context. Read each one relevant to the topic.
- **`docs/adr/`** — system-wide decisions. Also check `src/Services/<Context>/docs/adr/` for context-scoped decisions.

If any of these files don't exist, **proceed silently**. Don't flag their absence; don't suggest creating them upfront. The `/domain-modeling` skill creates them lazily when terms or decisions actually get resolved.

## File structure

This repo is multi-context (its `CONTEXT-MAP.md` at the root is what makes it so):

```
/
├── CONTEXT-MAP.md
├── docs/adr/                          ← system-wide decisions
└── src/
    └── Services/
        ├── Identity/
        │   ├── CONTEXT.md
        │   └── docs/adr/              ← context-specific decisions
        └── Scheduling/
            ├── CONTEXT.md
            └── docs/adr/
```

## Use the glossary's vocabulary

When your output names a domain concept (in an issue title, a refactor proposal, a hypothesis, a test name), use the term as defined in the relevant `CONTEXT.md`. Don't drift to synonyms the glossary explicitly avoids.

If the concept you need isn't in a glossary yet, that's a signal — either you're inventing language the project doesn't use (reconsider) or there's a real gap (note it for `/domain-modeling`).

Terms that appear in more than one context are a **false friend** warning: the same word may mean different things in Identity and in Auditing. Check the map before assuming.

## Flag ADR conflicts

If your output contradicts an existing ADR, surface it explicitly rather than silently overriding:

> _Contradicts ADR-0003 (YARP edge owns authentication) — but worth reopening because…_
