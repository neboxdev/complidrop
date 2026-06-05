# Bug report template

Copy the block below into a new GitHub issue. Apply the `bug` label so [#48](https://github.com/neboxdev/complidrop/issues/48) picks it up. Add `launch-blocker` only if it would stop a real customer from completing a first-day workflow.

---

```markdown
## Summary
<!-- One line. "Save changes button stays disabled after editing a field." -->

## Where
- Section in QA plan: <!-- e.g. §4.3 step 7 -->
- URL / page: <!-- e.g. /documents/abc123 -->
- Component / area: <!-- e.g. Documents detail page → Extracted fields card -->

## Steps to reproduce
1.
2.
3.

## Expected
<!-- What the QA plan said should happen, quoted where possible -->

## Actual
<!-- What you observed instead. Be specific about text, color, position. -->

## Severity (your judgement)
- [ ] **launch-blocker** — real customer can't complete a core workflow
- [ ] **major** — workflow completes but lands in a confusing/wrong state
- [ ] **minor** — works correctly but cosmetic / polish miss
- [ ] **edge** — only reproduces under unusual conditions documented below

## Reproducibility
- [ ] Every time
- [ ] Sometimes (~%)
- [ ] Once, can't reproduce

## Environment
- Browser: <!-- e.g. Chrome 132 / Firefox 134 / Safari 18 / Edge 132 -->
- OS: <!-- Windows 11 / macOS Sonoma / iOS 18 -->
- Viewport: <!-- desktop 1440×900 / iPhone 14 / 360×800 emulated -->
- Account: <!-- which test account, plan, fresh or seeded -->

## Evidence
<!-- Drop in: -->
<!-- - screenshot or screen recording -->
<!-- - console errors (browser DevTools → Console) -->
<!-- - network response (DevTools → Network → failed request → Response tab) -->
<!-- - correlation id from X-Trace-Id header if you have it -->

## Triage suggestion
<!-- Optional. -->
<!-- - Fix now (blocks launch) -->
<!-- - Defer to #48 (acceptable post-launch) -->
<!-- - Defer to #150 (post-launch follow-up) -->
<!-- - Working as designed (close + add to docs/qa/manual-testing-plan.md "known limits") -->
```

---

## Tips for good bug reports

- **Include the literal text you saw.** "Toast says 'Bad Gateway'" tells the dev exactly which guardrail leaked. "Error toast appeared" doesn't.
- **One bug per issue.** If two bugs share a root cause, the dev will figure it out. Splitting prevents partial fixes from closing a multi-bug ticket.
- **Always capture the correlation id when you can.** Open DevTools → Network → click the failing request → Headers → look for the `X-Trace-Id` response header (present on every response). Error responses also echo the same id as `error.correlationId` in the Response body. Paste either. This is the single fastest way to find the matching server log.
- **Network errors deserve a screenshot of the Response tab, not just the toast.** The user-facing copy is sanitized; the underlying server `code` (`document.too_large`, `rate_limit.exceeded`) is in the response JSON.
- **Take a screen recording for any race or polling-related bug.** "The badge sometimes doesn't update" needs ~30 seconds of video to be actionable.
