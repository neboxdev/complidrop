#!/usr/bin/env node
// PostToolUse hook: append one JSON line per tool call to .claude/logs/activity.jsonl.
// Async, non-blocking. Fails silently.

import { readFileSync } from "node:fs";
import { appendFile, mkdir } from "node:fs/promises";
import { dirname } from "node:path";

const LOG = ".claude/logs/activity.jsonl";

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
  } catch {
    // fail silently
  }
})();
