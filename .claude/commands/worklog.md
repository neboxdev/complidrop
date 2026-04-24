---
description: Append a free-form narrative entry to WORKLOG.md
argument-hint: <optional one-line summary>
---

Add a worklog entry capturing the current session's narrative — what shipped, what's stuck, what's next.

## Process

1. Read tail of `WORKLOG.md` to see the format used so far.
2. Read `git log --oneline -10` and `git status --short` for context.
3. Draft an entry with this structure:

```markdown
## YYYY-MM-DD — <one-line summary or $ARGUMENTS>

**Done:**
- ...

**In progress:**
- ...

**Stuck / questions:**
- ...

**Next:**
- ...
```

4. Show the draft. If the user has anything to add or correct, fold it in.
5. Append to `WORKLOG.md`.

## Rules

- This is the human thread, not a commit log. Capture decisions, frustration, half-formed ideas — not what `git log` already shows.
- Keep entries short. If it's longer than 20 lines, you're probably writing an ADR or a ticket instead.
- Never overwrite existing entries — append only.
