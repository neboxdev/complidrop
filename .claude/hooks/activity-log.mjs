#!/usr/bin/env node
// PostToolUse hook: append one JSON line per tool call to .claude/logs/activity.jsonl.
// Async, non-blocking. Fails silently.

import { readFileSync, statSync, writeFileSync } from "node:fs";
import { appendFile, mkdir } from "node:fs/promises";
import { dirname } from "node:path";

const LOG = ".claude/logs/activity.jsonl";

// Keep the log bounded: over MAX bytes, keep the newest KEEP bytes aligned to a
// line boundary. Fails silently like everything here.
const MAX_BYTES = 5 * 1024 * 1024;
const KEEP_BYTES = 2 * 1024 * 1024;
const truncateIfHuge = () => {
  try {
    if (statSync(LOG).size <= MAX_BYTES) return;
    const content = readFileSync(LOG, "utf8");
    let tail = content.slice(-KEEP_BYTES);
    const nl = tail.indexOf("\n");
    if (nl > -1) tail = tail.slice(nl + 1);
    writeFileSync(LOG, tail);
  } catch {
    // fail silently
  }
};

(async () => {
  try {
    const raw = readFileSync(0, "utf8");
    if (!raw) return;
    const payload = JSON.parse(raw);
    const tool = payload.tool_name || "?";
    const input = payload.tool_input || {};
    let target = "";
    if (typeof input.file_path === "string") target = input.file_path;
    else if (typeof input.path === "string") target = input.path;
    else if (typeof input.command === "string") target = input.command.slice(0, 200);
    const entry = {
      ts: new Date().toISOString(),
      session: payload.session_id || "",
      tool,
      target,
    };
    await mkdir(dirname(LOG), { recursive: true });
    await appendFile(LOG, JSON.stringify(entry) + "\n");
    truncateIfHuge();
  } catch {
    // fail silently
  }
})();
