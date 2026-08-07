"use client";

import posthog from "posthog-js";
import type { CaptureResult, Properties } from "posthog-js";
import { REDACTED, sanitizeUrl } from "./sentry/scrub";

let initialized = false;

/**
 * PostHog property keys whose value is (or embeds) a URL or a path.
 *
 * WHY THIS EXISTS (ADR 0037 Amendment 1). The vendor-portal token IS a path
 * segment — `/portal/{token}` — and it is a bearer credential: whoever holds it
 * can upload to that link. PostHog runs with `capture_pageview` /
 * `capture_pageleave` on, and it puts `window.location` into a whole family of
 * properties, so an unsanitized portal visit ships the token to a third-party
 * analytics store on every event. Sentry has redacted exactly this string since
 * #356 (`sanitizeUrl`); this is the same rule on the other vendor.
 *
 * The list is a SUBSTRING rule rather than an enumeration on purpose — the names
 * PostHog emits were read off real payloads (jsdom + an MSW interceptor, see
 * `analytics.test.ts`) rather than guessed, and they come in families the SDK
 * grows by prefix:
 *
 *   `$current_url` · `$pathname` · `$referrer`                (every event)
 *   `$session_entry_url` / `_pathname` / `_referrer`          (every event)
 *   `$initial_current_url` / `_pathname` / `$initial_referrer` (`$set_once`)
 *   `$prev_pageview_pathname` · `$external_click_url`          (pageleave / autocapture)
 *
 * A future `$…_url` / `$…_pathname` / `$…_referrer` is covered the day it ships,
 * which an explicit list would not be. `$host` / `$referring_domain` carry no
 * path and so cannot carry the token; they are matched by nothing here and that
 * is fine — over-matching is harmless, under-matching is the bug.
 *
 * Not global (`g`): a global regex carries `lastIndex` between `.test()` calls,
 * which would make the result depend on key order.
 */
const URL_BEARING_KEY = /url|path|referrer|href/i;

/**
 * Bounds a pathological/cyclic property bag. Same limit as `sentry/scrub.ts`'s
 * cap, and it fails in the same DIRECTION: past the cap a value the walker was
 * on its way to redact is replaced by `REDACTED` rather than passed through.
 * Returning it intact — what this did before #404 round 2 — made the cap an
 * escape hatch: a URL-bearing key with something nested deeply enough under it
 * left unredacted, which is the one outcome the cap must not produce.
 *
 * It is NOT the neighbour's blanket `return REDACTED`: outside a URL-bearing
 * key this walker redacts nothing anyway, so blanking those values would delete
 * ordinary deep analytics (session-replay `$snapshot_data`, nested autocapture
 * payloads) to no privacy end. Past the cap the guarantee is therefore exactly
 * the guarantee below it — everything under a URL-bearing key is unreadable —
 * and nothing more.
 */
const MAX_DEPTH = 12;

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

/**
 * KEYS are data too, and a value-only walker copies them verbatim.
 *
 * posthog-js's heatmaps extension buffers clicks in a map KEYED BY
 * `window.location.href` and flushes it through `capture()` as `$heatmap_data`
 * (`heatmaps.js`: `_capture` builds `this._buffer[url]`, `_flush` sends
 * `capture('$$heatmap', { $heatmap_data: … })`). `$heatmap_data` matches nothing
 * in {@link URL_BEARING_KEY} and neither does the URL used as the inner key, so
 * before #404 round 2 the whole tokenized URL left as an object key, intact.
 * The extension is also pinned OFF in `initAnalytics` — this is the general
 * rule, that is the belt that survives someone rewriting this function.
 *
 * Only URL-SHAPED keys are rewritten (`://`, or a leading `/`). Running every
 * key through `sanitizeUrl` would let its opaque-token net rename an ordinary
 * long property key and silently change the analytics schema — a redactor must
 * not invent property names.
 *
 * Two keys that differ only in the redacted segment collapse onto one, and the
 * later bucket wins. That is inherent to redaction, not a bug to route around:
 * the only way to keep them apart is to keep the distinction being erased.
 */
function sanitizeUrlKey(key: string): string {
  return key.includes("://") || key.startsWith("/") ? sanitizeUrl(key) : key;
}

/**
 * Rewrite every URL-valued string in a property bag through {@link sanitizeUrl}
 * — the SAME function the Sentry scrubber uses, imported rather than mirrored.
 * Two copies of a redaction rule drift, and this repo already refuses that
 * shape elsewhere (the `ContactEmail` mirror is pinned by a shared corpus for
 * exactly this reason).
 *
 * Recursive because autocapture nests `attr__href` inside `$elements`; once a
 * key is URL-bearing everything under it is treated as such. Object keys go
 * through {@link sanitizeUrlKey} for the case where the URL IS the key.
 */
