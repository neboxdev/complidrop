---
description: Start work on a ticket — implement, test, review (fix every bug), then PR
argument-hint: <issue number or local ticket NNN>
---

Pick up ticket **$ARGUMENTS** and implement it with the full quality gate.

## Phase 1: Setup

1. **Fetch the ticket.**
   - GitHub: `gh issue view $ARGUMENTS --json number,title,body,labels,comments,state`. On Windows, never parse gh stdout directly — non-ASCII (em dashes, arrows, accents) becomes `?`. Redirect to a file from the Bash tool (`gh ... > .claude/tmp/ticket-$ARGUMENTS.json`) and Read that file.
   - Local: find `docs/tickets/backlog/*-$ARGUMENTS-*.md` or `docs/tickets/in-progress/*-$ARGUMENTS-*.md`
   - If closed or missing, stop.

2. **Move to in-progress.**
   - GitHub: `gh issue edit $ARGUMENTS --add-assignee @me --add-label in-progress`
   - Local: move file to `docs/tickets/in-progress/` and update `status` frontmatter

3. **Create the feature branch.**
   Infer type from labels (`task` → `feat`, `bug` → `fix`, `chore` → `chore`):
   `git checkout -b <type>/<n>-<kebab-slug>`

   If another Claude session may be active on this checkout, work in a dedicated worktree instead (`git worktree add ../complidrop-<n> -b <branch>`) — parallel sessions share branch state and race each other. Verify `git branch --show-current` before every commit.

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
   - **Frontend**: under `frontend/` with the existing runner and patterns — colocated `*.test.tsx` next to pages/components, shared suites in `frontend/src/test/` (e.g. `forms.test.tsx` pins label wire-up for every form — add an entry when introducing a form).

3. **Run the tests.** They MUST pass before moving on.
   - API: `cd api && dotnet test CompliDrop.Api.Tests/CompliDrop.Api.Tests.csproj`
   - Frontend: `cd frontend && npm test` (or whatever the project uses)

4. **Check coverage of acceptance criteria.** Every `- [ ]` needs at least one test that would fail if the criterion regressed.

## Phase 4: Multi-agent code review (verified)

Read `.claude/commands/review.md` and invoke the **Workflow tool** with the script embedded there, passing this ticket's body via `args: { context: "<ticket body>" }`. The workflow spawns the 5 reviewer personas from `.claude/agents/` in parallel (security, correctness, performance, test-quality, architecture — all pinned to `model: opus`), then adversarially verifies every `bug` finding with 2 independent verifiers.

Reviewers and verifiers are **read-only** — they report findings, never modify the tree. If one claims it "changed" or "tested" something by editing files, distrust it and verify against `git status` before acting.

Findings come back in four buckets:

- **confirmed** — survived both verifiers. Real bugs; all get fixed (Phase 5).
- **plausible** — verifiers split (or one died). Read the code yourself and decide bug vs not; record your call in the PR body.
- **refuted** — both verifiers disproved it, reasoning attached. No fix unless your own reading disagrees; listed in the PR body so nothing is silently dropped.
- **suggestions** — unverified by design (taste, not truth); triaged in Phase 5.

**Definitions (be strict):**
- **bug** = code is actually wrong. Fails now, will fail later, or is logically incorrect. Size and severity are independent — a one-line null-check omission can be a bug.
- **suggestion** = reviewer would do it differently but current code isn't wrong. Style, personal preference, "nicer" rewrites.

Severity orders the fixing (blockers first), but does NOT decide whether to fix.

## Phase 5: Triage and fix

1. Collect the workflow's buckets (confirmed / plausible / refuted / suggestions).
2. De-duplicate overlapping findings.
3. **Fix every CONFIRMED bug**, regardless of severity. Blockers first, then majors, then minors. For each PLAUSIBLE finding, investigate personally: real → fix it like a confirmed bug; not real → move it to the refuted list with your reasoning.
4. Re-run tests after fixes. They must still pass.
5. After all bugs are fixed, re-run the review workflow once to verify nothing new surfaced from the fixes (a clean diff verifies cheaply — verification only fires on bug findings).
6. **Triage every `kind: suggestion` finding — strong default is implement in this PR.** Target: **≥99% of suggestions land in the same session.** "Listed in the PR body but not fixed" is NOT a valid outcome:
   - **Implement in this PR** (overwhelming default). Polish, missing test edges, small refactors, ADRs, mid-sized cleanups — all of it. Even when the change is non-trivial, absorb it here unless one of the two exceptions below applies. Commit as `fix(scope): address review findings (#N)` alongside the bug fixes.
   - **Defer to a follow-up ticket** — **rare; permitted only when ONE of these holds:**
     1. **Strictly necessary.** The suggestion changes data semantics or a public API contract, needs its own ADR / spec conversation, or the reviewer explicitly flagged it as deferred-to-later (e.g. "MVP no-op", "don't introduce prophylactically"). "It's a bigger refactor" alone is NOT sufficient — absorb it.
     2. **5-hour Claude session budget is approaching its cap** with the bug-fix queue and core suggestions not yet drained. In that case defer the smallest still-pending suggestions last; **never defer a `bug`-kind finding** on this exception — always finish the bugs.
     File a real GitHub issue via `gh issue create --title "[task|bug] …" --label "task,…" --body-file .claude/ticket-*.md`. **Never `mcp__ccd_session__spawn_task`** — chips bypass the bug-fix epic and the team backlog. Copy the reviewer's reasoning verbatim. List the new ticket id(s) AND the deferral reason (necessity vs. budget) in the PR body. **If the deferred finding is a bug or latent issue** (defect, race, TZ/multi-instance assumption, contract ambiguity producing wrong client behavior), apply the `bug` label — the rolling bug-fix epic [#48](https://github.com/neboxdev/complidrop/issues/48) auto-syncs from that label via `.github/workflows/bugfix-epic-sync.yml`.
   - **Discard** — only when the suggestion contradicts a project rule (CLAUDE.md, an existing ADR, the ticket's Non-goals). List discards in the PR body with the rule cited.
   When unsure between implement and defer, **implement**.
7. If any bug fix requires a design change contradicting the ticket, stop and ask the user — do not silently diverge.

## Phase 6: PR

```bash
git push -u origin HEAD
gh pr create -t "<conventional commit title> (#$ARGUMENTS)" -F <pr-body-tempfile>
```

The PR title/body should include `Closes #$ARGUMENTS` so the ticket auto-closes on merge. If the ticket carries the `bug` label, the close event triggers `bugfix-epic-sync.yml` which ticks the box in the rolling bug-fix epic [#48](https://github.com/neboxdev/complidrop/issues/48) — no manual epic edit needed.

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

### Findings refuted by verification (N total)
- [correctness] <file:line> — <claimed issue>; refuted: <verifier reasoning, one line>
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
- **Never open a PR with an unfixed confirmed bug finding** (or a plausible one you validated as real). Ever. Regardless of severity.
- **If reviewers disagree**, use your judgment and explain in the PR why you chose one side.
- Save the full review report to `.claude/reviews/<ticket>.md` for deep dives — keep PR body summary-level.
- Local mode (no `gh`): instead of opening a PR, push the branch and tell the user the branch name + a summary of what's ready to merge.
