# 0026. Environment-aware required-config validation uses a custom IValidateOptions + ValidateOnStart

- **Status:** accepted
- **Date:** 2026-06-15
- **Deciders:** Ruben G.

## Context

The #237 prod incident surfaced a class of config bugs that all share a shape: a setting has a
local-dev-friendly default that is *wrong in production*, nothing validates it at boot, and the
failure surfaces much later — on a user's most important request — as an opaque error.

- **#250**: `Frontend:BaseUrl` defaults to `http://localhost:3000`; prod never overrode it, so the
  API minted `http://localhost:3000/portal/<token>` links and mailed them to real vendors. The
  failure was invisible until a vendor clicked a dead link.
- **#248**: `AzureStorage:ConnectionString` is empty by default; a missing/malformed value made
  `BlobStorageService` throw on **first upload** (the product's core path) as a generic 500, with no
  log naming the misconfiguration.

The existing precedent — `JwtSettings` — uses `AddOptions<T>().ValidateDataAnnotations().ValidateOnStart()`.
Data annotations are fine for environment-*invariant* rules (`[Required]`, length, range), but these
new rules are environment-*aware*: the localhost default and the empty connection string are **valid
in Development** (local dev, and integration tests that fake the dependency) and **invalid everywhere
else**. Data annotations cannot express "required unless `IHostEnvironment.IsDevelopment()`".

## Decision

Environment-aware required-config validation uses a **custom `IValidateOptions<T>` registered as a
singleton, wired with `.ValidateOnStart()`** on the options builder. Data annotations remain the tool
for environment-invariant rules.

The validator shape is uniform across settings (see `FrontendSettingsValidator`,
`AzureStorageSettingsValidator`):

- `sealed`, constructor-injects `IHostEnvironment`.
- Short-circuits `return ValidateOptionsResult.Success` when `env.IsDevelopment()`.
- Outside Development, rejects the unsafe values with a **clear, actionable message that names the
  config key and never echoes the secret value** (the blob validator parse-checks the connection
  string via `new BlobServiceClient(...)` — which parses with no network call — and reports
  "malformed" without printing the string).

Wiring in `Program.cs`:

```csharp
builder.Services.AddOptions<FrontendSettings>()
    .Bind(builder.Configuration.GetSection("Frontend"))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<FrontendSettings>, FrontendSettingsValidator>();
```

`ValidateOnStart()` runs the validator during host startup, so a misconfigured deploy **fails fast at
boot** (an aborted boot keeps the old Railway container serving — see [ADR 0016](0016-apply-ef-migrations-on-startup.md))
instead of 500ing or mailing dead links on the first request that touches the setting.

## Consequences

### Positive
- A whole class of "wrong-in-prod default" bugs becomes a loud boot failure with a fix-it message,
  not a silent user-facing failure discovered in the field.
- Consistent, copyable shape: the next required setting (more secrets will adopt this) follows the
  same template, and the validators are pure and trivially unit-testable in isolation.
- The boot-fail *wiring* (registration + `ValidateOnStart`) is pinned by an integration test per
  setting (booting a `Production` host and asserting `OptionsValidationException`), so a regression
  that drops the registration is caught — not just the validator logic.

### Negative
- A validator that is too strict can block a legitimate boot (e.g. an unusual-but-valid origin). Rules
  must reject only genuinely-unroutable/unsafe values; the #301 follow-up tightened the
  `Frontend:BaseUrl` loopback check after exactly this kind of edge (trailing-dot / wildcard hosts).
- Two validators that drift in shape re-introduce inconsistency; keep them mirrored.

### Neutral
- Development and the integration-test harness keep the permissive defaults (the dependency is faked
  or self-disables there), so the guard never interferes with local work or tests.

## Alternatives considered

### Option A — Data annotations only (`ValidateDataAnnotations`)
Cannot express "required unless Development". Would either reject the localhost default in dev (breaks
local work) or accept it in prod (the bug). Rejected for environment-aware rules; retained for
invariant ones.

### Option B — Validate lazily on first use (guard inside the service)
The status quo that caused #248: the failure surfaces on the first request that touches the setting,
on the product's core path, as a late and opaque error. Rejected — fail-fast at boot is strictly
better.

### Option C — Health-check / readiness probe reports misconfig
A `/health/ready` 503 is weaker than a startup abort (the process would still accept other traffic)
and duplicates the boot-time guard. Rejected as the primary mechanism, consistent with ADR 0016's
reasoning.

## References

- Tickets: [#248](https://github.com/neboxdev/complidrop/issues/248), [#250](https://github.com/neboxdev/complidrop/issues/250), [#301](https://github.com/neboxdev/complidrop/issues/301) (loopback-guard tightening), [#302](https://github.com/neboxdev/complidrop/issues/302)
- ADRs: [0016](0016-apply-ef-migrations-on-startup.md) (boot-time fail-fast posture)
- Code: `api/CompliDrop.Api/Configuration/FrontendSettings.cs`, `api/CompliDrop.Api/Configuration/AzureStorageSettings.cs`, `api/CompliDrop.Api/Program.cs`
