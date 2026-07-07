#!/usr/bin/env node
// SessionEnd hook: append session-end marker + activity count + changed files to
// .claude/session-log.md (gitignored), keeping the tracked WORKLOG.md clean so it never
// dirties the working tree. Fails silently.

import { execSync } from "node:child_process";
import { existsSync, readFileSync, statSync, writeFileSync } from "node:fs";
import { appendFile } from "node:fs/promises";

// Keep the gitignored log bounded: over MAX bytes, keep the newest KEEP bytes,
// trimmed to the next session-marker boundary. Fails silently like everything here.
const MAX_BYTES = 512 * 1024;
const KEEP_BYTES = 256 * 1024;
const truncateIfHuge = (path) => {
  try {
    if (!existsSync(path) || statSync(path).size <= MAX_BYTES) return;
    const content = readFileSync(path, "utf8");
    let tail = content.slice(-KEEP_BYTES);
    const marker = tail.indexOf("### Session ended");
    if (marker > 0) tail = tail.slice(marker);
    writeFileSync(path, "<!-- older entries truncated -->\n\n" + tail);
  } catch {
    // fail silently
  }
};

const safeRun = (cmd) => {
  try {
    return execSync(cmd, { encoding: "utf8", stdio: ["ignore", "pipe", "ignore"] }).trim();
  } catch {
    return "";
  }
};

(async () => {
  try {
    const raw = readFileSync(0, "utf8");
    let payload = {};
    try {
      payload = raw ? JSON.parse(raw) : {};
    } catch {}
    const reason = payload.reason || "exit";

    let activityCount = 0;
    if (existsSync(".claude/logs/activity.jsonl")) {
      try {
        const content = readFileSync(".claude/logs/activity.jsonl", "utf8");
        const session = payload.session_id || "";
        if (session) {
          activityCount = content
            .split("\n")
            .filter((line) => line && line.includes(`"session":"${session}"`)).length;
        }
      } catch {}
    }

    const changed = safeRun("git status --short");
    const branch = safeRun("git rev-parse --abbrev-ref HEAD") || "?";
    const ts = new Date().toISOString();

    const entry = `\n---\n\n### Session ended ${ts}\n\n- Branch: \`${branch}\`\n- Reason: ${reason}\n- Tool calls (this session): ${activityCount}\n- Changed files:\n\`\`\`\n${changed || "(none)"}\n\`\`\`\n`;

    await appendFile(".claude/session-log.md", entry);
    truncateIfHuge(".claude/session-log.md");
  } catch {
    // fail silently
  }
})();
