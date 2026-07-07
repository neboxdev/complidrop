# CompliDrop reviewer personas (project overlay)

This directory holds ONLY CompliDrop-specific personas. The generic roster (5 code +
5 PM reviewers) lives in the machine-level claude-kit at `~/.claude/agents/` — see its
README for conventions (read-only rule, opus pinning, findings contract, evidence
requirements). Do not copy generic personas back into this repo.

## Personas here

- `compliance-claims-reviewer` — **code roster.** Hunts divergence between what the
  product claims (verdict labels, deletion promises, privacy copy, exported PDFs,
  marketing JSON-LD) and what the code does. Exists because issues #396–#405 proved
  this class is structurally invisible to the generic five.
- `legal-compliance-reviewer` — **PM roster.** Challenges specs on privacy, US
  regulatory exposure (HIPAA, state privacy laws, retention rules), third-party AI
  processing paths, and liability from compliance claims.

## Wiring

Rosters are declared in [.claude/reviewers.md](../reviewers.md) ("Extra personas") —
the /review, /start, /plan and /pm-review skills read that file and pass these names
to the review engine. Adding a persona here without listing it there means it never
runs.

## Project facts

Volatile facts (rate limits, scale, deliberate patterns) belong in
[.claude/reviewers.md](../reviewers.md), NOT in persona prompts — that file is updated
in the same PR as the code that changes a fact. These two personas may reference
stable product context (what CompliDrop is, who uses it) but must point at
reviewers.md/CLAUDE.md/ADRs for anything that can drift.
