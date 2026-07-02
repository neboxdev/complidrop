---
description: Run the 5-agent code review + adversarial verification on the current branch
---

Run the multi-agent review against the current branch's diff vs main. **Diagnostic only — do NOT auto-fix.** (`/start` Phase 4 runs this same workflow, then fixes.)

## Process

1. If the branch has no commits ahead of `origin/main` (`git log origin/main..HEAD --oneline` is empty), stop.
2. Invoke the **Workflow tool** with the script below — this command is your explicit opt-in to multi-agent orchestration. When running as `/start` Phase 4, pass the ticket body via `args: { context: "<ticket body>" }`; for a plain `/review`, omit `args`.
3. The workflow returns four buckets:
   - **confirmed** — bug findings that survived both adversarial verifiers. Treat as real.
   - **plausible** — verifiers split (or a verifier died). Read the code yourself and decide; record the call.
   - **refuted** — both verifiers disproved the finding (their reasoning is attached). Report these — never silently drop them — but don't act unless your own reading disagrees.
   - **suggestions** — unverified by design (taste, not truth); triage per the standard rules.
4. Report to the user grouped by bucket, most severe first. Save the full report (including refutations) to `.claude/reviews/<branch>-<YYYYMMDD-HHMM>.md`.

## Workflow script

Models are pinned to `opus` throughout (founder decision 2026-07-02): routine reviews never run on a premium-tier session model. Don't override without being asked.

```js
export const meta = {
  name: 'complidrop-code-review',
  description: '5 reviewer personas over the branch diff, then adversarial verification of every bug finding',
  phases: [
    { title: 'Review', detail: '5 reviewer personas in parallel', model: 'opus' },
    { title: 'Verify', detail: '2 adversarial verifiers per bug finding', model: 'opus' },
  ],
}

const REVIEWERS = [
  'security-reviewer',
  'correctness-reviewer',
  'performance-reviewer',
  'test-quality-reviewer',
  'architecture-reviewer',
]

const FINDINGS_SCHEMA = {
  type: 'object',
  required: ['findings'],
  properties: {
    findings: {
      type: 'array',
      items: {
        type: 'object',
        required: ['kind', 'severity', 'file', 'issue', 'fix'],
        properties: {
          kind: { type: 'string', enum: ['bug', 'suggestion'] },
          severity: { type: 'string', enum: ['blocker', 'major', 'minor'] },
          file: { type: 'string' },
          line: { type: 'number' },
          issue: { type: 'string' },
          fix: { type: 'string' },
        },
      },
    },
  },
}

const VERDICT_SCHEMA = {
  type: 'object',
  required: ['refuted', 'reasoning'],
  properties: {
    refuted: { type: 'boolean' },
    reasoning: { type: 'string' },
  },
}

const context = (args && args.context) || ''
const MAX_VERIFIED_PER_REVIEWER = 10

const reviewPrompt =
  'Review the current branch diff against origin/main, per your persona.\n\n' +
  '1. Run `git diff origin/main...HEAD` and `git log origin/main..HEAD --oneline` (read-only).\n' +
  '2. Review ONLY the changed code unless a concern requires wider context.\n' +
  '3. Read CLAUDE.md (Core patterns) and relevant docs/adr/ before flagging an invariant.\n\n' +
  (context ? 'Ticket under implementation:\n' + context : 'No ticket context - this is a branch-level diagnostic review.')

const verifyPrompt = (f) =>
  'You are an adversarial verifier. A code reviewer reported this finding on the current branch (diff vs origin/main):\n\n' +
  JSON.stringify(f, null, 2) +
  '\n\nTry to REFUTE it. Read the actual code (Read/Grep/Glob; run `git diff origin/main...HEAD` to see the change), then decide:\n' +
  '- refuted=true ONLY if you can articulate concretely why the code is correct as written - cite the guarding code, existing test, or deliberate design decision (check CLAUDE.md Core patterns and docs/adr/) that the reviewer missed.\n' +
  '- refuted=false if the finding holds OR you cannot conclusively disprove it.\n' +
  'You are read-only: never edit files, never run builds or tests.'

const results = await pipeline(
  REVIEWERS,
  (r) => agent(reviewPrompt, { agentType: r, label: 'review:' + r, phase: 'Review', model: 'opus', schema: FINDINGS_SCHEMA }),
  (review, r) => {
    const found = (review && review.findings) || []
    const bugs = found.filter((f) => f.kind === 'bug')
    if (bugs.length > MAX_VERIFIED_PER_REVIEWER) {
      log(r + ': ' + bugs.length + ' bugs; verifying the first ' + MAX_VERIFIED_PER_REVIEWER + ', the rest pass through as PLAUSIBLE')
    }
    return parallel(
      found.map((f) => () => {
        const tagged = Object.assign({}, f, { reviewer: r })
        if (f.kind !== 'bug') return Promise.resolve(tagged)
        if (bugs.indexOf(f) >= MAX_VERIFIED_PER_REVIEWER) {
          return Promise.resolve(Object.assign(tagged, { verdict: 'PLAUSIBLE', refutations: [] }))
        }
        return parallel(
          [1, 2].map((n) => () =>
            agent(verifyPrompt(tagged), {
              label: 'verify:' + f.file + '#' + n,
              phase: 'Verify',
              model: 'opus',
              schema: VERDICT_SCHEMA,
            })
          )
        ).then((vs) => {
          const votes = vs.filter(Boolean)
          const refutes = votes.filter((v) => v.refuted)
          let verdict
          if (votes.length === 0) verdict = 'PLAUSIBLE'
          else if (refutes.length === 0) verdict = 'CONFIRMED'
          else if (refutes.length === votes.length && votes.length >= 2) verdict = 'REFUTED'
          else verdict = 'PLAUSIBLE'
          return Object.assign(tagged, { verdict, refutations: refutes.map((v) => v.reasoning) })
        })
      })
    )
  }
)

const findings = results.filter(Boolean).flat().filter(Boolean)
const bugs = findings.filter((f) => f.kind === 'bug')
log('Findings: ' + bugs.length + ' bugs across ' + findings.length + ' total')
return {
  confirmed: bugs.filter((f) => f.verdict === 'CONFIRMED'),
  plausible: bugs.filter((f) => f.verdict === 'PLAUSIBLE'),
  refuted: bugs.filter((f) => f.verdict === 'REFUTED'),
  suggestions: findings.filter((f) => f.kind === 'suggestion'),
}
```

## Output to the user

```
Review verdict:
- Confirmed bugs: 2 (1 blocker, 1 major) — must fix
- Plausible (needs your judgment): 1
- Refuted by verification: 3
- Suggestions: 5

Confirmed:
1. [security/blocker] api/CompliDrop.Api/Endpoints/Documents.cs:84 — IgnoreQueryFilters bypasses tenant filter on list endpoint. Fix: remove the IgnoreQueryFilters call and verify tenant scoping is enforced via AppDbContext.CurrentOrgId.
...

Refuted (reported for the record, no action):
1. [correctness/major] <file:line> — <claim>; refuted: <verifier reasoning>

Full report saved to .claude/reviews/<branch>-<timestamp>.md.
```

## Rules

- Do NOT modify any files. This command is read-only.
- Never silently drop a refuted finding — it goes in the report with its refutation so a human can override.
- Reviewer personas and verifiers run pinned to `model: opus`. Don't override without being asked.
- If the user wants fixes applied, re-run via `/start <ticket>` or ask explicitly.
