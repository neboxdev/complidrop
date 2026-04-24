---
description: Scaffold a new ADR (Architecture Decision Record)
argument-hint: <decision title>
---

Create a new ADR documenting an architectural decision: **$ARGUMENTS**

## Process

1. Read `docs/adr/template.md` and `docs/adr/README.md`.
2. List existing ADRs in `docs/adr/` to find the next number (`NNNN` format, zero-padded to 4 digits).
3. **Interview the user** before drafting:
   - What's the problem you're trying to solve?
   - What options did you consider?
   - Why did you pick this one?
   - What are you trading away?
4. Create `docs/adr/<NNNN>-<kebab-title>.md` from the template, filling in:
   - Title
   - Status: `proposed` (becomes `accepted` after discussion)
   - Date: today (YYYY-MM-DD)
   - Context (why this decision is needed)
   - Decision (what was decided)
   - Consequences (positive, negative, neutral)
   - Alternatives considered (if relevant)
5. Show the draft ADR. Ask the user to confirm or edit.
6. After confirmed, append a one-line entry to `docs/adr/README.md` index.

## Rules

- ADRs are for **architectural** decisions, not coding-style nitpicks. If the decision wouldn't affect a future engineer reading the codebase 6 months from now, it doesn't need an ADR.
- Status transitions: `proposed` → `accepted` | `rejected`. Once `accepted`, only supersede via a new ADR (`status: superseded by NNNN`).
- Never delete or rewrite history of an accepted ADR — supersede instead.
- For CompliDrop, decisions worth recording include: extraction-pipeline provider switches, schema changes affecting tenant isolation, auth-model changes, payment-tier model, retention-policy decisions, AI-prompt versioning strategy.
