# 0038. Vendor contact-email validation — one strict rule, mirrored in two languages

- **Status:** accepted
- **Date:** 2026-07-20
- **Deciders:** Ruben G. (founder), Claude (implementing #369)

## Context

The vendors LIST add-form had guarded the contact email since FP-076. The vendor DETAIL edit
form had no equivalent check, and the API only trimmed. So a typo saved through the edit path —
`jane@acme,com`, or a pasted `Jane Smith <jane@acme.com>` — returned `200 OK` and then broke
**every** subsequent reminder send for that vendor, silently: sends retry in place (ADR 0025) and
surface nothing to the operator unless Resend happens to bounce. The vendor looks fine in the UI
while no reminder can reach it.

#369 *is* what happened when one form owned the only copy of a rule. Three decisions follow from
that, and each has a non-obvious failure mode that cost a review pass to find.

## Decision

### 1. One shared predicate per side, and the API is authoritative

`frontend/src/lib/contact-email.ts` (consumed by BOTH vendor forms) and
`api/CompliDrop.Api/Services/ContactEmail.cs` (consumed by `CreateVendor` and `UpdateVendor`,
returning `400 validation.contact_email`). The client guard exists to explain the problem inline
while the user types; the server check exists because `/api/vendors` is reachable without either
form. Blank stays legal on both sides — a vendor with no contact email is a supported state.

Endpoints call `ContactEmail.TryNormalize(raw, out var normalized)` rather than
`IsWellFormed` followed by `Normalize`, so the value that was **checked** is by construction the
value that gets **written**.

**Deliberately NOT unified with `AuthEndpoints.IsValidEmail`**, which stays lax (`Contains('@')`).
The asymmetry is load-bearing: an ACCOUNT email is *proven* by the verification mail, so a typo
self-corrects when the mail never arrives, and over-strict signup validation locks a real customer
out of registering. A VENDOR contact email is never proven — nothing round-trips through it — and
a typo fails silently forever. Different evidence, different strictness.

### 2. The blank class is spelled out explicitly, never `\s`

Both mirrors declare the blank-or-invisible set as explicit `\uXXXX` ranges and strip edges with
that same set instead of `Trim()` / `.trim()`.

This is not verbosity for its own sake. **.NET's `\s` and JS's `\s` are different character
classes** — .NET's includes U+0085 (NEL) and excludes U+FEFF (BOM); JS's is exactly the reverse —
and the two native trims diverge on the same pair. Relying on each engine's `\s` made the mirrors
genuinely disagree on real input: a pasted BOM was REJECTED client-side and ACCEPTED server-side,
which then stored an unsendable address. That is the very failure this ticket is about, recreated
one layer down. `\s`, `\p{C}`, or any general-category class resolves against each runtime's
Unicode tables and re-introduces the same engine-dependence in a new costume.

The explicit class also covers the C0 controls, which `\s` does not: a NUL reached
`SaveChangesAsync` and raised Postgres 22021 (`varchar` cannot store `0x00`) as a **500**.

### 3. Edge-stripping is a linear scan, not a regex

The strip was `Regex.Replace` over `^[Blank]+|[Blank]+\z`. The second alternative is unanchored at
its start, so when it cannot match, the engine retries at **every offset**, and at each offset the
greedy `[Blank]+` re-consumes the run before failing — O(n²), with no match timeout configured
anywhere in the API and the 256-char cap applied only AFTER normalization.

**The hostile shape is not the obvious one**, and this is the part worth remembering: leading or
trailing padding is **linear** even at 10 MB (1.3 ms measured), because the `^`-anchored
alternative matches at offset 0 and consumes the whole run in a single match. The pathological
input is blanks in the **MIDDLE** with a non-blank at **both** ends, where neither alternative can
ever match. Measured on the real generated-regex path:

| n (interior blanks) | .NET `[GeneratedRegex]` | JS `String.replace` |
|---|---|---|
| 100,000 | 225 ms | — |
| 200,000 | 1.0 s | — |
| 400,000 | 4.3 s | — |
| 500,000 | 6.8 s | 203 s |

~4× per doubling — clean quadratic. Extrapolated to a 10 MB body, which Kestrel accepts and any
authenticated org user can post to `PUT /api/vendors/{id}`, the server side is on the order of
**45 minutes of one pegged CPU** on a shared Railway instance. The client side is worse per byte
and runs synchronously on every keystroke and paste in both vendor forms, so the same pattern
freezes the tab.

Both mirrors now strip with an O(n) index scan over the identical explicit set. Semantics are
unchanged — verified by a differential test over 300,000 randomized inputs drawn from an alphabet
dense in blank code points and both surrogate halves, with zero mismatches.

### 4. Agreement is mechanical, not asserted in a comment

- One shared accept/reject corpus,
  `api/CompliDrop.Api.Tests/SharedFixtures/contact-email-cases.json`, drives BOTH suites. It lives
  in the api test tree so `api-ci`'s `api/**` filter covers a corpus-only edit — the exact change
  contributors are told to make; `frontend-ci` names it explicitly (the #272 precedent). Under
  `docs/` **neither** workflow ran for it.
- The corpus covers every range component of the blank class, both endpoints of every
  multi-code-point range, and the adjacent code points that must stay legal, in both the
  *reject* position (mid-string) and the *strip* position (at an edge) — two different code paths
  over the same set. It also declares `maxLength`, so the two length constants cannot drift.
- Each side has a test walking **every code point in the BMP**, pinning its character class
  against its linear predicate. The set exists twice per language by necessity (the class is used
  to reject, the predicate to strip); these tests make a range added to one but not the other fail
  immediately.

## Consequences

- **A legacy-malformed vendor is block-until-fixed.** `UpdateVendor` validates the submitted
  address whether or not this request changed it, so a vendor whose STORED address is malformed
  (written by the pre-#369 unguarded path) must have it corrected or cleared before unrelated
  edits — including checklist reassignment, which drives grading — will land. Chosen deliberately:
  the address is actively failing, the detail form shows the reason inline on load with Save
  disabled, and both fixing and clearing are accepted. The alternative (validate only when the
  value changed) is friendlier but lets a known-dead address persist indefinitely behind unrelated
  saves. Finding such rows without opening each vendor is
  [#430](https://github.com/neboxdev/complidrop/issues/430).
- **Neither vendor form uses `type="email"`.** The browser's native constraint validation has an
  ASCII-only local-part grammar, so inside a real `<form>` it silently blocked submission of
  `josé@empresa.es` — which the shared predicate accepts and the other form saved happily. That is
  the same drift class on a new axis. `inputMode="email"` keeps the mobile keyboard and
  `autoComplete="email"` keeps autofill; only the contradicting grammar is dropped. Preferred over
  `noValidate` on the `<form>` so the guarantee is local to the field. **Four account-email inputs
  still carry `type="email"`**, all in real `<form>`s with no `noValidate`, so all four are
  stricter than the API they post to (`AuthEndpoints.IsValidEmail` accepts anything with an `@`):
  `(auth)/login`, `(auth)/register`, `(auth)/forgot-password`, and
  `(dashboard)/settings/account-management.tsx` (the change-email field). The first three are
  under the `frontend/src/app/(auth)/**` sensitive glob; the settings one is **not** — it is
  deferred purely as out-of-scope for this ticket, not for sensitivity. Tracked separately.
- **Bidi and invisible-format controls are still accepted** (U+200E/200F, U+202A–U+202E,
  U+2061–U+2064, U+2066–U+2069). They are not in the blank class, so `ops\u200E@acme.com` is
  stored, renders pixel-identical to the real address in the vendor list, and is unsendable — the
  same silent-failure class this ticket is about, but harder to diagnose because nothing is
  visible. Deliberately deferred rather than fixed here: widening the class changes accept/reject
  semantics beyond the ticket's acceptance criteria, and it is late-session scope creep of exactly
  the kind that caused a churning stop on this branch. The corpus lists a few such code points
  under `valid` **only** to pin the range bounds; its header states that `valid` means "the
  predicate accepts this", not "this is a good address". Tracked separately.
- **This predicate is a typo catcher, not an RFC 5322 parser.** Over-strict validation that
  rejects a real address is a worse failure than letting an odd one through. Notably it does NOT
  use `System.Net.Mail.MailAddress`, which ACCEPTS the display-name form
  `Jane Smith <jane@acme.com>` — one of the two literals #369 reports — and would then persist the
  whole string as the send address.
- Normalization strips so the stored value round-trips EXACTLY against the per-(org, email)
  suppression key, which the Resend webhook writes trimmed (#340). Casing is preserved; every
  comparison site is already case-insensitive.
