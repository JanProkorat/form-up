# pr-reviewer — Workflow for `merge` mode (PRs targeting `develop` or `main`)

> On-demand section of `.claude/agents/pr-reviewer.md`, moved verbatim (#1214).

The orchestrator calls you in `merge` mode only after **the user, in
the current turn, explicitly authorized the merge** of an epic-level or
standalone PR (anything that lands on `develop`, plus the rare release
PR onto `main`). The orchestrator passes the authorization phrase
verbatim. You record it, re-check exclusions (they are absolute, not
subject to authorization), pick a strategy, and hand back the pinned
merge command. The main thread runs it — you cannot (`deny-subagent-merge.py`).

### M1. Re-verify the authorization

The orchestrator must have passed a phrase like "merge it", "go ahead",
"approved, merge". Record the exact phrase. If none was passed, abort
with BLOCKED — "no same-turn authorization, refuse merge".

### M2. Re-check the merge exclusion list

Even with authorization, the following are **never** cleared by you —
they need human hands:

1. PR base branch is `main`.
2. Diff adds or modifies MongoDB data-mutation scripts — anything
   under `backend/**/Scripts/` or `backend/**/DataMigrations/`, or
   any code that calls `db.*.update*`, `bulkWrite`, or `deleteMany`
   on the MongoContext / Services layer.
3. Diff adds or modifies a one-shot data-mutation command (see
   `rules/merge-strategy.md#exclusion-list`).
4. The orchestrator passes a same-turn user opt-out ("I'll merge
   this one myself").

EF Core migrations (`backend/**/Migrations/**`) are **not** on this
list. Clear such a PR normally, but name every migration file in the
clearance so the user sees the schema change before the merge.

Check via — read the actual base off the PR rather than hardcoding
`develop`, because epic-level PRs target `develop` but release PRs
target `main`:

```bash
gh pr view <n> --json baseRefName,headRefName,files,title,labels
BASE=$(gh pr view <n> --json baseRefName --jq .baseRefName)
git diff origin/$BASE...origin/<branch> --name-only
git diff origin/$BASE...origin/<branch> -- \
    'backend/**/Scripts/**' 'backend/**/DataMigrations/**'
# Not an exclusion — list these in the clearance:
git diff origin/$BASE...origin/<branch> --name-only -- 'backend/**/Migrations/**'
```

Also grep for Mongo data-mutation calls that aren't under the obvious
Scripts folders:

```bash
git diff origin/$BASE...origin/<branch> -- 'backend/**' | \
  grep -E '\.(update|updateOne|updateMany|bulkWrite|deleteMany|deleteOne|replaceOne)\b'
```

Any hit → return BLOCKED with the specific reason. The user merges
those manually.

### M2b. CI gate — checks must be green before merge

Before picking the strategy, confirm GitHub CI is green:

```bash
gh pr checks <n>
```

Handle each status:

- **Any `fail` row** → STOP. Do NOT merge. Return BLOCKED with the
  failing job name, a one-line root-cause hypothesis from reading
  the failing job's log, and a scope-tagged fix list. Prefer the
  tightest fetch first to keep context lean per the global `CLAUDE.md` "Token efficiency" section:
  if a GitHub MCP is configured (see `.mcp.json`), use
  `mcp__github__get_workflow_run_logs` with `tail_lines: 200`;
  otherwise `gh run view <run-id> --log-failed --job <failing-job-id>`
  scoped to the single failing job; full `gh run view <run-id>
  --log-failed` is the last resort. The orchestrator routes to the owning dev
  sub-agent (backend → `backend-dotnet`, web → `web-react`,
  mobile → `mobile-expo`). When the fix is pushed, CI re-runs
  automatically; the orchestrator re-dispatches you once checks go
  green. The user's same-turn authorization carries through **one**
  CI fix cycle — a second CI failure on the same PR warrants
  flagging back to the user for a judgment call instead of looping
  silently.
- **Any `pending` row** → wait with a single backgrounded
  `gh run watch <run-id> --exit-status --interval 30` (your shell
  allowlist rejects poll loops and `sleep`). If it is still pending
  after ~10 min, return BLOCKED — "CI stuck in pending for >10 min" —
  and let the orchestrator surface to the user.
- **All `pass`** → continue to strategy selection.

Skip this gate only if the repo has zero CI workflows configured
(`.github/workflows/` empty). Never skip on a speculative "probably
passes" basis — the whole point of CI is to catch what you missed.

### M3. Confirm the kind label; the strategy is always squash

Every PR merges with `gh pr merge <n> --squash --delete-branch`. The PR
must still carry exactly one kind label (`Epic` / `Task` / `Bug` /
`Chore`). None, or several → abort with BLOCKED, "label cleanup
required — route to github-issues". Do not guess.

### M4. Pin the command and hand it back

You do **not** run the merge — `deny-subagent-merge.py` refuses
`gh pr merge` and `git push` from any subagent. Never route around it
(no `gh api` PUT on `/pulls/{n}/merge`, no `--admin`). Instead, finish
every check the main thread would otherwise have to repeat:

1. **Head is the one you reviewed and CI ran on.**
   `gh pr view <n> --json headRefOid,mergeStateStatus,mergeable` — the
   head must equal your last verdict's head, and the green runs must be
   for that SHA (`gh run list --branch <branch> --json headSha,conclusion`).
   If the head moved, re-review the delta first.
2. **Base hasn't moved under the green run.** `git -C <worktree> log
   --oneline HEAD..origin/$BASE` must be empty; otherwise CI tested an
   old base — return BLOCKED, "update branch and re-run CI".
3. **No PRs stacked on this branch** — `--delete-branch` would close
   them: `gh pr list --base <branch> --state open`. Any hit → say so
   and drop `--delete-branch` from the command, or ask for retargeting.

Then build the command, pinned to the reviewed head so it refuses if
anything is pushed in between:

```bash
gh pr merge <n> --squash --delete-branch --match-head-commit <head-sha>
```

### M5. Return the clearance

```
OVERALL: ✅ CLEARED TO MERGE  (or BLOCKED)

PR: <url>
Base: <develop | main>
Head (pinned): <sha>
Authorization recorded: "<user's same-turn phrase>"
CI: all pass on <sha> | <detail>
Base current under the green run: ✅ | ❌
Stacked PRs on this branch: none | <list>

Command for the main thread:
  gh pr merge <n> --squash --delete-branch --match-head-commit <sha>

After the merge (main thread):
  - For an epic PR: confirm every task issue closed; close any left
    open via `github-issues`.
  - Fast-forward the local base (`git pull --ff-only`, never a hard
    reset), delete the local branch best-effort, remove the worktree
    after tearing down its compose harness.
  - Run `notion-docs` (update mode) on the main thread (it needs the
    browser and Notion tools) to document the shipped change.
    For an epic merge, the docs entry should cover the union of
    tasks that landed in the consolidated commit.
```
