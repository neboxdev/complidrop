"use client";

import posthog from "posthog-js";
import type { CaptureResult, Properties } from "posthog-js";
import { sanitizeUrl } from "./sentry/scrub";

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

/** Bounds a pathological/cyclic property bag, mirroring `sentry/scrub.ts`'s own cap. */
const MAX_DEPTH = 8;

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

/**
 * Rewrite every URL-valued string in a property bag through {@link sanitizeUrl}
 * — the SAME function the Sentry scrubber uses, imported rather than mirrored.
 * Two copies of a redaction rule drift, and this repo already refuses that
 * shape elsewhere (the `ContactEmail` mirror is pinned by a shared corpus for
 * exactly this reason).
 *
 * Recursive because autocapture nests `attr__href` inside `$elements`; once a
 * key is URL-bearing everything under it is treated as such.
 */
function sanitizeUrlsDeep(value: unknown, inUrlKey: boolean, depth: number): unknown {
  if (depth > MAX_DEPTH) return value;
  if (typeof value === "string") return inUrlKey ? sanitizeUrl(value) : value;
  if (Array.isArray(value)) {
    return value.map((item) => sanitizeUrlsDeep(item, inUrlKey, depth + 1));
  }
  if (isRecord(value)) {
    const out: Record<string, unknown> = {};
    for (const [key, val] of Object.entries(value)) {
      out[key] = sanitizeUrlsDeep(val, inUrlKey || URL_BEARING_KEY.test(key), depth + 1);
    }
    return out;
  }
  return value; // numbers / booleans / null / undefined pass through
}

/**
 * `before_send` runs on the fully-assembled event immediately before transmit,
 * and it sees `$set` / `$set_once` as well as `properties` — which is where the
 * `$initial_current_url` family lives. (`sanitize_properties` is the older hook
 * for this; the installed SDK marks it `@deprecated` and logs an error on every
 * event that uses it, and it is handed `properties` alone.)
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