function sanitizeUrlsDeep(value: unknown, inUrlKey: boolean, depth: number): unknown {
  if (depth > MAX_DEPTH) return inUrlKey ? REDACTED : value;
  if (typeof value === "string") return inUrlKey ? sanitizeUrl(value) : value;
  if (Array.isArray(value)) {
    return value.map((item) => sanitizeUrlsDeep(item, inUrlKey, depth + 1));
  }
  if (isRecord(value)) {
    const out: Record<string, unknown> = {};
    for (const [key, val] of Object.entries(value)) {
      // `inUrlKey` is decided from the ORIGINAL key: redaction rewrites the name
      // we are matching on, and a subtree's treatment must not depend on that.
      out[sanitizeUrlKey(key)] = sanitizeUrlsDeep(
        val,
        inUrlKey || URL_BEARING_KEY.test(key),
        depth + 1,
      );
    }
    return out;
  }
  return value; // numbers / booleans / null / undefined pass through
}

/**
 * `before_send` runs on the fully-assembled event immediately before transmit,
 * and it sees `$set` / `$set_once` as well as `properties` — which is where the
 * `$initial_current_url` family lives. (`sanitize_properties` is the older hook
 * for this; the installed SDK marks it `@deprecated` and logs an error on EVERY
 * event that uses it — twice, in fact, since `_calculate_set_once_properties`
 * calls it a second time with the `$set_once` bag.)
 *
 * It only wraps `capture()`, so it reaches no channel the SDK opens on its own
 * — see `initAnalytics` for the two that are closed at init instead.
 */
function redactUrlProperties(result: CaptureResult | null): CaptureResult | null {
  if (!result) return result;
  result.properties = sanitizeUrlsDeep(result.properties, false, 0) as Properties;
  if (result.$set) result.$set = sanitizeUrlsDeep(result.$set, false, 0) as Properties;
  if (result.$set_once) {
    result.$set_once = sanitizeUrlsDeep(result.$set_once, false, 0) as Properties;
  }
  return result;
}

/**
 * Never called on `/portal/*`, nor anywhere in a browsing context that has EVER
 * been there — `Providers` gates it on the pathname AND on a module-scope flag
 * (ADR 0037 Amendments 2-3). That route's URL IS a bearer credential, and
 * per-channel redaction against a dependency that keeps adding channels is a
 * promise this file cannot keep. The second half of the gate exists because what
 * this function starts is a MODULE-GLOBAL singleton that nothing here stops:
 * initialise it once anywhere in a tab and it keeps measuring that tab, so a
 * pathname-only gate lapsed the moment a soft navigation left the portal and came
 * back. Everything below is the layer for every OTHER route, which can still
 * carry a portal URL in a `$referrer` (a reminder mail, quoted free text).
 */
export function initAnalytics() {
  if (initialized || typeof window === "undefined") return;
  const key = process.env.NEXT_PUBLIC_POSTHOG_KEY;
  if (!key) return;
  posthog.init(key, {
    api_host: process.env.NEXT_PUBLIC_POSTHOG_HOST ?? "https://us.i.posthog.com",
    capture_pageview: true,
    capture_pageleave: true,
    persistence: "localStorage+cookie",
    before_send: redactUrlProperties,
    // `POST /flags/?v=2` builds `person_properties` straight from persistence
    // (`get_initial_props()` → the raw `$initial_current_url` / `$initial_pathname`
    // / `$initial_referrer` that the first `capture()` stored) and never passes
    // through `before_send`, so redaction cannot reach it. It is issued at INIT
    // by the remote-config loader and again every 5 minutes by its refresh —
    // no `identify()` in either chain, which is what round 1 got wrong. Closing
    // it costs PostHog remote configuration (web vitals, surveys, and anything
    // else configured in PostHog's settings rather than in code): a deliberate,
    // recorded trade (ADR 0037 Amendment 2). Nothing in `frontend/` reads a
    // feature flag.
    advanced_disable_flags: true,
    // The heatmaps extension ships in the default bundle and, with neither
    // `capture_heatmaps` nor `enable_heatmaps` set, enables itself from the
    // PostHog project's own dashboard toggle (`heatmaps.js`: `isEnabled` falls
    // through to `_enabledServerSide`). It buffers by `location.href` and sends
    // the map as `$heatmap_data` — a URL as an object KEY. `sanitizeUrlKey`
    // covers that shape generally; this makes the surface unreachable, so a
    // toggle flipped in a dashboard cannot re-open it.
    capture_heatmaps: false,
  });
  initialized = true;
}

export function track(event: string, properties?: Record<string, unknown>) {
  if (!initialized) return;
  posthog.capture(event, properties);
}

export function identify(userId: string, traits?: Record<string, unknown>) {
  if (!initialized) return;
  posthog.identify(userId, traits);
}

export function resetIdentity() {
  if (!initialized) return;
  posthog.reset();
}
