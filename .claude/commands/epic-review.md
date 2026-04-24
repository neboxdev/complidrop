---
description: Run a consolidated review across all tickets in an epic
argument-hint: <epic issue number>
---

Run a codebase-wide review after all tickets in epic #$ARGUMENTS have been merged.

## Process

1. Fetch the epic: `gh issue view $ARGUMENTS --json body,state` (or read from `docs/tickets/done/`). Parse the checklist of child tickets.
2. Verify all children are closed/merged. List any still open and stop if so.
3. Compute the combined diff: `git diff <epic-start-sha>...HEAD`. The epic-start-sha is the commit immediately before the first merged child ticket — find via `git log` if not provided.
4. Spawn 5 code-reviewer subagents with **epic-scope prompts**:
   - **Integration issues** across tickets (do they compose correctly? do the new endpoints play nicely with the new background worker?)
   - **Regressions** in adjacent code (did anything outside the epic break?)
   - **Feature coherence** (does the sum deliver the epic's goal?)
   - **Documentation drift** (ADRs, README, CLAUDE.md out of date with the new feature?)
   - **Test gaps at feature level** (integration tests across the full flow — e.g., end-to-end document upload → extraction → reminder → vendor portal export)
5. Triage findings. **Bugs get fixed**, suggestions go in the report.
6. Write to `.claude/reviews/epic-$ARGUMENTS.md` and summarize.
7. For each remaining bug, offer to create a follow-up ticket via `/ticket`.

## Rules

- Broader review than per-ticket. Focus on emergent integration issues.
- Same bug-vs-suggestion rule: fix every bug, regardless of severity.
- Keep findings actionable.
- If documentation is stale (CLAUDE.md, ADRs), update it as part of the epic-review pass.
