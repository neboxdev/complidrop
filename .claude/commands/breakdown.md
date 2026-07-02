---
description: Break an approved spec into ordered, well-structured tickets
---

Take the most recently approved spec (output of `/plan`) and turn it into tickets.

## Decision: simple or complex?

If the spec has more than 5 user stories OR touches more than 3 distinct subsystems (API + DB + frontend + auth + emails + workers), pause and propose splitting it into two or more sequential epics — the first must ship user-visible value on its own. For an unusually large or architecturally tangled spec, also offer `/ultraplan` (cloud planning, research preview in recent Claude Code versions): the user runs it, then pastes the resulting plan back here to inform the breakdown. Confirm the approach with the user before creating any tickets.

Otherwise proceed directly.

## Produce the tickets

Structure:

1. **One "epic" parent ticket** for the feature
2. **5–12 child tickets** that together implement the epic
3. Each child explicitly references dependencies ("depends on #51")
4. Tickets are ordered — the list shows implementation sequence

Each ticket body:

```markdown
## Goal
One sentence. End state.

## Why
Motivation. What unlocks?

## Context a fresh session needs
- Exact file paths that will be modified or referenced
- Existing patterns to follow (with file paths) — e.g., follow the pattern in `api/CompliDrop.Api/Endpoints/Documents.cs` for tenant-scoped endpoints
- Related ADRs in `docs/adr/`
- External API references with URLs
- Dependencies on other tickets in this epic (e.g., depends on #51)

## Acceptance criteria
- [ ] Testable outcome 1
- [ ] Testable outcome 2
- [ ] Error and edge cases handled

## Non-goals
- Deliberately out of scope items

## Size
S (< 1h) / M (1–3h) / L (1 session) / XL (needs splitting)
```

## Creation

Detect mode at runtime: if `gh --version` works AND `gh auth status` succeeds, use GitHub mode. Otherwise local mode.

### GitHub mode

1. Create the epic first:
   `gh issue create -t "[epic] <title>" -F <tempfile> -l "epic,task" -a @me`

2. For each child ticket:
   `gh issue create -t "[task] <title>" -F <tempfile> -l "task,<extras>" -a @me`

3. After all are created, edit the epic body with a checklist:
   `gh issue edit <epic-number> -F <updated-epic-tempfile>`

4. Epic body ends with:
   ```markdown
   ## Tickets

   - [ ] #51 First ticket title
   - [ ] #52 Second ticket title
   ```

5. **Mark tickets that touch sensitive areas** with the `careful-review` label. Sensitive areas in CompliDrop:
   - Auth (`Endpoints/Auth*`, JWT, BCrypt, lockout logic)
   - Stripe (checkout, webhook, subscription state)
   - Multi-tenant filter (`AppDbContext.CurrentOrgId`, anything using `IgnoreQueryFilters`)
   - Vendor portal (`/api/portal/*` — public endpoints)
   - Document storage / Azure Blob access
   - Audit log (`AuditSaveChangesInterceptor`)
   - PII handling (extraction, exports)

6. **Mark bug-fix tickets** with the `bug` label so they auto-index in the rolling bug-fix epic [#48](https://github.com/neboxdev/complidrop/issues/48) (via `.github/workflows/bugfix-epic-sync.yml`). Inside a feature breakdown, most children are tasks — `bug` is uncommon here and usually only applies if the spec exists to fix a defect. See CLAUDE.md → "Rolling bug-fix epic (#48)" and [ADR 0006](../../docs/adr/0006-rolling-bug-fix-epic.md).

### Local mode

Create files under `docs/tickets/backlog/` named `YYYY-MM-DD-NNN-kebab-title.md`. Epic file named `YYYY-MM-DD-NNN-epic-<title>.md` listing child ticket paths. Use `docs/tickets/TEMPLATE.md` as a starting point.

`NNN` = next sequence (look at all files in `docs/tickets/{backlog,in-progress,done}/`, take max + 1).

## After creation

Show the list (numbers and titles). Ask:

> "Tickets created. Want me to start with the first one now (`/start <n>`) or leave them in the backlog?"

## Rules

- **Be ruthless about ticket quality.** If you can't write clear acceptance criteria, break it down further or mark as "discovery spike".
- **Never use placeholders like `<TBD>`** in ticket bodies. If info is missing, ask before creating.
- **XL = too big.** Split it.
- **Order matters** — list tickets in the order they should be implemented (DB schema first, then API, then frontend, etc.).
