---
paths:
  - ".claude/state/**"
---

# Rules: Merge strategy & gate

The merge gate has two sub-rules depending on the PR's base branch.

- **Task PR into an epic branch** — the main thread merges it itself
  once CI is green. No QA, no review, no user pause — see
  [#task-merge-into-the-epic-branch](#task-merge-into-the-epic-branch).
- **PR into `develop`** (epic PR or standalone PR) — `pr-reviewer`
  **clears** the merge (CI on the exact head, base current, exclusion
  list, stacked PRs) and hands back a pinned
  `gh pr merge … --match-head-commit <sha>` command; the main thread
  runs it after explicit user authorization.

`deny-subagent-merge.py` blocks `gh pr merge` and `git push` for every
subagent, by design. Dev agents never merge.

## Strategy mapping

Every PR merges with `--squash --delete-branch` — `Epic`, `Task`,
`Bug` and `Chore` alike (one atomic commit per PR; clean, revertible).
`Chore` used to `--rebase`; it was folded into squash when the
`type:docs` / `type:refactor` / `type:chore` labels merged into
`Chore` (#1163).

For epic PRs `--squash` means **the entire epic lands as a single
commit on `develop`** — clean revert, clean changelog.

A PR to `develop` with no kind label (`Epic` / `Task` / `Bug` /
`Chore`), or more than one → abort, return BLOCKED, route label cleanup
to `github-issues` before retrying.

## Task merge into the epic branch

A task PR (base = an **epic branch**, not `develop`, not `main`) gets
**no** `qa-tester` run and **no** `pr-reviewer` review. Both run once,
on the whole epic, before the epic PR merges to `develop` — see
[`epic-branch.md#branch-merge-flow`](epic-branch.md#branch-merge-flow).

- The **main thread** opens the PR and merges it itself — no
  `pr-reviewer` dispatch, no user authorization. Burdening the user
  with N approvals for N tasks defeats the consolidation; the user
  authorizes the epic merge once at the end.
- **Gate: the dev sub-agent's own verify skill passed, and CI is
  green.** `gh pr checks <N>` must show every required check as `pass`.
  CI failures route to the owning dev sub-agent. Pending checks are
  polled every ~30s up to 10 min, then escalated.
- Command: `gh pr merge <N> --squash --delete-branch --match-head-commit <sha>`.
- The [#exclusion-list](#exclusion-list) does **not** apply at this
  tier. A task touching migrations or Mongo data scripts merges into
  the epic branch like any other. The epic PR that carries a Mongo data
  script to `develop` is still excluded and merged by the user; one that
  carries only EF migrations goes through the normal authorized merge.
- After the merge, the orchestrator fast-forwards the local epic branch
  (`git pull --ff-only`, never a hard reset), deletes the task branch
  best-effort, tears down its compose harness, removes its worktree,
  and merges the fresh epic-branch tip into any in-flight sibling task
  branches (merge + normal push — never rebase and force-push).
- **Leave the task issue open.** It is not done until the epic's QA
  passes. The epic PR body carries one `Fixes #<task>.` line per task,
  so the tasks close when the epic merges to `develop`.
- `notion-docs` is **not** dispatched per task. It runs once after the
  epic ships to `develop` (main thread; see Merge dispatch step 5).

## Authorized merge

When `pr-reviewer` returns ✅ READY FOR MERGE on a PR whose base is
`develop` (epic-level PR with `head = <epic-branch>`, or a standalone
issue's PR), the orchestrator reports the PR URL and **waits for
explicit same-turn merge authorization** — a phrase like "merge it",
"go ahead", "approved, merge". Historical approval from earlier in the
conversation does not count.

When authorized:

### Pre-merge CI gate

`gh pr checks <N>` must show every required check as `pass`. If any
check is `fail` or still `pending`, the orchestrator does NOT proceed
to merge:

- **`fail`:** read the failing job's log (`gh run view <id> --log`),
  diagnose the root cause, route the fix to the owning dev sub-agent.
  After the fix is pushed, CI re-runs automatically; orchestrator
  waits for green before merging. The user's earlier authorization
  carries through a single fix cycle. A second CI failure on the same
  PR warrants surfacing back to the user for judgment.
- **`pending`:** wait. Poll with `gh pr checks <N>` every ~30s up to
  10 min before escalating. Never merge on an unresolved status.
- **`pass`:** continue.

### Merge dispatch

1. Orchestrator re-dispatches `pr-reviewer` in `mode: merge` with the
   explicit authorization phrase.
2. `pr-reviewer` checks the [#exclusion-list](#exclusion-list). If the
   PR hits any exclusion, it returns BLOCKED with the reason — the
   user merges those manually.
3. Otherwise it confirms CI on the exact head, that the base hasn't
   moved under the green run, and that no PRs are stacked on the
   branch, and returns CLEARED TO MERGE with the command
   `gh pr merge <n> --squash --delete-branch --match-head-commit <sha>`.
4. The **main thread** runs that command, confirms the merge landed
   (`gh pr view <n> --json state,mergeCommit`), fast-forwards the
   local base (`git pull --ff-only`, never a hard reset), deletes the
   local branch best-effort and removes the worktree. A failed
   fast-forward (dirty tree, local commits) is a ⚠️ warning to the
   user, not a rollback — the merge already landed.
5. Orchestrator runs `notion-docs` (update mode) on the main thread (it
   needs the browser and Notion tools). For an epic merge it covers all
   tasks in the consolidated commit.

## Exclusion list

The agent never merges these — user does it manually:

- PRs whose base branch is `main` (any release into the main line).
- PRs that add or modify MongoDB data-mutation scripts (bulk fix-ups,
  seed overrides, reprocessing jobs under `backend/**/Scripts/` or
  `backend/**/DataMigrations/`, or `db.*.update`/`bulkWrite`/
  `deleteMany` calls in MongoContext / Services). Applies to PRs into
  `develop` only — task PRs into an epic branch are exempt (see
  [#task-merge-into-the-epic-branch](#task-merge-into-the-epic-branch)).
- PRs that add or modify a **one-shot data-mutation command**: any routine
  dispatched from the CLI or from startup that writes, renames, drops or
  bulk-modifies stored data, in either database, wherever the file sits. This
  bullet is deliberately phrased by what the code *does*, because the
  bullet above is phrased by directory and by method name — and a command
  registered in `backend/**/Infrastructure/Cli/**` matches neither while being
  exactly as destructive. Two already exist:
  `RunDropLegacyTrainingCollectionsAsync` (#847) drops two collections, and
  the rename command in #1033 moves a third. Same — `develop` only.
- Any PR where the user has said in the current turn "I'll merge this
  one myself".

**EF Core migrations are not excluded** (maintainer decision 2026-11-03,
re-confirmed 2026-11-14 in #2124). A PR into `develop` whose diff touches
`backend/**/Migrations/**` goes through the normal
[#authorized-merge](#authorized-merge): explicit same-turn authorization,
and `pr-reviewer` names the migration files in its clearance so the user
knows a schema change is landing.

## Skip-merge-gate scenarios

Skip this gate only if no PR was produced (doc-only commits pushed
directly, out-of-band infra tweaks). The agent never merges to
`develop` or `main` without a fresh, in-turn go-ahead.
