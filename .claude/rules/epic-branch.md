---
paths:
  - ".pipeline/state/**"
---

# Rules: Epic-branch model

Epics — issues that enumerate sub-issues in their body — do **not**
ship their children one-by-one into `develop`. They ship as one
consolidated unit. This protects `develop` from a half-shipped epic
state where one sub-issue has merged and the next is still in flight.

## Branch hierarchy

```
main
 └── develop                   ← release-stable; only complete epics merge in
      └── feature/<E>-<short>  ← THE EPIC BRANCH (one per Epic issue)
           ├── feature/<C1>-<short>   ← task branches off the EPIC branch
           ├── fix/<C2>-<short>
           └── chore/<C3>-<short>
```

## Definitions

- **Epic issue** — a GitHub issue labelled `Epic`: a whole feature,
  screen or module. Its body contains a checklist of task references
  (`- [ ] #123 — title`), or its tasks back-reference it via
  "Part of #N" / "Parent: #N". An unlabelled parent with ≥1 sub-issue
  is still treated as an epic — add the `Epic` label.
- **Task** — one implementation step of an epic, labelled `Task`
  (or `Bug` / `Chore` when that is what the step is).
- **Epic branch** — `feature/<epic-N>-<short-kebab>` (the `Epic` row of
  [`branch-and-pr.md`](branch-and-pr.md#branch-prefix-per-label)).
  Branched off `develop`. Created the moment epic work starts. Lives
  until the epic's consolidated PR merges to `develop`.

  The `pull_request.branches` filters in `.github/workflows/` cover
  every branch prefix (widened in #936), because a task PR's base is
  the epic branch. If you add a new branch prefix, widen those five
  filters with it.

  Not a verified root cause: PR #965 had no workflow runs at creation but
  did get them on its next push, with the old filters still in place both
  times. Whatever caused that gap, it was not conclusively the prefix
  list. Treat "no checks on a fresh sub-issue PR" as unexplained, and
  push a commit before concluding CI is broken.
- **Task branch** — `<prefix>/<child-N>-<short-kebab>`, prefix from the
  task's kind label. Branched off the **epic branch**, not `develop`.
  PR base = the epic branch. Every task gets its own branch and PR.
- **Standalone issue** — an issue with no parent epic. Continues to
  branch off `develop` directly with PR base = `develop`. The
  epic-branch model does not apply.

## Branch & merge flow

1. **Epic kickoff:** orchestrator creates and pushes the epic branch
   off the latest `develop`. No code yet — just a tracking branch.
2. **Task dispatch:** each task is dispatched to its dev sub-agent
   (after the design-review gate). The dev agent creates its branch
   off the epic branch (not `develop`). Concurrent tasks use
   `git worktree` rooted
   at `.worktrees/<child-N>-<short>/` based on
   `origin/<epic-branch>` — see [`branch-and-pr.md#parallel-sub-agents-one-branch-each`](branch-and-pr.md#parallel-sub-agents-one-branch-each).
3. **Task PR:** opens against the **epic branch**. **No `qa-tester`,
   no `pr-reviewer`.** Once the dev agent's own verify skill passed and
   CI is green, the main thread merges it into the epic branch without
   asking the user — see
   [`merge-strategy.md#task-merge-into-the-epic-branch`](merge-strategy.md#task-merge-into-the-epic-branch).
   The task issue stays open until the epic merges.
4. **Sibling update:** after a task merges to the epic branch, the
   orchestrator **merges** the new epic-branch tip into any in-flight
   sibling task branches (normal push) before letting their PRs
   proceed. Otherwise the diffs go stale. Never rebase a
   pushed branch — that needs a force-push, which is banned
   (`git-workflow.md#never`).
5. **Epic QA:** when every task the user wants in the epic has merged
   into the epic branch, `qa-tester` runs **once** on the epic branch,
   checking the acceptance criteria of the epic and of every task.
   Fixes land on a new task-style branch off the epic branch and merge
   per step 3.
6. **Epic review:** after QA passes, `pr-reviewer` opens the **epic
   PR** (`head = <epic-branch>`, `base = develop`, one `Fixes #<N>.`
   line for the epic and each task) and runs its two-pass review on
   the full consolidated diff. This is the only code review the tasks
   get. Rework follows `.claude/CLAUDE.md` rule 7 (delta rounds, QA ∥
   review).
7. **Epic merge:** on READY FOR MERGE the orchestrator presents the
   epic PR URL and **waits for explicit same-turn merge authorization**
   — see [`merge-strategy.md#authorized-merge`](merge-strategy.md#authorized-merge).
   It merges to `develop` with `--squash` (one commit per epic) and the
   epic branch is deleted. Verify every task issue closed; close any
   left open by hand.

## When the model applies

- The user invokes `ship-epic` (the named entry point that enumerates
  sub-issues and dispatches in parallel).
- The user hands over an epic issue ad-hoc ("implement #66") and the
  orchestrator detects sub-issues in the body or via parent
  back-references.
- The user hands over a single task ("implement #142") and the
  orchestrator detects a parent epic via the issue's body
  ("Part of #66"). The orchestrator first checks whether the epic
  branch exists; if not, it creates one off `develop` and only then
  dispatches the task against it.

## When the model does NOT apply

- Standalone issues with no parent epic — branch off `develop`,
  PR base = `develop`,
  [`merge-strategy.md#authorized-merge`](merge-strategy.md#authorized-merge) applies.
- Ad-hoc spikes (`spike/<date>-<desc>`) — no PR, no gates.
- Doc-only / chore tweaks the user explicitly wants merged one-shot to
  `develop`.
