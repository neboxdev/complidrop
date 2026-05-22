---
description: Start work on a ticket — implement, test, review (fix every bug), then PR
argument-hint: <issue number or local ticket NNN>
---

Pick up ticket **$ARGUMENTS** and implement it with the full quality gate.

## Phase 1: Setup

1. **Fetch the ticket.**
   - GitHub: `gh issue view $ARGUMENTS --json number,title,body,labels,comments,state`
   - Local: find `docs/tickets/backlog/*-$ARGUMENTS-*.md` or `docs/tickets/in-progress/*-$ARGUMENTS-*.md`
   - If closed or missing, stop.

2. **Move to in-progress.**
   - GitHub: `gh issue edit $ARGUMENTS --add-assignee @me --add-label in-progress`
   - Local: move file to `docs/tickets/in-progress/` and update `status` frontmatter

3. **Create the feature branch.**
   Infer type from labels (`task` → `feat`, `bug` → `fix`, `chore` → `chore`):
   `git checkout -b <type>/<n>-<kebab-slug>`

4. **Read the ticket as your session prompt.** Treat Goal / Why / Context / Acceptance Criteria as authoritative.

5. **Load context files.** Read every file path mentioned in the Context section, plus `CLAUDE.md` and any referenced ADRs.

6. **Summarize and confirm.** One short paragraph:
   - What you're going to do
   - Acceptance criteria you'll meet
   - First 3 concrete steps (e.g., "1. Add `RemindersOptOut` column to `Document` entity, 2. Create migration, 3. Wire endpoint")

   Ask "ready to proceed?" **Do not change anything until confirmed.**

## Phase 2: Implementation

Implement the ticket. Make small focused commits with conventional format:
`feat(scope): description (#$ARGUMENTS)`

Commit scopes for CompliDrop: `extraction`, `reminders`, `portal`, `auth`, `billing`, `audit`, `frontend`, `api`, `db`, `worker`, `docs`, `ci`.

Check off acceptance criteria in the ticket body as you complete them.

If you discover a needed change that wasn't in the ticket, **stop and update the ticket first** — don't silently expand scope.

## Phase 3: Tests (MANDATORY)

After feature code compiles and runs:

1. **Identify what needs testing:**
   - Every new public function / endpoint / command / background worker tick
   - Every acceptance criterion
   - Edge cases: nulls, empty inputs, errors, boundaries, concurrent requests
   - Integration points: Postgres (via `WebApplicationFactory`), Azure Blob, Document AI, Gemini, Stripe webhook signature verification, Resend

