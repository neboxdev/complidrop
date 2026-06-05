# Pre-launch QA — how to use this folder

You ("the tester") are about to drive every workflow CompliDrop ships, by hand, in a real browser, against a real running environment. Goal: catch anything a real customer would feel before they feel it.

> The plan was last reconciled against the app on **2026-06-05**, after the UX overhaul (PRs #181–#197, #216, #220). Where the plan and the app disagree, the **app is the source of truth** — file a doc-fix, not necessarily a product bug.

## What's in here

| File | Purpose |
|---|---|
| [`manual-testing-plan.md`](manual-testing-plan.md) | The master plan. Eighteen sections, every workflow, every edge case, every visual contract. Work through it top-to-bottom. |
| [`test-fixtures.md`](test-fixtures.md) | What test files you need (sample COIs, oversized PDFs, etc.), where to put them, and how to generate the synthetic ones. |
| [`bug-report-template.md`](bug-report-template.md) | Copy/paste body for any GitHub issue you open for a bug found here. Keeps the bug-fix epic ([#48](https://github.com/neboxdev/complidrop/issues/48)) tidy. |

## How to read the plan

Each test step is one of these shapes:

> **Do:** {single concrete action — "Click X", "Type Y", "Drag file Z"}
> **Expect:** {the exact thing the user should see, with copy in quotes where possible}
> **Don't expect:** {anti-expectations — common failure modes you'd otherwise miss}

Every step has a `[ ]` checkbox. Tick as you go. If a step fails, **don't** fix it inline — log a bug, mark the checkbox `[!]`, and keep moving. A single failed test rarely blocks the next test; momentum matters.

## How long this takes

Realistically: **6–10 focused hours**, spread across 2–3 sittings. Don't try to do it in one go — eyes glaze, polish bugs slip past. Suggested cadence:

| Sitting | Sections | Why batch them |
|---|---|---|
| 1 (~2.5h) | §0 setup, §1 smoke, §2 first-time user, §3 auth & account | Establishes baseline & all the canned accounts the rest needs (§3 now covers password reset, change email/password, delete account, email verification) |
| 2 (~3h) | §4 documents, §5 vendors, §6 portal, §7 vendor requirements, §8 reminders | The product's core surface |
| 3 (~2h) | §9 export, §10 billing, §11 settings, §12 dashboard | Money + reporting + read-only views |
| 4 (~2h) | §13 multi-tenancy, §14 edge cases, §15 a11y, §16 known limits, §17 perf, §18 sign-off | The "polish before launch" pass |

## What you're explicitly NOT doing here

- **Code testing.** Unit + integration tests already cover that. If something looks broken, the question is "what does the user see and feel?", not "what's the bug in the implementation?".
- **Performance load testing.** You're checking *perceived* feel (cold-start, polling cadence), not throughput.
- **Security pen-testing.** Multi-tenancy isolation gets one user-facing pass in §13; deeper boundary testing is its own future ticket.
- **Re-testing the architecture doc.** Trust it. If reality contradicts it, that's a doc bug — note it as a finding, don't change behavior to match.

## When to stop

Launch-ready means:

- Every checkbox in [`manual-testing-plan.md`](manual-testing-plan.md) is `[x]` or has a linked bug ticket and a stated fix-or-defer decision.
- §18 sign-off table is fully filled in.
- No open bugs labeled `bug` + `launch-blocker`.

Anything below `launch-blocker` (cosmetic, polish, low-frequency edge) can be deferred to [#48](https://github.com/neboxdev/complidrop/issues/48) or [#150](https://github.com/neboxdev/complidrop/issues/150) post-launch — log it, link from the plan checkbox, keep going.

## Reporting bugs

For each failure:

1. Copy [`bug-report-template.md`](bug-report-template.md) into a new GitHub issue.
2. Apply the `bug` label (auto-indexes into [#48](https://github.com/neboxdev/complidrop/issues/48)).
3. If it would block a real customer signup or first-day use, add `launch-blocker`.
4. Drop the issue link next to the failed `[!]` checkbox in the plan so the trace is recoverable later.

If a section uncovers a *theme* (multiple bugs in the same area) write one tracker issue for the theme and link the individual bugs as sub-items.
