# 0035. Standing cleanup-tooling gates (dotnet format + knip)

- **Status:** accepted
- **Date:** 2026-06-25
- **Deciders:** Ruben G.

## Context

Epic [#41](https://github.com/neboxdev/complidrop/issues/41) (codebase simplification) starts from a
6-reviewer PM finding: most of the bloat AI-generated code accumulates — dead files, dead exports,
unused dependencies, unused usings, stray whitespace — is **mechanically detectable and fixable by
off-the-shelf tools**, which then keep the codebase clean via CI far more cheaply and durably than a
recurring human review. So the epic's shape is *tools first* (ticket [#42](https://github.com/neboxdev/complidrop/issues/42)),
then a small human-judgment pass only where tools cannot reach (#43 backend, #44 frontend).

Two constraints from the epic shape this decision: (1) the fixes must be strictly **behaviour-preserving**
(no logic, contract, schema, or control change), and (2) there must be **one authoritative tool per
finding class** — running two gates for the same class is noise, and the choice must be documented.

## Decision

Add a standing CI gate to each stack, enforcing only mechanical, behaviour-neutral cleanups, and pin
each gate's config with a guard test so it cannot silently regress.

### Frontend — knip

- **knip is the sole authority** for the dead-files / unused-deps / unlisted-imports / dead-exports
  classes. **ts-prune** (in maintenance mode; its own README points to knip) and **depcheck** (whose
  dependency check overlaps knip's) are deliberately **not** added.
- Dead-**export** detection is kept **live across the whole source tree**, with the intentional
  public-surface directories scoped out via `ignore` (`src/components/ui/**` shadcn kit,
  `src/test/**` toolkit, `e2e/support/**` Playwright harness) — rather than turning the export rule
  off globally, which would surrender detection on the other ~95 source files.
- CI: `npm run knip` in `frontend-ci.yml`. knip's version is pinned in `package-lock.json`, so a local
  `npm run knip` pass guarantees a CI pass.
- Config + rationale live in `frontend/knip.jsonc`; invariants pinned by `frontend/src/test/knip-config.test.ts`.

### Backend — dotnet format + .editorconfig

- **`dotnet format`** driven by the repo-root **`.editorconfig`** enforces mechanical wins only:
  unnecessary usings (IDE0005), trailing whitespace, final newline, UTF-8 without BOM. The opinionated
  object/anonymous-initializer **reflow is opted out** (`csharp_new_line_before_members_in_*` = false)
  so the gate does not churn the codebase's deliberately compact initializers — "no line-count golfing"
  per the epic.
- Generated EF migrations are excluded, **anchored to `[api/CompliDrop.Api/Migrations/*.cs]`** only —
  the test project's hand-written `Migrations/*Tests.cs` stay under the gate.
- CI: `dotnet format CompliDrop.slnx --verify-no-changes` in `api-ci.yml`. The .NET 10 SDK feature band
  is pinned in **`global.json`** (`10.0.200` + `rollForward: latestFeature`) so local and CI run the
  same formatter.
- Invariants pinned by `api/CompliDrop.Api.Tests/CleanupGateConfigTests.cs`.

Both workflows' `paths:` triggers include the root config (`global.json`/`.editorconfig`) so a
config-only change still re-validates the gate.

## Consequences

- **Bloat can't silently re-accumulate**: every PR re-runs both gates; a new unused using/dep/file or
  newly-orphaned export fails CI. Guard tests stop a future tool upgrade or careless config edit from
  quietly gutting either gate.
- **Judgment calls stay out of the gate.** Over-abstraction, real duplication, and the
  intentional-public-surface export pruning are human work for #43/#44, not mechanical CI failures.
- **Accepted cost — `dotnet format` adds ~25s** to `api-ci` on the critical path (it must re-load the
  Roslyn workspace; there is no `--no-build`). Kept **inline** rather than split into a parallel job:
  a separate job would duplicate restore+build and spend more billable minutes for marginal feedback
  latency — not worth it on a solo pre-launch repo. Revisit if api-ci wall-clock becomes painful.
- **`global.json` footgun.** `rollForward: latestFeature` is forgiving upward within the 10.0 band
  (CI's `setup-dotnet 10.0.x` satisfies it, no parity gap), but it does **not** cross major.minor: once
  every 10.0.x SDK is uninstalled locally (only 11.x present), `dotnet`/`dotnet format` hard-fails until
  `global.json` is bumped. That is the deliberate, visible cost of a real SDK pin — bump the file when
  dropping the 10.0 line.
- **`@sentry/nextjs` kept-but-ignored.** It is installed but never wired into the frontend (no sentry
  config / instrumentation / import); the privacy policy's "Sentry — error monitoring" claim is
  satisfied today by the **backend** SDK. Whether to wire frontend error monitoring or drop the SDK is a
  product/legal decision, not mechanical cleanup — ignored in `knip.jsonc` (documented) and flagged for a
  separate decision rather than silently removed.

## Alternatives considered

- **ts-prune / depcheck alongside knip** — rejected: duplicate gates for the same finding class.
- **knip `exports`/`types` rules off globally** — rejected: surrenders dead-export detection repo-wide to
  silence noise confined to ~3 directories; scoped `ignore` keeps detection live where it matters.
- **Adopt the formatter's initializer reflow** — rejected: large behaviour-neutral churn that obscures
  history and adds lines, contradicting the epic's "no line-count golfing".
- **Parallel `dotnet format` CI job** — rejected for now (duplicated build; see Consequences).