2. **Write tests.**
   - **API**: `api/CompliDrop.Api.Tests/` (xUnit + `WebApplicationFactory`). Use a real test Postgres (Testcontainers if present, otherwise the project's existing test DB pattern). Mock external HTTP boundaries (Document AI, Gemini, Resend, Stripe) at the HttpMessageHandler level.
   - **Frontend**: under `frontend/` with the project's test runner (Jest or Vitest). If no test infra exists yet for frontend, set up the minimum and note in the PR.

3. **Run the tests.** They MUST pass before moving on.
   - API: `cd api && dotnet test CompliDrop.Api.Tests/CompliDrop.Api.Tests.csproj`
   - Frontend: `cd frontend && npm test` (or whatever the project uses)

4. **Check coverage of acceptance criteria.** Every `- [ ]` needs at least one test that would fail if the criterion regressed.

## Phase 4: Multi-agent code review

Spawn 5 review subagents **in parallel** via the Task tool. Each is defined in `.claude/agents/`:

1. `security-reviewer` — injection, authn/authz, secrets, input validation, PII leakage, multi-tenant filter bypass
2. `correctness-reviewer` — edge cases, nullability, error paths, off-by-one, race conditions, async/await pitfalls
3. `performance-reviewer` — N+1 queries, missing indexes, unbounded memory, blocking I/O
4. `test-quality-reviewer` — test comprehensiveness, mocking correctness
5. `architecture-reviewer` — fits existing patterns, no duplicated abstractions, no ADR contradictions

Give each the diff (`git diff origin/main...HEAD`) and the ticket body.

Each returns findings classified as **either "bug" or "suggestion"**:

```json
{
  "findings": [
    {
      "kind": "bug" | "suggestion",
      "severity": "blocker" | "major" | "minor",
      "file": "api/CompliDrop.Api/Endpoints/Foo.cs",
      "line": 42,
      "issue": "Short description",
      "fix": "How to fix"
    }
  ]
}
```

**Definitions (be strict):**
- **bug** = code is actually wrong. Fails now, will fail later, or is logically incorrect. Size and severity are independent — a one-line null-check omission can be a bug.
- **suggestion** = reviewer would do it differently but current code isn't wrong. Style, personal preference, "nicer" rewrites.

Severity orders the fixing (blockers first), but does NOT decide whether to fix.

## Phase 5: Triage and fix

1. Collect all findings from 5 reviewers.
2. De-duplicate overlapping findings.
3. **Fix every `kind: bug` finding**, regardless of severity. Blockers first, then majors, then minors.
4. Re-run tests after fixes. They must still pass.
5. After all bugs fixed, re-run the affected reviewers once more to verify nothing new surfaced from the fixes.
6. **Triage every `kind: suggestion` finding three ways** — "listed in the PR body but not fixed" is NOT a valid outcome:
   - **Implement in this PR** (default). Polish, missing test edges, small refactors, ADRs. Commit them as a `fix(scope): address review findings (#N)` commit alongside the bug fixes.
   - **Defer to a follow-up ticket** — only when the suggestion expands scope, changes data semantics, or contradicts the reviewer's own caveat (e.g. "MVP no-op", "don't introduce prophylactically"). Use `mcp__ccd_session__spawn_task` (or `gh issue create`) with the reviewer's reasoning copied verbatim. List the new ticket id(s) in the PR body.
   - **Discard** — only when the suggestion contradicts a project rule (CLAUDE.md, an existing ADR, the ticket's Non-goals). List discards in the PR body with the rule cited.
   When unsure between implement and defer, default to implement if the change is <30 lines.
7. If any bug fix requires a design change contradicting the ticket, stop and ask the user — do not silently diverge.

## Phase 6: PR

```bash
git push -u origin HEAD
gh pr create -t "<conventional commit title> (#$ARGUMENTS)" -F <pr-body-tempfile>
```

PR body structure:

```markdown
## What changed
<one sentence>

## Why
Closes #$ARGUMENTS

## Acceptance criteria
- [x] ... (copy from ticket, checked)

## Tests added
- Unit: <list>
- Integration: <list>

## Review report

### Bugs fixed (N total)
- [security] <file:line> — <what>, fix: <what was done>
- [correctness] <file:line> — <what>, fix: <what was done>
- ...

### Suggestions implemented (N total)
- [architecture] <file:line> — <what>, applied: <what was done>
- ...

### Suggestions deferred to follow-up tickets (N total)
- [correctness] <what>, ticket #N — reason: <expands scope / changes data semantics / reviewer's own caveat>
- ...

### Suggestions discarded (N total)
- [architecture] <what>, reason: <which project rule it contradicts>
- ...

### Reviewer summary
- Security: N bugs / M suggestions
- Correctness: N bugs / M suggestions
- Performance: N bugs / M suggestions
- Test quality: N bugs / M suggestions
- Architecture: N bugs / M suggestions
```

Ask the user: "PR opened at <url>. Ready to merge, or review the diff first?"

## Rules

- **Never open a PR with failing tests.** Ever.
- **Never open a PR with an unfixed bug finding.** Ever. Regardless of severity.
- **If reviewers disagree**, use your judgment and explain in the PR why you chose one side.
- Save the full review report to `.claude/reviews/<ticket>.md` for deep dives — keep PR body summary-level.
- Local mode (no `gh`): instead of opening a PR, push the branch and tell the user the branch name + a summary of what's ready to merge.
