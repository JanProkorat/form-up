---
name: ship-epic
description: GitHub epic orchestration via epic-branch model — design-review + dispatch tasks off epic, merge tasks on green CI, QA + review the epic PR once, same-turn auth merge, notion-docs. Invoke on "ship epic #N". Orchestrator-only.
disable-model-invocation: true
argument-hint: "<epic-issue-number-or-URL>"
---

# ship-epic — drive a GitHub epic to merge end-to-end

This skill is the named single entry-point for the project's
epic-to-PR lifecycle. The full contract (dev/QA/review/merge gates,
label taxonomy, branch conventions, merge exclusions, notion-docs
handoff) already lives in `.claude/CLAUDE.md` — this skill does not
redefine any of it. It **sequences** those gates reliably so the same
orchestration runs the same way every time.

The skill follows the project's **epic-branch model** (see
`.claude/CLAUDE.md` → "Epic-branch model"):

```
develop                                ← only the consolidated epic merges in
 └── feature/<epic-N>-<short>          ← the epic branch (created in Phase 0)
      ├── feature/<C1>-<short>         ← task branches (Phase 1)
      ├── fix/<C2>-<short>             ← merge into the epic branch on green CI,
      └── chore/<C3>-<short>              no QA, no review, no user pause
```

Task PRs merge into the **epic branch**, not `develop`. Only when
every task has landed on the epic branch does the skill run `qa-tester`
and `pr-reviewer` — once, on the whole epic — then open one
consolidated PR against `develop` and pause for explicit same-turn
user authorization (rule 8b). This is what the user means by "I don't
want a half-shipped epic on develop".

## Session layout (maintainer decision 2026-11-20)

The session that runs this skill is the **epic coordinator** only. Every
task runs in its **own child session**, started with
`mcp__ccd_session__start_session` (`initiation: "user_asked"`,
`context: "fresh"`, `use_worktree: true`, `wake_when_done: true`,
`name: "task-<N>-<short>"`). Children nest under the coordinator in the
sidebar on their own; rename the coordinator `Epic #<N> - <short name>`
(`set_session_title`, `"self"`).

| Coordinator (this session) | Task session (one per child) |
|---|---|
| Phase 0, 0a, 0b; epic branch; state file; test-guide page | Phase 1a–1d for its one task: branch, design review, dev agent, PR, merge on green CI |
| Starts each child as soon as its `depends_on` children are merged | Its guide section (`in-progress` → `ready` + steps) and its time clock |
| Tells in-flight siblings (`SendMessage`) when the epic branch moves | Merges the epic tip into its own branch before its own merge |
| Phase 2, 2b, 3 — epic QA, guide walk, epic PR, review, merge, docs | Never edits the state file; never runs `qa-tester`, `pr-reviewer`, `notion-docs` |

Start every child whose dependencies are met **in the same message**, so
they run in parallel, same package included. Each brief is self-contained
(the child has none of this conversation) and names any file a parallel
sibling also changes, with the conflict plan — e.g. two EF migrations:
the one that merges second merges the epic tip and regenerates its
migration. Briefs also tell the child to check `pgrep -fl "dotnet test"`
before a full local Testcontainers run and to rely on CI's full suite
when another run is going.

## When to invoke

- "Ship epic #N" / "implement epic <URL>" / "drive epic #N to green"
- The user hands over a single GitHub epic issue whose body enumerates
  child issues (the pattern from epic #16, #17, weekly-check-ins,
  Photos epic, questionnaire flow).
- You're about to orchestrate ≥2 child issues and the work will span
  more than one dev sub-agent.

## When NOT to invoke

- Single-issue work — just route directly to the owning dev sub-agent
  and run the usual gates manually. The skill is overhead for one PR.
- Ad-hoc spike with no issue backing it — use `spike/<date>-<desc>`
  per the branch conventions and skip the gates.
- The user is mid-epic and asks for one specific correction — finish
  that correction through the normal routing; don't restart the skill.

## What this skill does NOT do

