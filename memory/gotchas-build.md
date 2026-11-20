---
name: fa-reel-gotchas-build
description: "Build only in ~/src/fa.reel — the iCloud clone is context files only and hangs the toolchain. Every dotnet command needs MSBuildEnableWorkloadResolver=false, and stale dotnet watch processes from an earlier session race every build."
type: gotcha
last_verified: 2026-09-11
---

# Gotchas — environment, build and the dev loop

- **Two clones, one repo, and only one of them builds.** Code, builds, tests and the dev loop happen in
  `~/src/fa.reel`; the copy under `FrostAura Global/` is the context-file home. On 2026-09-09 fileproviderd took `git
  status` on the iCloud tree past two minutes, a 40 MB Docker context to 16 minutes, and Vite to 137 s before it hung
  on a file close — the clone starts Vite in under a second. The two clones exchange commits by pulling from each
  other's local path, so **after committing in one, pull in the other or they drift**.
- **Every `dotnet` command in `~/src` needs `MSBuildEnableWorkloadResolver=false`** — the installed workload set has
  missing manifests and fails the restore otherwise. `dotnet format` takes `fa.reel.slnx` at the repo root.
- **`dotnet watch` processes from an EARLIER session survive and keep rebuilding.** Three outlived their session and
  raced every test build for the API's `obj` directory (MSB4018 on static web assets); plain `kill` did not end them,
  `kill -9` did. Run `pgrep -fl 'dotnet watch'` before any build.
- **Never run the API under `dotnet watch`; use plain `dotnet run` and restart it yourself after a merge** (the rule
  is in [`../CLAUDE.md`](../CLAUDE.md)). *The symptom to recognise:* with hot reload on, a merge that adds an enum
  value or a class stops at a "Restart is needed … Yes/No" prompt waiting on stdin forever while the old binary keeps
  serving; with `--no-hot-reload`, every restart loses the port race to its own predecessor and dies on "address
  already in use" — again with the stale binary serving, and the new one having already applied its migrations. Two
  backtests ran against the wrong build before this was caught. Confirm the listener's start time postdates the merge:
  `ps -o lstart -p $(lsof -tnP -iTCP:5054 -sTCP:LISTEN)`.
- **`dotnet run` ignores `ASPNETCORE_URLS`** — `launchSettings.json` pins 5054, the founder's live port. Any other
  slot must pass `--no-launch-profile --urls http://127.0.0.1:<port>` **and** override the connection string to a copy
  database.
- **A worktree's migration can reach the LIVE database.** The test fixture only touches `reel_test_*`, but anything
  that starts the API from a worktree with the repo `.env` migrates `reel` under the *branch's* migration id; when the
  integrator re-scaffolds that migration on `main`, the live API then dies at startup creating tables that already
  exist. The fix used was to rename the history row to main's id after verifying the DDL was byte-identical.
  Prevention: never start the API from a worktree.
- **A QA slot left with `Background__*` enabled processes the copy database's job queue against the real providers.**
  One QA walk spent hundreds of real extraction calls that way. Every slot recipe turns background jobs off, truncates
  the outbox and deletes pending jobs after copying the DB. `/health` is not proxied by Vite, and scripting Chromium
  in a worktree needs `npx playwright install chromium` there first.
- **`git branch --merged main` matches a branch created from main seconds ago.** A "remove merged, clean worktrees"
  sweep deleted two agents' fresh worktrees from under them. Prune worktrees **before** launching agents, never
  alongside them.
- **The dev-link QA session expires after roughly 85 minutes**, and **restarting the API drops it too** — the app
  falls to the landing page. Re-POST `/api/auth/dev/link` from the page origin and reload. Trakt's bot protection also
  blocks the Electron browser pane on the *authorize* page, so a real OAuth sign-in must happen in a normal browser;
  the pane works once a session cookie exists.
- **Never leave the compose `backend` container running beside the dev loop.** Both connect to the same Postgres and
  share the job queue, so a month-old image silently claimed training and feed builds: it kept writing artifacts at an
  old schema while the new binary's sweep kept superseding them, and the stats tables stayed at zero while snapshots
  appeared. Run the compose stack *or* the dev loop, never both: before a dev loop, stop the compose `backend`,
  `frontend` and `gateway` and leave only `postgres` up. The local compose file uses `restart: always` on purpose — `unless-stopped`
  leaves the stack dead after Docker Desktop is quit — and the two production files keep `unless-stopped` equally
  deliberately, so do not "harmonize" them.
