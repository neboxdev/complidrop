// Next.js 16 client instrumentation (App Router). This file is the SDK-expected
// entry point for browser-side Sentry init — Next runs it after the document
// loads but before React hydration, and the Sentry build plugin reads it at
// build time. See node_modules/next/dist/docs/01-app/03-api-reference/
// 03-file-conventions/instrumentation-client.md.
//
// PII scrubbing (beforeSend), the dev / no-DSN no-op gating, and the
// conservative sample rates all live in the shared commonInitOptions so the
// browser, server, and edge runtimes can never drift. Session Replay is
// deliberately NOT enabled — a certificate of insurance on screen must never be
// recorded. See ADR 0036.
import * as Sentry from "@sentry/nextjs";
import { commonInitOptions } from "./lib/sentry/options";

Sentry.init(commonInitOptions());

// Ties client-side App Router navigations to the correct Sentry trace, so a
// navigation that precedes an error is part of the same transaction.
export const onRouterTransitionStart = Sentry.captureRouterTransitionStart;