- Does not merge the epic PR without explicit in-turn authorization.
  The merge gate in `.claude/CLAUDE.md` rule 8 still applies — the
  skill pauses at READY FOR MERGE and hands back the PR URL. Task PRs
  into the epic branch need no authorization.
- Does not bypass the merge exclusion list for the epic PR to
  `develop`. An epic whose diff touches Mongo data-mutation scripts or a
  one-shot data command is still merged to `develop` by the user. EF
  migrations alone are not excluded — they go through the normal
  authorized merge.
- Does not write code itself — dev sub-agents do.
- Does not replace `github-issues`, `qa-tester`, or `pr-reviewer` — it
  calls them.

## Preconditions

Before running any phase below, confirm:

1. You have the **epic issue number or URL**.
2. Your local `develop` is up to date: `git fetch origin && git
   checkout develop && git pull --ff-only`. If the tree is dirty,
   stop and ask the user what to do. The epic branch (Phase 0) will
   be created off this fresh `develop`.
3. Docker is running (required by backend Testcontainers tests) *if*
   the epic contains any `BE` task.

Note: there is no longer a "pause at each child / batch-authorize at
the end" choice. The epic-branch model is the single mode — task
PRs merge into the epic branch on green CI, and authorization is
required only once, at the epic merge to `develop` (Phase 2b).

## State persistence (survives `/clear` and compact)

Every phase boundary writes `.claude/state/ship-epic.json` so the
orchestration can resume after a context reset. The
`reinject-state.sh` SessionStart hook reads this file on every fresh
session and surfaces it back as context.

**Write the state file at every transition:**

- After Phase 0 step 4 (epic branch created + pushed): `phase: "creating"`.
- When starting each child's task session in Phase 1: append/update
  that child's entry with `status: "in-progress"`, the `branch` it will
  create, its `session` id (`local_…`), and `pr` (null until opened).
  Only the coordinator writes this file.
- After each child merge: bump that entry to `status: "merged"`.
- When opening the epic PR (Phase 2): `phase: "epic-pr"` and capture
  the epic PR number on a new top-level field if useful.
- After the epic merge (Phase 2b): `phase: "done"`, then move on to
  Phase 3.

**Schema:** [`schemas/ship-epic-state.v1.json`](../../schemas/ship-epic-state.v1.json)
— validated by `reinject-state.sh` on every SessionStart. Required
fields: `epic_number`, `epic_branch`, `phase` (`creating |
design-reviewing | dispatching | merging-children | epic-pr | done`),
`children` array (each `{ issue, status, branch?, pr?, session?,
depends_on?, estimated_complexity?, blocked_reason? }`), `started_at`, `updated_at`
(ISO-8601). Optional `fast_path: bool` and `epic_pr: int` once those
phases are reached. Every state-file write must include the literal
`"$schema": ".claude/schemas/ship-epic-state.v1.json"` field — that's
how the validator finds the schema.

**On session start with a non-empty state file**: the reinject hook
emits the current epic + children. If `phase != "done"`, the
orchestrator picks up where it left off — usually by re-running the
loop body for the first child whose `status != "merged"`.

---

## Phase 0 — Read the epic, build the work list, and create the epic branch

Actions (orchestrator):

1. Fetch the epic via `gh issue view <N> --json
   number,title,body,labels,state` (or the URL form). Parse the body
   for child-issue references — the project convention is a checklist
   of `- [ ] #123 — title` lines or explicit `Closes #123` / `Part of
   #N` links from the children themselves.
2. Fetch each child with `gh issue view <child> --json
   number,title,labels,body,state`. Filter out already-closed children
   unless the user said "re-verify closed ones".
3. Build the work list as a plain table in the chat so the user can
   sanity-check before dispatch:

```
| # | Title | Package | Kind | Dev agent | Depends on |
|---|-------|---------|------|-----------|-----------|
| 123 | Publish nutrition week | BE | Task | backend-dotnet | — |
| 124 | Consume publish event in web | Web | Task | web-react | 123 |
| 125 | Mobile toast on publish | Mobile | Task | mobile-expo | 123 |
```

