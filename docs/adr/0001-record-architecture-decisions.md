# 0001. Record architecture decisions

- **Status:** accepted
- **Date:** 2026-04-24
- **Deciders:** Ruben G.

## Context

CompliDrop is a single-developer SaaS being built rapidly. Architectural decisions are made in conversations with Claude Code or while writing code. Without a record, the *why* behind decisions vanishes — and future engineers (or future Ruben) will revisit and possibly undo them without context.

Examples of decisions already made that warrant ADRs going forward: the OCR-first-then-LLM extraction pipeline, the choice of Gemini Flash as primary extraction LLM, the cookie-based JWT auth model, the global tenant query filter approach, the `FOR UPDATE SKIP LOCKED` pattern for the extraction queue.

## Decision

We record significant architectural decisions as Architecture Decision Records (ADRs) under `docs/adr/`, following Michael Nygard's lightweight ADR format. Each ADR is numbered (NNNN, zero-padded to 4 digits), titled, and follows the template at `docs/adr/template.md`.

ADRs are appended via the `/adr <title>` slash command, which interviews the user before drafting and then asks for confirmation.

## Consequences

### Positive
- Future engineers (and future-self) can understand *why*, not just *what*.
- Decisions become explicit artifacts, surfacing tradeoffs that conversations don't.
- Forces clarity at decision time — writing it down often improves the decision.

### Negative
- Friction: every architectural decision now takes 10–20 extra minutes to document.
- Risk of bikeshedding ADR format vs actually deciding.

### Neutral
- ADRs are append-only; superseded ones stay in the repo with a pointer to the replacement.
- Not every decision warrants an ADR — coding-style nitpicks, library choices that don't affect data flow, and small refactors are out of scope.

## References

- https://github.com/joelparkerhenderson/architecture-decision-record
- Michael Nygard, "Documenting Architecture Decisions" (2011)
