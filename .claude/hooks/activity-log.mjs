#!/usr/bin/env node
// PostToolUse hook: append one JSON line per tool call to .claude/logs/activity.jsonl.
// Async, non-blocking. Fails silently.

import { closeSync, openSync, readFileSync, readSync, statSync, writeFileSync } from "node:fs";
import { appendFile, mkdir } from "node:fs/promises";
import { dirname } from "node:path";

const LOG = ".claude/logs/activity.jsonl";

// Keep the log bounded: over MAX bytes, keep the newest KEEP bytes aligned to a
// line boundary. Byte-accurate fd tail-read (never loads a huge legacy file whole;
// a partial multibyte char at the buffer start is discarded with the first line).
// This hook runs async per tool call, so a concurrent append can race the
// truncation rewrite and lose a line — accepted: this is a best-effort local log.
// Fails silently like everything here.
const MAX_BYTES = 5 * 1024 * 1024;
const KEEP_BYTES = 2 * 1024 * 1024;
const truncateIfHuge = () => {
  try {
    const size = statSync(LOG).size;
    if (size <= MAX_BYTES) return;
    const fd = openSync(LOG, "r");
    const buf = Buffer.alloc(KEEP_BYTES);
    const read = readSync(fd, buf, 0, KEEP_BYTES, size - KEEP_BYTES);
    closeSync(fd);
    let tail = buf.subarray(0, read).toString("utf8");
    const nl = tail.indexOf("\n");
    if (nl >= 0) tail = tail.slice(nl + 1);
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