4. Decide **parallel vs sequential** per dependency:
   - Children with no unmet dependencies run in parallel, each in its
     own task session and worktree — same package included (standing
     opt-in, 2026-11-20). Name shared files and the conflict plan in
     each brief (see "Session layout").
   - A child that `Depends on` another waits for that PR to **merge
     into the epic branch** first (no longer "into `develop`" — under
     the epic-branch model, dependent siblings observe each other on
     the epic branch).

5. Present the dispatch plan to the user (one paragraph: "I'll run
   123 alone first; once merged into the epic branch, fan out 124 +
   125 in parallel; epic branch goes to develop at the end") and
   **wait for go-ahead**. Per Working Principles §5 this is a
   plan-then-execute boundary for any epic touching >5 files.

6. Create and push the **epic branch** off the latest `develop`:

```bash
git fetch origin
git checkout develop
git pull --ff-only

EPIC_N=<N>                              # e.g. 66
EPIC_SHORT=<kebab>                      # ≤20 chars from the epic title
EPIC_BRANCH="feature/${EPIC_N}-${EPIC_SHORT}"

git checkout -b "$EPIC_BRANCH" develop
git push -u origin "$EPIC_BRANCH"
git checkout develop                    # leave the main checkout on develop
```

If the push is rejected because the epic branch already exists (e.g. an
earlier session exported the design boards onto it), check it out in a
worktree and **merge** `origin/develop` into it instead. If it was cut
from another epic's branch that has since squash-merged into `develop`
(check `git merge-base --is-ancestor <its base> <that PR's head>`), the
merge may conflict everywhere. Only in a clean, dedicated worktree on
the epic branch (`read-tree --reset` discards uncommitted changes, like
a hard reset), take `develop`'s tree
(`git merge --no-commit -s ours origin/develop`,
`git read-tree -u --reset origin/develop`, commit) and cherry-pick the
branch's own epic commits back on top. Never force-push.

The epic branch is empty at this point — no commits beyond
`develop`'s tip. Sub-issue worktrees will branch off it. Record
`$EPIC_BRANCH` for the rest of the skill — every `--base`, every
worktree, every dev-agent brief uses it.

Optional but recommended: post a comment on the epic issue noting the
branch (`gh issue comment <N> --body "Epic branch:
\`feature/${EPIC_N}-${EPIC_SHORT}\`. Sub-issues will merge here, not
into develop."`). Route via `github-issues` rather than running `gh`
directly.

## Phase 0a — Dependency resolution (topo-sort + cycle detection)

After Phase 0 builds the child list and BEFORE the fast-path check
(0b) runs, resolve any explicit cross-child dependencies declared in
the issue bodies.

### How children declare dependencies

Each child issue body MAY contain a "Depends on #N" or
"**Depends on:** #N, #M" line — the `github-issues` agent supports
this syntax in issue templates. Parse those out into a
`depends_on: [int]` array on each child entry.

### Topological sort

Sort children so each comes after every issue it depends on. Standard
Kahn's algorithm works fine — pick a child with no remaining open
deps, schedule it, remove it from the dep list of its dependents,
repeat. Ties (multiple ready-now children) preserve their original
issue-order so the user sees a stable plan.

### Cycle detection

If after topo-sort there are still children with unresolved deps, you
have a cycle. **Fail loudly** — emit the cycle path
(`#A → #B → #A`) and surface it via `AskUserQuestion`. Do NOT
dispatch any of the cycle's members. The user fixes the issue
metadata (drops one of the dependencies), then ship-epic re-runs
Phase 0a.

### Effect on dispatch

After topo-sort + cycle detection passes:

- Children with **no remaining deps** are eligible for parallel
  dispatch in Phase 1 (and the fast-path check in 0b).
- Children with deps are held until all their deps reach
  `status: merged` in `state/ship-epic.json`. The ship-epic loop
  re-checks eligibility after each child merge.
- Persist `depends_on` to each child entry in the state file so
  re-injected sessions know the dependency graph.

### Why an explicit declaration

Implicit dependencies (e.g. "child B touches a file child A creates")
are not detected here — that's the design-reviewer's job (Phase 1b
contradiction guard). This phase only handles dependencies the user
explicitly declared in the issue body. Unstated deps that surface
only at design-review time pull the affected child out of the
fast-path; the rest of the epic continues.

## Phase 0b — Fast-path eligibility check (deferred-gate)

After Phase 0 creates the epic branch and BEFORE the per-child loop
runs, check if the epic qualifies for the deferred-gate fast-path.
The default flow runs design-review per child with separate user
visibility per round; the fast-path consolidates that into ONE
approval prompt covering "epic plan + all child design-reviews" so
small routine epics don't burn N+1 user touchpoints.

### Trigger conditions — ALL must hold

1. **Size bound:** the epic has either a single child, OR ≤3 sibling
   children whose `approved_scope.files_in_scope` are **disjoint**
   (no pair overlaps).
2. **Per-child complexity:** every child's
   `approved_scope.estimated_complexity` is in `{XS, S}`.
3. **No new entity:** no child's `files_in_scope` contains a path
   under `backend/.../Domain/Entities/` or
   `backend/.../Domain/Documents/`. New aggregates always need full
   per-child user visibility.
4. **AC quality:** every child issue body has ≥3 concrete AC bullets
   (well-specified — `github-issues` enforces this on creation, but
   re-verify here).
5. **Package-label match:** every child's `BE` / `Web` / `Mobile` label matches the
   orchestrator's intended dispatch. No orchestrator-side scope
   guesses (a guess is a soft signal that the issue is under-defined).

### Contradiction guard

Before offering the fast-path, scan each child's design-review
findings (if any have already run). If ANY child has:

- A new `DbSet` or aggregate added.
- `files_in_scope` crossing package boundary (`backend` + `web` in
  same child).
- `estimated_complexity` ≥ M.
- `rule_citations` including architecture anchors
  (`rules/architecture.md#banned-patterns`, etc.).

→ **Suppress the fast-path offer**. Require per-child approvals.
The cost of this guard is small; the cost of an architectural
decision flying past unreviewed is not.

### Fast-path action

Instead of asking the user for separate approvals (epic plan → child
1 design review → child 2 design review → ...), present ONE
consolidated `AskUserQuestion`:

> "Epic plan + design-review summary for all N children. Approve to
> dispatch all in parallel."

Include in the question body:
- The epic title and number.
- Each child: number, title, sub-agent, `estimated_complexity`,
  `files_in_scope` summary, `error_paths` count.
- A note that any individual child can later self-eject from the
  fast-path if its design-review actually returns NEEDS-REVISION.

On approval, start ALL children's task sessions **concurrently**, in
one message (see "Session layout"); each runs its own design-review +
dev pipeline in its own worktree.

### Fall-back

Any child whose design-review returns NEEDS-REVISION or BLOCK pulls
itself out of the fast-path:

- The orchestrator surfaces that single child's issue to the user.
- The other children continue without interruption.
- When the user resolves the blocked child, ship-epic resumes its
  per-child loop for that one child only.

### When the fast-path is rejected

If any of the trigger conditions fails, fall through to the regular
Phase 1 loop (one design-review per child, default user visibility).
This is the safe default — never auto-promote ambiguous epics.

## Phase 1 — Per-child execution loop (sub-issue → epic branch)

**The coordinator does not run 1a–1d itself.** For each child whose
dependencies are merged, it starts a task session (see "Session
layout") whose brief carries steps 1a–1d below, with the epic number,
`$EPIC_BRANCH`, the branch name to create, the guide URL, the facts the
dev agent needs, shared-file warnings and the done condition. All
ready children start in one message. **Every sub-issue branch is
rooted off `origin/$EPIC_BRANCH`, never `origin/develop`** — the app
cuts the session's worktree from another base, so the brief tells the
child to create its task branch from `origin/$EPIC_BRANCH` first.

When a child session reports back (the app wakes the coordinator):
check the PR really merged (`gh pr view <n> --json state,mergeCommit`),
set its state entry to `merged`, `SendMessage` any in-flight sibling to
merge the new epic tip, and start every child whose dependencies are
now all merged. When none are left, go to Phase 2.

Steps 1a–1d, as run **inside the task session**:

### 1a. Task branch

The app already gave the task session its own worktree. Create no
second one (no `.worktrees/<N>-<short>`, no work in the main checkout).
In the session's worktree, create the task branch off the epic branch:

```bash
git fetch origin "$EPIC_BRANCH"
git switch -c <type>/<N>-<short-kebab> "origin/$EPIC_BRANCH"
```

Where `<N>` is the child issue number, `<short-kebab>` is a short slug
of the title, and `<type>` is the branch prefix for the child's kind
label (`Task` → `feature`, `Bug` → `fix`, `Chore` → `chore`). The dev
sub-agent works in this same worktree, on this branch.

### 1b. Run design-review (Rule 5.5)

**Before dispatching the dev sub-agent**, invoke `design-reviewer`
with the child issue + dispatch brief (target sub-agent, base branch
= `$EPIC_BRANCH`, scope summary, guessed `files_in_scope`). Read the
result at `.claude/state/handoff-design-<N>.json`:

- **APPROVE** → proceed to 1c. Pass `approved_scope` forward — the
  dev sub-agent reads it as its first action.
- **NEEDS-REVISION** → tighten the brief per the findings, re-submit.
  Loop up to 3 rounds total. Round 4 → escalate to user, mark this
  child as blocked in `state/ship-epic.json`, continue with siblings.
- **BLOCK** → surface `blocked_reason` to user. Common: AC ambiguity
  → route to `github-issues` to clarify; missing parent epic branch
  → can't happen here since you created it in Phase 0.

If a child design-reviews to BLOCK while siblings are mid-flight,
let the siblings continue (the BLOCKED child pulls itself out of the
fast-path; ship-epic resumes when the user resolves the block).

### 1c. Dispatch the dev sub-agent

Using the `Agent` tool with the correct subagent_type from the
scope→agent map in `.claude/CLAUDE.md`:

- `BE` → `backend-dotnet`
- `Web` → `web-react`
- `Mobile` → `mobile-expo`
- `Chore` with no package label → orchestrator handles it directly

The prompt must include:

- The issue number (so the agent creates the right branch).
- The **base branch the agent should root from** — `$EPIC_BRANCH`,
  spelled out explicitly. Tell the agent this is a sub-issue of
  epic #`$EPIC_N` and that the PR will target the epic branch, NOT
  `develop`. Without this nudge an agent that hasn't read the latest
  CLAUDE.md will default to `develop`.
- The absolute worktree path if one was created (so the agent `cd`s
  there first).
- A pointer to the epic so the agent has context.
- An explicit reminder to run `regen-api` at the end if the child
  changed endpoint contracts (backend) or after pulling a backend
  sub-issue merge (web / mobile).

### 1d. Open the task PR and merge on green CI

Tasks get **no** `qa-tester` run and **no** `pr-reviewer` review. Both
run once, on the whole epic, in Phase 2. The task's gate is the dev
sub-agent's own verify skill plus CI.

1. Dev agent finishes, commits, and reports its verify-skill result.
   A red build or red tests go back to the same agent — never open a
   PR on a failing self-check.
2. Push the task branch and open the PR yourself on the main thread:
   `gh pr create --base "$EPIC_BRANCH" --title "<N>: <short thing>"
   --label <kind> --label <package…>` with a 2–4 sentence body that
   says `Part of #$EPIC_N` (not `Fixes` — tasks close at the epic merge).
3. Wait for CI: `gh pr checks <pr>`, polling ~30s up to 10 min. Red →
   route the failing job's root cause to the owning dev sub-agent on
   the same branch, push, wait again. A second red run on the same
   task → surface to the user.
4. Green → merge it yourself, **without asking the user**:
   `gh pr merge <pr> --squash --delete-branch --match-head-commit <sha>`.
   This holds even when the diff touches migrations or Mongo data
   scripts — Mongo data scripts are caught at the epic PR, which the
   user merges.
5. Report the merge to the coordinator (it sets the state entry to `merged`),
   and stop any compose harness it started. The app owns the
   session's worktree; the session does not remove it.
   Leave the task issue **open** — it closes when the epic PR merges.
- **Bring the new epic-branch tip into any in-flight sibling sub-issue
  branches — by merge, never rebase.** A rebase rewrites the pushed
  branch and needs a force-push, which is banned outright (global
  CLAUDE.md, `rules/git-workflow.md#never`). The sibling PR is
  squash-merged later, so the merge commit never reaches the epic
  history. Each task session does this for its own branch: when the
  coordinator says the epic branch moved, and always right before its
  own merge, it waits until its dev sub-agent has handed back, then
  runs:
  ```bash
  git fetch origin "$EPIC_BRANCH"
  git merge --no-edit "origin/$EPIC_BRANCH"
  git push
  ```
  then waits for its CI to go green again before merging. On a
  conflict it resolves it as it would any merge (an EF model-snapshot
  conflict → regenerate its own migration on top); never `--force`.
- **Do NOT dispatch `notion-docs`** for task merges. That fires
  exactly once, in Phase 3, after the epic merges to `develop`, run by
  the main thread.

The task session reports back to the coordinator (PR, merge SHA,
anything noticed but not touched) and ends.

## Phase 2 — Open and review the epic PR (epic branch → develop)

Once every sub-issue the user wants in the epic has merged into the
epic branch (or been deferred with their say-so):

1. Confirm the epic branch is clean and ahead of `develop`:
   ```bash
   git fetch origin
   git log --oneline "origin/develop..origin/$EPIC_BRANCH"
   ```
   If the log is empty, the epic landed nothing — abort and ask the
   user what happened. If `develop` has moved while the epic was
   open, **merge** the new `develop` tip into the epic branch — never
   rebase it, since that needs a force-push, which is banned:
   ```bash
   git checkout "$EPIC_BRANCH"
   git pull --ff-only
   git merge --no-edit origin/develop
   git push
   ```
   Resolve conflicts the same way you'd resolve any merge. The epic
   PR is squash-merged, so `develop` still gets one commit per epic.

2. **Run QA on the whole epic.** Dispatch `qa-tester` with the epic
   issue number, `branch: $EPIC_BRANCH`, and the list of task numbers
   that landed. It checks the acceptance criteria of the epic **and of
   every task** against the epic branch. Follow `.claude/CLAUDE.md`
   rules 6 and 6.5 for FAIL / INTERACTIVE-REQUIRED / PARTIAL. A fix
   lands on a new task-style branch off the epic branch
   (`fix/<epic-N>-qa-<k>`) and merges per Phase 1d on green CI; then
   re-run `qa-tester` on the delta.

2b. **Walk the test guide in the browser.** Once `qa-tester` passes, the main thread runs
   [`docs/guide-walk.md`](../../docs/guide-walk.md) on the epic's test guide (`test-guide` skill): every
   `ready` step, in a real browser on the dev stack, with real Stripe test mode where billing is involved.
   Fixes go on `fix/<epic-N>-walk-<k>` branches and merge per Phase 1d. Then hand the maintainer only the
   guide's **Needs you** list, and continue to step 3 without waiting for their test.

3. Open the **epic PR** with `pr-reviewer` once QA passes. Dispatch in
   `mode: open-and-review` with:
   - `branch: $EPIC_BRANCH` (the head)
   - `base: develop`
   - issue number = the **epic** issue number (so the PR body links
     `Fixes #<epic-N>` and GitHub auto-closes the epic on merge).
   - Hand it the list of task numbers that landed; `pr-reviewer`
     composes a body with one `Fixes #<task>.` line per task, so
     GitHub closes every task on the epic merge.

4. `pr-reviewer` runs the two-pass review on the consolidated diff
   (`git diff origin/develop...origin/$EPIC_BRANCH`). This is the
   **only** code review the epic's tasks get — no task PR was
   reviewed — so it reviews every line, not a delta.

5. If verdict is 🔁 NEEDS REWORK, route the fix list to the owning
   dev sub-agent. The fix lands on a **new task-style branch off the
   epic branch** and merges per Phase 1d on green CI — never directly
   on the epic branch. Then run a delta round per `.claude/CLAUDE.md`
   rule 7d (`qa-tester` ∥ `pr-reviewer` `mode: re-review`).

6. When `pr-reviewer` returns ✅ READY FOR MERGE on the epic PR,
   present the URL to the user with a one-paragraph summary of what
   the epic ships:
   ```
   Epic #<N> is ready to merge to develop:
     PR: <url>
     Tasks consolidated: #<C1>, #<C2>, …
     Strategy on merge: --squash (one commit per epic on develop)
   Reply "merge it" / "go ahead" / "approved, merge" to ship; reply
   "I'll merge this one myself" to defer to manual.
   ```
   Then **wait** for the same-turn authorization phrase. Historical
   approval from earlier in the conversation does not count
   (rule 8b).

## Phase 2b — Merge the epic PR to `develop`

When the user authorizes:

1. Re-dispatch `pr-reviewer` in `mode: merge` with the PR number and
   the verbatim authorization phrase.
2. `pr-reviewer` runs the pre-merge CI gate and the merge exclusion
   check, and returns CLEARED TO MERGE with a pinned
   `gh pr merge <n> --squash --delete-branch --match-head-commit <sha>`. **You run
   it on the main thread**; subagents can't merge. The squash
   collapses every sub-issue commit into a single commit on `develop`
   named for the epic; `develop` history stays linear and one revert
   undoes the whole epic.
3. If `pr-reviewer` returns BLOCKED on exclusions (rare for an epic,
   but possible — e.g. a Mongo data-mutation script in the diff),
   tell the user and let them merge manually. Once they confirm the
   merge landed, continue to Phase 3.
4. When MERGED:
   - The remote epic branch is gone (deleted by `--delete-branch`).
   - Fast-forward local `develop` yourself (`git pull --ff-only`, never
     a hard reset; a failure is a ⚠️ warning for the user), and clean up
     the local epic branch:
     ```bash
     git branch -D "$EPIC_BRANCH" 2>/dev/null || true
     ```
   - Confirm all tasks are closed: `gh issue view <child> --json
     state` for each should show `CLOSED`. Close any still open by
     hand via `github-issues`.

## Phase 3 — Recap, document, close

After the epic PR has merged to `develop` (or the user merged it
manually for an excluded epic):

1. Comment on the epic issue with a recap: which children shipped in
   the consolidated commit, which were deferred, link to the
   single merged epic PR (and the squashed commit SHA on `develop`).
   Use `gh issue comment <epic> --body-file` with a HEREDOC. This is
   a `github-issues` task — dispatch it rather than running `gh`
   directly.
2. The epic issue itself usually auto-closed via `Fixes #<epic-N>` in
   the PR body. If not, ask the user whether to close it manually —
   sometimes an epic stays open as a tracking issue for a follow-up.
3. **`notion-docs` update** after the epic merges to `develop`, run by
   the main thread (it needs the browser and Notion tools). One run
   covers every file the epic PR changed.

---

## Epic-level verification checklist (before declaring the skill done)

- [ ] Every child issue referenced by the epic was either shipped
      (auto-merged into the epic branch and then included in the epic
      PR) or explicitly deferred with the user's say-so.
- [ ] Every task PR had a passing dev verify-skill run and green CI
      before it merged into the epic branch.
- [ ] The guide walk (Phase 2 step 2b) marked every `ready` step by
      Claude or flagged it for the maintainer, and its failures were fixed.
- [ ] `qa-tester` returned PASS on the whole epic branch (epic ACs and
      every task's ACs) **and** `pr-reviewer` returned READY FOR MERGE
      on the epic PR's full diff before the user was asked to merge.
- [ ] Every task merge into the epic branch used `--squash`.
- [ ] The epic PR merged to `develop` with `--squash --delete-branch`
      so the develop history shows one commit per epic.
- [ ] If the epic PR hit the merge exclusion list (Mongo data-mutation
      scripts, one-shot data commands), the user merged it, not the skill.
- [ ] Every task session reported its merge; any extra worktree the
      coordinator made (e.g. for the epic branch itself) is removed.
- [ ] Local `develop` is synced and clean. Local epic branch deleted.
- [ ] A `notion-docs` update ran once, on the main thread, after the
      epic merged to `develop` (none per task).
- [ ] Epic issue was auto-closed via `Fixes #<epic-N>` (or commented
      with the final recap if the user opted not to close).

## Error-recovery notes

- **A child's self-check or CI fails repeatedly.** After 3 dev loops
  on the same child with no progress, stop and surface to the user. Don't
  burn the turn budget in a tight loop; this is the signal to hand
  back or drop the child from the epic. The epic branch holds the
  rest of the work safely while you decide.
- **A task PR fails CI.** Read the failing job (`gh run view <id>
  --log`), name the root cause, route the fix to the owning dev
  sub-agent on the same task branch, and let CI re-run.
  Develop is unaffected — only the epic branch is at stake. A second
  CI failure on the same sub-issue still warrants surfacing to the
  user before looping silently.
- **The epic PR conflicts with `develop` at the end.** `develop`
  moved while the epic was open. Merge the new `develop` into the epic
  branch (no rebase, no force-push), push, re-dispatch `pr-reviewer`
  (mode: re-review) on the epic's own new changes per rule 7d. Conflicts here mean the same code path was
  touched on `develop` and on the epic branch — resolve carefully,
  and consider whether anything on the epic branch needs adjustment
  in light of the develop change.
- **The user's local `develop` diverged mid-epic.** If `git pull
  --ff-only` fails between children or after the epic merge, stop
  and ask — don't force anything. A merge commit on `develop` is the
  user's call, not the skill's.
- **Session runs out of context mid-epic.** Each sub-issue is an
  independently resumable unit: its branch is pushed, its PR (if any)
  is open against the epic branch, and the epic branch
  holds the merged work. A fresh session can invoke `ship-epic`
  again with the same epic number; the skill picks up by reading
  the epic branch's git log to see which children have already
  merged and which still need to ship.

## Related skills to chain

- **`github-issues`** — called by Phase 3 for the epic recap comment
  and by `pr-reviewer` if labels need cleanup mid-loop.
- **`qa-tester`, `pr-reviewer`, `notion-docs`** — the three gates and
  the docs tail. `ship-epic` is the sequencer; they remain the
  authorities for their respective steps.
- **`root-cause-swarm`** — if a child's dev loop is stalling on a
  non-obvious bug (qa fails for a reason the dev agent can't
  diagnose), promote the diagnosis to a parallel swarm instead of
  guessing.
- Day-end recap of what the epic shipped: read the Notion Changelog page
  that `notion-docs` maintains rather than re-deriving from git. There is
  no standup skill installed.

## Never

- Never merge the **epic PR** (base = `develop`) without same-turn
  authorization. That's the gate that protects `develop`. Rule 8b is
  not overridden by the skill. Task PRs (base = epic branch) merge on
  green CI without asking — that's rule 8a — but the epic PR never does.
- Never base a sub-issue branch directly off `develop`. They must
  branch off the epic branch so the epic PR captures the full
  consolidated diff. Check `--base` on every `gh pr create` for a task.
- Never merge a sub-issue PR directly into `develop`. The epic-branch
  model exists specifically so this doesn't happen. If you find
  yourself about to do it, stop — that breaks the user's stated
  pain point ("don't want a half-shipped epic on develop").
- Never let one sub-agent touch more than one package. If a child
  issue spans packages, it stays on **one branch** with sequential
  sub-agent dispatches — never fan out across packages for a single
  issue.
- Never skip the epic-level gates to save a round trip. QA and review
  on the epic branch are the contract — tasks skip them only because
  the epic gets them in full.
- Never invoke `notion-docs` per sub-issue merge. The single
  invocation lives at Phase 3, after the epic ships to `develop`.
  N small docs entries for one epic is the failure mode this
  skill explicitly avoids.
- Never hand-edit `generated.ts`. If a child changed backend
  contracts, the web/mobile dev agent runs `regen-api` before
  touching call sites.
