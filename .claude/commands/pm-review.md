---
description: Run the 6 PM reviewers on a spec or proposal document
---

Run Phase 2 and Phase 3 of `/plan` against the spec most recently discussed in this conversation (or a file path provided by the user).

Useful when:
- A spec was written outside Claude Code and you want a sanity check
- You want to re-review after significant changes
- Onboarding someone else's proposal

Reviewers (in `.claude/agents/`):
1. `pm-scope-reviewer`
2. `pm-user-empathy-reviewer`
3. `pm-business-reviewer`
4. `pm-risk-reviewer`
5. `pm-simplicity-reviewer`
6. `legal-compliance-reviewer`

Spawn them in parallel via the Task tool. Collect, de-duplicate, present grouped by reviewer (most severe first). User triages each concern as **address / defer / reject**.

Do not modify the spec without explicit user instruction during triage.
