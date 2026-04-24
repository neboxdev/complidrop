---
description: Create a single well-structured ticket
argument-hint: <short description>
---

Create a single ticket for: **$ARGUMENTS**

## Step 1: Gather context

1. Read recent commits: `git log --oneline -20`
2. Search the codebase with Grep for related keywords from the description
3. Check for duplicates:
   - GitHub: `gh issue list --search "<keywords>" --state all`
   - Local: scan `docs/tickets/{backlog,in-progress,done}/`
4. Read `docs/adr/` for relevant past architectural decisions
5. Read `CLAUDE.md` for project conventions

If you find a likely duplicate, ask the user whether to update it instead of creating a new one.

## Step 2: Draft the ticket body

Use this structure exactly:

```markdown
## Goal
One sentence. End state when complete.

## Why
Motivation. What outcome does this unlock?

## Context a fresh session needs
- Exact file paths that will be modified or referenced
- Existing patterns to follow (with file paths)
- Related ADRs in `docs/adr/`
- External API references with URLs
- Dependencies on other tickets (e.g., depends on #51)

## Acceptance criteria
- [ ] Testable outcome 1
- [ ] Testable outcome 2
- [ ] Error and edge cases handled

## Non-goals
- Deliberately out of scope items

## Size
S (< 1h) / M (1–3h) / L (1 session) / XL (needs splitting)
```

**Never use `<TBD>` placeholders.** If something is missing, ask the user before creating.

If size is XL, ask the user whether to split into multiple tickets first (or run `/breakdown` instead).

## Step 3: Create

Auto-detect mode: `gh --version` works AND `gh auth status` succeeds → GitHub mode. Otherwise local.

### GitHub mode

```bash
gh issue create -t "[task] <title>" -F <tempfile> -l "task" -a @me
```

Add `careful-review` label if the work touches: auth, payments (Stripe), PII (document storage, audit log, vendor portal), multi-tenant filter, or compliance documents themselves.

For bugs: title prefix `[bug]`, label `bug`.

### Local mode

Create `docs/tickets/backlog/YYYY-MM-DD-NNN-kebab-title.md`:

- `YYYY-MM-DD` = today (use `git log -1 --format=%cd --date=short` or system date)
- `NNN` = next sequence (look at all files in `docs/tickets/{backlog,in-progress,done}/`, max + 1, zero-pad to 3 digits)
- File body = the ticket body above, prefixed with frontmatter:

```markdown
---
title: <kebab-title>
status: backlog
type: task | bug | epic
size: S | M | L | XL
created: YYYY-MM-DD
---

<body>
```

## Step 4: Confirm

Show the user the ticket number / file path, then ask:

> "Ticket created. Want to start work on it now (`/start <n>`) or leave it in the backlog?"

## Rules

- Never create a ticket with placeholder content. Ask if uncertain.
- Acceptance criteria must be testable — "code is clean" is not testable.
- Reference exact file paths in Context. A cold session must be able to find them without guessing.
