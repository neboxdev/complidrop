#!/usr/bin/env node
// SessionEnd hook: append session-end marker + activity count + changed files to
// .claude/session-log.md (gitignored), keeping the tracked WORKLOG.md clean so it never
// dirties the working tree. Fails silently.

import { execSync } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { appendFile } from "node:fs/promises";

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
  } catch {
    // fail silently
  }
})();
