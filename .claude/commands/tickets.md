---
description: List and triage open tickets
---

Show all open tickets, grouped by status, and offer triage actions.

## Process

1. **Detect mode.**
   - GitHub: `gh --version` works → use `gh issue list`.
   - Local: scan `docs/tickets/{in-progress,backlog,done}/`.

2. **Fetch and group.**

   ### GitHub mode
   ```bash
   gh issue list --state open --limit 50 --json number,title,labels,assignees,createdAt
   ```
   Group by:
   - **In progress** — has `in-progress` label or assigned to current user
   - **Epics** — has `epic` label
   - **Careful-review** — has `careful-review` label (auth/payments/PII/tenant)
   - **Other** (regular task/bug)

   ### Local mode
   - List files in `docs/tickets/in-progress/` (sorted by name)
   - List files in `docs/tickets/backlog/` (sorted by name, top 20)
   - Recent done (top 5) from `docs/tickets/done/`

3. **Print.** Format as a single overview the user can scan in 30 seconds. Include numbers, titles, sizes (if available), and labels.

4. **Offer triage actions:**
   - `/start <n>` — pick up a ticket
   - "Close <n>" — close stale ticket (ask why)
   - "Move <n> to top" — reorder backlog (local mode: rename file with new sequence)
   - "Convert <n> to epic" — promote to parent ticket
   - "Show <n>" — print full ticket body

5. Do nothing destructive without explicit confirmation.

## Rules

- Never close or delete a ticket without confirmation.
- If counts are large (>30 backlog), show top 20 with a "more" hint instead of dumping all.
- If you see stale tickets (>30 days old, no activity), flag them but don't auto-close.
- Highlight `careful-review`-labeled tickets — they need extra attention before merge.
