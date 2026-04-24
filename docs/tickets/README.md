# Local tickets

When the `gh` CLI is unavailable, tickets live as markdown files here:

- `backlog/` — not yet started
- `in-progress/` — actively being worked on (one branch per ticket)
- `done/` — completed (move file here after merge)

File naming: `YYYY-MM-DD-NNN-kebab-title.md` where `NNN` is a zero-padded sequence number.

Use `/ticket <description>` to create. Use `/start <n>` to begin work. Use `/tickets` to list.

When `gh` becomes available, the slash commands auto-detect and switch to GitHub Issues — local tickets remain valid history but new ones go to GitHub.

See [TEMPLATE.md](TEMPLATE.md) for the ticket body structure.
