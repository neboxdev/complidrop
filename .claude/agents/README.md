# Agent personas

Each `.md` file in this directory defines a reviewer persona that Claude Code can spawn as a subagent — via the Task tool, or via the `/review` Workflow script (which resolves personas through `agentType`).

## Existing personas

### Code reviewers (used by `/start`, `/review`, `/epic-review`)
- `security-reviewer` — security bugs (multi-tenant leakage, injection, secrets, auth, file uploads)
- `correctness-reviewer` — logic bugs, edge cases, async/await pitfalls, EF Core gotchas
- `performance-reviewer` — N+1, indexes, blocking I/O, paid AI-call caching
- `test-quality-reviewer` — test coverage and quality, mocking correctness
- `architecture-reviewer` — fits existing patterns, ADR adherence, layer responsibilities

### PM reviewers (used by `/plan`, `/pm-review`)
- `pm-scope-reviewer` — scope and MVP framing
- `pm-user-empathy-reviewer` — UX / discoverability for non-technical SMB users
- `pm-business-reviewer` — business reasoning at $49/mo SMB scale
- `pm-risk-reviewer` — operational/market/technical risk
- `pm-simplicity-reviewer` — build vs buy vs skip
- `legal-compliance-reviewer` — privacy, US regulatory (HIPAA/SOC 2/state laws), AI-processing concerns (CompliDrop-specific)

## Adding a new persona

Create a new `.md` file with frontmatter:

```markdown
---
name: my-new-reviewer
description: What this reviewer focuses on
tools: Read, Grep, Glob, Bash
model: opus
---

You are a <role>. Your job is <focus>.

Focus on:
- <specific concerns>

<inline JSON findings schema — the agent must be self-contained>
```

Then reference it in the relevant slash command (edit `.claude/commands/*.md` to include the new reviewer name in its subagent list).

## Conventions (all personas)

- **Self-contained prompts.** Subagents do NOT see `CLAUDE.md`, the slash command that spawned them, or the other agent files. Never write "per the schema in X" — embed the schema and every needed definition inline, and instruct the agent to read `CLAUDE.md` / `docs/adr/` itself when it needs project invariants.
- **Read-only.** Reviewers report findings; they never edit files or run builds/tests. The `tools:` line (code reviewers: `Read, Grep, Glob, Bash`; PM reviewers: `Read, Grep, Glob, WebSearch, WebFetch`) removes Edit/Write; since Bash could still mutate, the prompt states the rule explicitly too. This exists because parallel reviewers editing the tree produced false "regression" findings in the past.
- **`model: opus`** — every persona is pinned to Opus (founder decision 2026-07-02): routine reviews must not burn premium-tier (Fable) session tokens; Fable-level review happens only as an explicit one-off request. The `/review` workflow also passes `model: 'opus'` on every agent call, including the ad-hoc adversarial verifiers.
- **Volatile facts go stale.** Rate limits, index shapes, and TTL semantics drift as the product evolves (three of them had drifted in these prompts by 2026-07). Hardcode a number only when it earns its keep for fast pattern-matching, and expect to update it when the pattern changes; otherwise point at `CLAUDE.md` § Core patterns as the source of truth.

## When to add a new persona

When you realize a whole class of issue keeps getting missed by existing reviewers. Examples you might add later:
- `accessibility-reviewer` — WCAG/a11y compliance for the customer dashboard and vendor portal
- `api-contract-reviewer` — breaking change detection for any public API
- `observability-reviewer` — logging, metrics, tracing coverage (Sentry, PostHog)
- `cost-reviewer` — paid-API call accounting (Document AI, Gemini, Resend, Stripe events)

Don't proliferate — 5–7 per review pass is the sweet spot. Beyond that, you get noise and duplicate findings. Rotate or retire personas instead of accumulating.

## Tuning

Edit any `.md` here directly. Reviewer prompts are project-specific and should evolve as CompliDrop's risk surface and architectural patterns evolve.
