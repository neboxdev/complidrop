# 0053. Liveness and readiness are separate probes; `/health` stays DB-blind and the monitor moves to `/health/ready`

- **Status:** accepted
- **Date:** 2026-08-06
- **Deciders:** Ruben G.

## Context

The API exposes three health endpoints (`Program.cs`):

| Path | Body | Touches the DB |
|---|---|---|
| `/health` | `{ status = "healthy", timestamp }` | no |
| `/health/live` | `{ status = "live", at }` | no |
| `/health/ready` | `{ status = "ready", at }` or 503 | **yes** (`SystemDbContext.CanConnectAsync`) |

A round-2 observability review (#390 item 1) found that the external uptime monitor is pointed at
`/health` — the static one — and that a proper readiness probe already existed one line above it,
used by nothing. The failure that motivates the ticket is the #226 shape: a process that is up while
every query fails. `/health` is green throughout it, so the monitor stays silent: product down, no
alert. The endpoint's own comment ("kept for UptimeRobot compatibility") is the only record that the
monitor is on the DB-blind one, and it does not say that this is a problem.

The ticket offered two fixes: repoint the monitor at `/health/ready`, **or** fold `CanConnectAsync`
into `/health`. The second is the one a repo-side change can actually perform, and it is the
dangerous one:

- **Railway's healthcheck path is not in this repository.** There is no `railway.json` /
  `railway.toml`, and the `Dockerfile` declares no `HEALTHCHECK`. The path is a dashboard setting,
  so from inside the repo we cannot know whether Railway's deploy/restart probe polls `/health`,
  `/health/live`, or nothing. ADR 0016 already refers to it as "the existing UptimeRobot/Railway
  probe" — one probe serving both consumers, which is exactly the ambiguity that makes an
  unobservable change to `/health` unsafe.
- **Merge = prod deploy** (reviewers.md § Deployment model), so a wrong guess ships unattended.
- **A DB-aware liveness probe compounds with ADR 0016's fail-fast boot.** If Railway's healthcheck
  is on `/health` and Neon blips for 30 seconds (a managed-serverless Postgres on the free tier;
  cold-start/auto-suspend behaviour is a known property, and the plan upgrade that removes it is
  gated on $2K MRR), the container is killed as unhealthy and restarted — and the restart runs
  migrations before serving, which ABORTS while the DB is still blipping. A transient blip becomes a
  hard outage, and the outage is caused by the monitoring change. That is strictly worse than the
  silent-monitor bug being fixed.

## Decision

**Keep the two questions separate, and keep them on separate endpoints.**

1. `/health` and `/health/live` are **LIVENESS**: "this process is up". They stay DB-blind. This is
   what a restart/deploy healthcheck belongs on, and adding a database check to either is a
   regression, not an improvement. The endpoints say so in their own comments, and
   `HealthProbeTests.Liveness_probes_stay_green_and_never_touch_the_database` pins it behaviourally
   (a fault interceptor makes every `SystemDbContext` connection open fail; the probes must answer
   200 and the fault count must stay 0).
2. `/health/ready` is **READINESS**: "this process can serve". It is the only probe that touches the
   database and therefore the only one that goes red in the #226 shape. It is the endpoint an uptime
   monitor and any alerting belong on — its consecutive-failure threshold absorbs a transient blip,
   which is the difference between paging and restarting.
3. **Repointing UptimeRobot at `/health/ready` is an external action** (its dashboard), so it is
   recorded where the founder will meet it — the QA launch checklist's monitoring line and README
   § Health probes and monitoring — rather than silently attempted from code.
4. The readiness probe reports failures **server-side**: a warning on the not-reachable branch
   (silent until now) and an error with the exception on the branch EF does not swallow, while the
   response stays a bare 503 (#390 item 4 — the endpoint is public and unauthenticated).

Net invariant: **the probe that a monitor polls is the one that answers a question about the
product; the probe that an orchestrator polls answers a question about the process — and neither
one may quietly become the other.**

## Consequences

### Positive

- The #226 outage shape becomes alertable without touching the deploy path at all: nothing about
  container restarts, boot, or migrations changes.
- The liveness/readiness distinction is now stated at the endpoints and enforced by a test, so the
  next reviewer who notices that "`/health` doesn't check anything" finds the reason instead of
  re-filing it (reviewers.md § Do NOT flag).
- A readiness failure now leaves a trace in our own logs. Previously both 503 branches were silent,
  so a DB incident was invisible on our side as well as to the monitor.

### Negative

- The fix is only half-shipped from the repo's point of view: until UptimeRobot is repointed, the
  monitor is still blind. This ADR deliberately does not pretend otherwise — the alternative is a
  code change that can fail a deploy.
- Three health endpoints for two questions. `/health` and `/health/live` are duplicates; `/health`
  is kept only because the monitor is on it, and collapsing them is a change to a URL an external
  system polls, which is the same class of unobservable-consumer risk this ADR is about.

### Neutral

- Nothing about the response bodies changes, so a keyword-matching monitor rule keeps working.
- Once the monitor is on `/health/ready`, `/health` can be retired — that removal is safe only when
  we can confirm nothing else polls it (Railway's dashboard setting included).

## Alternatives considered

### Option A — Fold `CanConnectAsync` into `/health`

The ticket's own second suggestion, and the only one implementable from the repo. Rejected: see
Context. Without knowing Railway's healthcheck path we would be changing the semantics of an
endpoint that may gate container restarts, in a repo where merge deploys unattended, and the failure
mode (blip → restart → fail-fast boot abort) is worse than the bug. The QA plan's own smoke step
polls `/health/live` and expects "not a database-disconnected response", which is a second consumer
whose expectation this option would break.

### Option B — Make `/health` DB-aware but tolerant (cache the result, fail only after N consecutive failures)

Rejected as an in-process re-implementation of exactly what an uptime monitor's consecutive-failure
threshold already does, added to the one endpoint whose consumers we cannot enumerate. More code,
same unobservable-consumer risk.

### Option C — Delete `/health` and force every consumer onto the split

Rejected for now: an endpoint an external system polls cannot be removed from inside the repo
without first confirming what polls it. It becomes available once the monitor is repointed (see
Consequences § Neutral).

### Option D — Have the readiness probe run a real query instead of `CanConnectAsync`

Rejected: the #226 shape it would catch (connection fine, schema stale) is already closed by ADR
0016's boot-time drift guard — the process never starts on a stale schema. A per-poll query buys
nothing today and costs a database round trip on every poll.

## References

- Tickets: [#390](https://github.com/neboxdev/complidrop/issues/390),
  [#226](https://github.com/neboxdev/complidrop/issues/226) (the outage shape),
  [#386](https://github.com/neboxdev/complidrop/issues/386) (backend Sentry — the other half of the
  same blindness)
- ADRs: [0016](0016-apply-ef-migrations-on-startup.md) (fail-fast boot + the drift guard, and the
  Option B that first named "the existing UptimeRobot/Railway probe")
- Code: `api/CompliDrop.Api/Program.cs` (§ Health endpoints),
  `api/CompliDrop.Api.Tests/HealthProbeTests.cs`,
  `api/CompliDrop.Api.Tests/TestHelpers/SystemConnectionFaultInterceptor.cs`
- Docs: `README.md` § Health probes and monitoring, `docs/qa/manual-testing-plan.md` (launch
  decision — the external repoint)
