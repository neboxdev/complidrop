---
description: Run the 5-agent code review on the current branch
---

Run the multi-agent review from Phase 4 of `/start` against the current branch's diff vs main. **Diagnostic only — do NOT auto-fix.**

## Process

1. Compute the diff: `git diff origin/main...HEAD`. If the branch has no commits ahead of main, stop.
2. Spawn the 5 code-reviewer subagents **in parallel** via the Task tool:
   - `security-reviewer`
   - `correctness-reviewer`
   - `performance-reviewer`
   - `test-quality-reviewer`
   - `architecture-reviewer`
3. Each returns findings using the schema in `/start` Phase 4 (bug vs suggestion).
4. De-duplicate, classify, and report to the user grouped by reviewer (most severe bugs first).
5. Save the full report to `.claude/reviews/<branch>-<YYYYMMDD-HHMM>.md`.

## Output to the user

Surface a summary like:

```
Reviewer summary:
- Security: 2 bugs (1 blocker, 1 major) / 3 suggestions
- Correctness: 0 bugs / 1 suggestion
- ...

Top bugs:
1. [security/blocker] api/CompliDrop.Api/Endpoints/Documents.cs:84 — IgnoreQueryFilters bypasses tenant filter on list endpoint. Fix: remove the IgnoreQueryFilters call and verify tenant scoping is enforced via AppDbContext.CurrentOrgId.
...

Full report saved to .claude/reviews/<branch>-<timestamp>.md.
```

## Rules

- Do NOT modify any files. This command is read-only.
- If user wants fixes applied, they should re-run via `/start <ticket>` or ask explicitly.
