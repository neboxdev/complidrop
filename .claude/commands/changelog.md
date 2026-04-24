---
description: Regenerate CHANGELOG.md from conventional commits using git-cliff
---

Regenerate `CHANGELOG.md` from the project's conventional commits.

## Process

1. Check `git-cliff --version` is available. If missing, point user at https://github.com/orhun/git-cliff/releases and stop.
2. Verify `cliff.toml` exists at repo root. If missing, stop and tell the user the installer left a default config.
3. Run:
   ```bash
   git-cliff --output CHANGELOG.md
   ```
4. Show the diff of `CHANGELOG.md`. If the user wants a release tag bumped, instruct them to:
   ```bash
   git-cliff --tag v<x.y.z> --output CHANGELOG.md
   ```
5. Do NOT commit automatically — let the user review.

## Rules

- Never edit `CHANGELOG.md` by hand — it's generated. Edit `cliff.toml` template if formatting is wrong.
- If commits don't follow Conventional Commits, they'll be filtered out (per `cliff.toml`). Note this and consider amending — but never amend already-pushed commits without checking with the user.
