# 0006. Rolling bug-fix epic synced from the `bug` label

- **Status:** accepted
- **Date:** 2026-05-25
- **Deciders:** Ruben G.

## Context

Bug-fix and latent-issue tickets accumulate in two main ways in this project:

1. **Deferred review findings** — a 5-agent code review flags an issue that's real but out of the current ticket's scope; per CLAUDE.md's three-way triage, it becomes its own follow-up ticket. As of 2026-05-25 there are seven such open tickets (#21, #24, #25, #26, #30, #31, #45) surfaced across reviews of #7, #8, #10, #11, and #22.
2. **Direct discovery** — a Sentry alert, a customer report, or a smoke test that catches behavior the suite missed.

Before this ADR these tickets had no shared home. Some were anchored to the epic that surfaced them (#21 sat as "deferred" under the otherwise-closed launch-hardening epic #1); others (#30, #31, #45) sat free-floating in the open-issues list. The cost: when triaging, the founder had to scan all open issues to identify which were defects vs. feature work vs. test scaffolding, and there was no single place to see "what bugs do we have."

A standard per-feature epic doesn't fit because:

- Bug discovery is rolling and unbounded — a fixed checklist can't be "completed."
- The bugs span every subsystem — auth, extraction, reminders, portal, frontend — so subsystem-epic ownership would fragment the index.
- We want the epic to act as an *index* of work, not as a blocker on other work.

Options considered:

- **Status quo** — leave bugs scattered. Rejected: indexing cost grows with the issue list.
- **A label and a saved search** (`is:open label:bug`) — works but invisible at the epic-overview level, doesn't show historical closed bugs in context, and there's no canonical "epic" issue to link from CLAUDE.md or PR bodies.
- **A rolling epic, manually maintained** — works but the checklist drifts the moment a contributor forgets to edit it. Manual sync defeats the purpose at the first missed event.
- **A rolling epic, auto-synced from the `bug` label via a workflow** — picked.

The simplification epic (#41) is the opposite shape: finite, one-time, gated on other epics merging. We keep that pattern for one-time work and use this new pattern only for the rolling case.

## Decision

Adopt a **rolling epic [#48 "Bug fixes & latent issues"](https://github.com/neboxdev/complidrop/issues/48)** whose ticket checklist is auto-synced from the GitHub `bug` label by `.github/workflows/bugfix-epic-sync.yml`.

Concretely:

- **Join key**: the `bug` label. Any issue carrying `bug` (open or closed) is rendered into the epic body. Removing the label removes the line. Closing the issue flips `- [ ]` to `- [x]`. Re-opening reverts it.
- **Managed block**: the epic body contains a fixed region delimited by `<!-- bugfix-epic-sync:start -->` / `<!-- bugfix-epic-sync:end -->` comment markers. Only that region is regenerated; the Goal / Scope / How sections stay editable by humans.
- **Trigger surface**: the workflow runs on `issues: [opened, closed, reopened, labeled, unlabeled, edited, deleted, transferred]` plus a daily 06:00 UTC cron as a safety net. `workflow_dispatch` allows manual replay.
- **Self-edit guard**: when the triggering issue is #48 itself, the job is skipped (the `if:` clause reads `vars.BUGFIX_EPIC_NUMBER`). This prevents the workflow editing the epic body from re-triggering itself in a loop.
- **Idempotency**: the workflow fetches all `bug`-labeled issues each run, sorts by issue number, and rewrites the block from scratch. A no-op run (no diff) skips `gh issue edit` to avoid spurious audit-log entries.
- **Dual epic membership is permitted**: a `bug` ticket can also be checklisted in another epic (e.g. #21 remains listed in launch-hardening #1 as the "deferred" tail). The rolling epic is a discovery index, not an ownership claim.
- **Configuration**: the epic issue number is held in repo variable `BUGFIX_EPIC_NUMBER` (set via `gh variable set BUGFIX_EPIC_NUMBER --body "48"`). If the epic ever has to move, only the variable changes — the workflow doesn't.
- **Workflow scope**: `permissions: issues: write` is the only token scope needed. The default `GITHUB_TOKEN` is sufficient.

The `bug` label means *"something is incorrect or about to be"*: a defect, a latent fragility (race, TZ assumption, multi-instance assumption), a semantic decision needed to resolve wrong behavior, or a contract ambiguity that produces wrong client behavior. It does NOT mean "general task" — feature work, refactors, test scaffolding, and codebase simplification do NOT get the `bug` label.

The slash commands enforce this:

- `/ticket` instructs cold sessions to apply `bug` when creating a bug-fix ticket and explains what counts.
- `/start` Phase 5 instructs the triage step to apply `bug` to any deferred-to-its-own-ticket finding that meets the definition, so review findings get indexed automatically.
- `/start` Phase 6 notes that `Closes #N` in the PR body causes the close event to tick the box on merge.
- `/breakdown` notes that `bug` is uncommon inside a feature breakdown but the label is the join key when it does apply.

CLAUDE.md links to this ADR and to the epic.

## Consequences

### Positive

- **Single canonical view of open defects** — one issue link in CLAUDE.md, in `/ticket` and `/start` docs, and in this ADR.
- **No manual epic maintenance** — adding `bug` is the only step a contributor takes; the workflow handles the checklist.
- **Closing a ticket via a PR's `Closes #N` automatically ticks the box** — no extra step in `/start` Phase 6.
- **Historical bugs stay visible as checked items** — the rolling list shows "what we've fixed" alongside "what's open", which is useful when triaging recurrence.
- **Per-feature epics are not displaced** — a feature epic like #1 or #33 can still own its own children; this epic only indexes bugs cross-cutting them.
- **One-time epics stay the right pattern for one-time work** — the simplification epic #41 keeps its finite-checklist shape; this ADR only changes how the rolling case is structured.

### Negative

- **Unbounded growth** — the checklist grows by one line per `bug` ticket forever. For a small SaaS this is a non-issue (years before pagination matters); if it does become unwieldy, the workflow's render step can be extended to group by year/quarter or to only show recent N closed.
- **`gh` CLI dependency at workflow runtime** — already pre-installed on GitHub-hosted Ubuntu runners, but the workflow would break on a self-hosted runner without `gh`.
- **Workflow self-trigger risk** — guarded by the `if:` clause that skips when the event issue IS the epic. If the variable is ever wrong, the workflow could loop until rate-limited; the daily cron is intentionally a separate trigger that won't loop.
- **Label discipline matters** — a contributor who applies `bug` to a feature ticket pollutes the epic. The slash commands document the rule, but it's a soft enforcement (no required-label CI check).
- **Workflow only runs from the default branch** — until the workflow file lands on `main`, the workflow can't run. The initial epic body was populated manually for this reason; subsequent sync is automatic.

## Compatibility & rollback

The workflow is purely additive: it edits one specific issue's body and does nothing else. To roll back, delete `.github/workflows/bugfix-epic-sync.yml` and the `BUGFIX_EPIC_NUMBER` variable; the epic body becomes static (last-rendered state) and contributors edit it by hand or close it.

## References

- Workflow: `.github/workflows/bugfix-epic-sync.yml`
- Epic: [#48 Bug fixes & latent issues](https://github.com/neboxdev/complidrop/issues/48)
- Related epics for context: #1 (one-time backend hardening), #33 (one-time frontend test hardening), #41 (one-time simplification).
- CLAUDE.md → "Rolling bug-fix epic (#48)" section.
