#!/usr/bin/env node
// SessionStart hook: inject git/issue/worklog context into the session.
// Fails silently — never crash the session start.

import { execSync } from "node:child_process";
import { existsSync, readFileSync, readdirSync } from "node:fs";

const safeRun = (cmd) => {
  try {
    return execSync(cmd, { encoding: "utf8", stdio: ["ignore", "pipe", "ignore"] }).trim();
  } catch {
    return "";
  }
};

const sections = [];

// --- Git ----------------------------------------------------------------
const branch = safeRun("git rev-parse --abbrev-ref HEAD");
if (branch) {
  const status = safeRun("git status --short");
  const log = safeRun("git log --oneline -5");
  sections.push(
    `## Git\n\nBranch: \`${branch}\`\n\nStatus:\n${status ? "```\n" + status + "\n```" : "_clean_"}\n\nRecent commits:\n\`\`\`\n${log}\n\`\`\``
  );
}

// --- GitHub issues (if gh is available) --------------------------------
const ghAvailable = safeRun("gh --version") !== "";
let issuesBlock = "";
if (ghAvailable) {
  const raw = safeRun("gh issue list --state open --limit 10 --json number,title,labels");
  if (raw) {
    try {
      const list = JSON.parse(raw);
      if (list.length) {
        issuesBlock =
          "## Open issues (top 10)\n\n" +
          list
            .map((i) => {
              const labels = (i.labels || []).map((l) => l.name).join(", ");
              return `- #${i.number} ${i.title}${labels ? " [" + labels + "]" : ""}`;
            })
            .join("\n");
      }
    } catch {}
  }
}

// --- Local tickets fallback --------------------------------------------
if (!issuesBlock) {
  const list = (dir) => {
    try {
      if (!existsSync(dir)) return [];
      return readdirSync(dir).filter((f) => f.endsWith(".md")).slice(0, 10);
    } catch {
      return [];
    }
  };
  const inProgress = list("docs/tickets/in-progress");
  const backlog = list("docs/tickets/backlog");
  if (inProgress.length || backlog.length) {
    issuesBlock = `## Local tickets\n\n**In progress:**\n${inProgress.map((f) => `- ${f}`).join("\n") || "_(none)_"}\n\n**Backlog (top 10):**\n${backlog.map((f) => `- ${f}`).join("\n") || "_(none)_"}`;
  }
}
if (issuesBlock) sections.push(issuesBlock);

// --- My in-progress tickets (GitHub mode only) ------------------------
if (ghAvailable) {
  const raw = safeRun(`gh issue list --state open --label in-progress --assignee @me --limit 5 --json number,title`);
  if (raw) {
    try {
      const list = JSON.parse(raw);
      if (list.length) {
        sections.push(
          "## My in-progress tickets\n\n" +
            list.map((i) => `- #${i.number} ${i.title}`).join("\n")
        );
      }
    } catch {}
  }
}

// --- WORKLOG.md tail ---------------------------------------------------
if (existsSync("WORKLOG.md")) {
  try {
    const content = readFileSync("WORKLOG.md", "utf8");
    const lines = content.trim().split("\n");
    const tail = lines.slice(-30).join("\n").trim();
    if (tail) sections.push(`## WORKLOG.md (tail)\n\n\`\`\`\n${tail}\n\`\`\``);
  } catch {}
}

const additionalContext = sections.join("\n\n") || "_(no context available)_";

process.stdout.write(
  JSON.stringify({
    hookSpecificOutput: {
      hookEventName: "SessionStart",
      additionalContext,
    },
  })
);
