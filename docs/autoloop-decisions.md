# Autoloop decisions

Decision log for the `complidrop-backlog-worker` scheduled task — the every-2h loop that works one
backlog ticket end-to-end. One entry per run that touched sensitive scope or made a judgement call
worth a second opinion.

**Audience: the founder, reading after the fact to reverse anything they disagree with.** Each entry
records what was decided, what risk was accepted, and exactly how to undo it. Entries are
append-only; correct a past decision with a new entry rather than editing the old one.

---

## 2026-07-19 — #364 DeleteRule N+1 re-evaluation

- **Ticket:** [#364](https://github.com/neboxdev/complidrop/issues/364) (label `bug`)
- **PR:** [#421](https://github.com/neboxdev/complidrop/pull/421), squash-merged as `fee3726`
- **Merged by:** you (`neboxdev`), 2026-07-19 23:07 UTC — **not** by the loop. See "How this got
  merged" below; this entry is written after the fact at your request.
- **Spin-off tickets:** [#422](https://github.com/neboxdev/complidrop/issues/422) (bug),
  [#423](https://github.com/neboxdev/complidrop/issues/423) (task)
- **Scratch PRs, both closed unmerged:** [#424](https://github.com/neboxdev/complidrop/pull/424),
  [#425](https://github.com/neboxdev/complidrop/pull/425) — mutation-testing proofs, see below

### Why this was sensitive

`.claude/reviewers.md` § Sensitive areas lists **Compliance-verdict semantics: `ComplianceStatus`,
`IComplianceCheckService`, …**. The diff adds a method to `IComplianceCheckService`, changes
`ComplianceCheckService`, and changes *which* documents get `ComplianceStatus` rewritten and *when*.
The project severity anchor for this area is blunt: *"Wrong persisted compliance verdict — the
product IS the verdict: **blocker**."*

None of the machine-readable sensitive **globs** match, so the `--careful` flag here came from the
prose list, judged by the session rather than matched mechanically.

### What changed

`DeleteRule` re-evaluated every affected document one-by-one *inside* the delete transaction —
~4 serial round-trips per document on a never-cleared change tracker, holding the rule and check-row
locks throughout. At ~1000 documents on a shared checklist that is a request timeout, so the delete
never landed. Check cleanup and rule delete now stay in one transaction (preserving the FK-restrict
409 retry) and re-evaluation moved after the commit onto the existing batched page path.

### Judgement calls and accepted risks

**1. Diverged from the ticket's own suggested fix.**
The ticket said to call `ReevaluateForTemplateAsync(template.Id, ct)` "like `UpsertRule`". That is
not safe: the predicate joins through `d.Vendor`, which carries the Vendor soft-delete query filter,
and `DeleteVendor` re-grades nothing — so a soft-deleted vendor's documents keep a `Compliant`
verdict *and* their check rows while dropping out of template membership. The old per-document loop
healed those to `Pending` incidentally. Shipped instead:
`ReevaluateForTemplateOrDocumentsAsync(templateId, affectedDocIds, …)`, re-grading template
membership ∪ the deleted rule's check-row holders, keeping the batched path a strict superset of the
loop it replaced. The `DELETE … RETURNING "DocumentId"` snapshot was kept for this reason.
*Risk accepted:* a slightly wider population than the ticket described, so a rule delete re-grades a
few documents that did not strictly need it.
*To revisit:* if you prefer the narrower population, drop the `documentIds` argument and call
`ReevaluateForTemplateAsync` — but fix [#422](https://github.com/neboxdev/complidrop/issues/422)
first, or you reintroduce the vacuous-`Compliant` hole this union exists to cover.

**2. Expanded scope past `DeleteRule` to all four post-commit fan-outs.**
The review found the fan-out was awaited on the request's `CancellationToken` (minimal APIs bind
that to `HttpContext.RequestAborted`). Post-commit there is nothing to roll back, so a client
disconnect truncated the re-grade and stranded documents on pre-mutation verdicts with no healer.
The same bug already shipped in `UpsertRule`, `DeleteTemplate` and `VendorEndpoints.UpdateVendor`.
All four now share `PostCommitRegrade.RunAsync`. `UpdateVendor` is the worst of them — a
reassignment can *tighten* a checklist, so a truncated re-grade there is a genuine false
`Compliant`, not merely a stale-strict one.
*Risk accepted:* the ticket asked about one endpoint; three more behaviours changed. Recorded in the
ticket body before implementing, per the discovered-scope rule.
*To revert just this:* restore `ct` at the call sites in `ComplianceEndpoints.UpsertRule`,
`DeleteTemplate` and `VendorEndpoints.UpdateVendor`, leaving `DeleteRule` on the shared runner.

**3. Fan-out failures are now swallowed — the endpoint returns 200, not 500.**
With the fan-out after the commit, anything it threw became a 500 for a rule that *is* deleted; the
user retries and gets a 404 (or a 409 "already on this checklist" on the upsert twin). Two triggers
are production-reachable and sit outside the fan-out's own per-page catch: its snapshot query, and
the cancellation the token itself raises (a SIGTERM during a Railway deploy fires
`ApplicationStopping` at t=0 of the drain). `PostCommitRegrade.RunAsync` logs and swallows.
*Risk accepted:* **a user can now see a success response for a mutation whose re-grade did not
finish.** Affected documents keep their previous verdict until the next user-initiated re-grade
("Check again", another rule edit, a reassignment). The nightly sweep does **not** heal this — it
only does date transitions. On the delete path the staleness is stale-*strict* (a `NonCompliant`
that should read `Compliant`), never a false `Compliant`, except when the **last** applicable rule
is deleted, where a stale `Compliant` should have become `Pending`. On the `UpsertRule` /
`UpdateVendor` paths a tightening change *can* leave a false `Compliant`.
*To revisit:* this is the decision most worth your scrutiny. The durable fix is a tenant-path
re-grade watermark like the seed path's `RegradedThroughRevision` (ADR 0036 Amendment 2), or moving
the fan-out to a queue — scoped in
[#423](https://github.com/neboxdev/complidrop/issues/423).
*To revert:* delete the `try`/`catch` in `PostCommitRegrade.RunAsync` and let failures surface as
500s. The test `The_rule_delete_survives_a_failing_re_evaluation_fan_out` will fail on its 200
assertion — that is the intended signal, not a broken test.

**4. A 2-minute ceiling on the fan-out.**
`ApplicationStopping` alone only trips at shutdown, so an abandoned request's fan-out would page
documents and hold an Npgsql pool connection indefinitely — the client disconnect used to reclaim
both. `PostCommitRegrade.Timeout` caps it.
*Risk accepted:* an org large enough to exceed 2 minutes gets a truncated re-grade, treated exactly
like a failed page.
*To change:* it is a single `TimeSpan` constant in `PostCommitRegrade`.

**5. Two review findings deferred rather than fixed.**
- [#422](https://github.com/neboxdev/complidrop/issues/422) — `DeleteVendor` never re-grades, and
  `DeleteTemplate`'s vendor snapshot has the same soft-delete hole. Deferred because guarding at the
  `DeleteTemplate` call site needs `IgnoreQueryFilters()` in request-path code, which CLAUDE.md
  forbids ("only inside background workers or system contexts"); fixing `DeleteVendor` removes the
  precondition for every downstream path at once.
- [#423](https://github.com/neboxdev/complidrop/issues/423) — the fan-out ships unused
  `ExtractionRawJson` (~20 KB/doc) per page, marks every document dirty even when its verdict is
  unchanged (an `UPDATE` + an `AuditLog` row each), and runs inline on the request thread. Deferred
  because the fixes are EF table splitting against a populated column, a queue, or an audit-semantics
  change — all ADR-worthy.

*If you disagree with either deferral, both tickets carry the full reasoning and reproduction steps.*

### Verification, and its one real gap

- **Two verified review passes** (5 core personas + `compliance-claims`, Opus-pinned, 3 adversarial
  verifiers per bug), 0 dead reviewers each. 2 CONFIRMED bugs — both fixed. 0 PLAUSIBLE, 0 REFUTED.
  13 suggestions: 4 implemented, deferred ones ticketed. Full report: `.claude/reviews/364.md`.
- **CI green at `4d2ccd9`: 1371 passed, 0 failed, 1 skipped.**
- **⚠️ The suite never ran locally.** Docker Desktop on the session host would not start — its WSL VM
  stayed `Stopped` and the daemon pipe hung — and the integration suite needs Testcontainers. Three
  recovery attempts failed (waiting out the boot, `wsl -d docker-desktop` directly, full kill +
  `wsl --shutdown` + restart). Locally verified: Release build and the `dotnet format
  --verify-no-changes` gate. **CI was the only thing that executed the tests.** If the host's Docker
  is still broken, future runs have the same gap.
- **Both fixes were mutation-tested through CI** rather than asserted, on scratch branches now
  closed: reverting `ComplianceEndpoints.cs` to `origin/main` while keeping the tests failed exactly
  the 3 discriminating tests (#424, `Failed: 3, Passed: 1367`); removing only the swallow failed
  exactly `The_rule_delete_survives_a_failing_re_evaluation_fan_out` on 200-vs-500 (#425,
  `Failed: 1, Passed: 1370`).
- **Not proven by test:** the request-token fix (decision 2). Client-abort timing is inherently racy,
  so there is no deterministic test; it was verified structurally — no post-commit fan-out call site
  receives the request token any more.

### How this got merged, and an open question for you

The loop did **not** merge this. It ran the gate with `--careful`, got the terminal
`careful-flag` block, stopped at exception 2, and left the PR open with a comment. You merged it
yourself at 23:07 UTC.

The reason the loop did not self-merge: your 2026-07-18 override authorizes it to merge tickets
**labeled `careful-review`**, and #364 is labeled `bug` only. Its diff merely *landed in* a sensitive
area. The session declined to extend the authorization by analogy, on the grounds that a delayed
merge costs nothing while a wrong unilateral merge into auto-deploying verdict code does.

**Open question:** if you intend the loop to self-merge any diff that touches a sensitive area — not
only tickets you have labelled — the override wording in the scheduled task needs widening, since
`--careful` is decided by the session reading `reviewers.md`, not by the label. As written, the loop
will keep stopping on cases like this one.
