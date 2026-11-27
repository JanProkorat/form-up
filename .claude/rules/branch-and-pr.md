---
paths:
  - ".pipeline/state/**"
---

# Rules: Branch & PR conventions

All code-bearing work follows this branch-name format:

```
<type>/<issue-number>-<short-kebab-description>
```

## Branch prefix per label

`<type>` comes from the issue's kind label:

| Kind label | Branch prefix | Example |
|---|---|---|
| `Epic` | `feature/` | `feature/66-photos-epic` |
| `Task` | `feature/` | `feature/123-nutrition-plan-publish` |
| `Bug` | `fix/` | `fix/124-plan-detail-crash-on-empty-week` |
| `Chore` | `chore/` | `chore/127-bump-expo-sdk` |

Older branches use `refactor/` and `docs/`; the CI filters still accept
them, but new branches use the four rows above.

## Where the branch is rooted

Depends on whether the issue is part of an epic — see
[`epic-branch.md`](epic-branch.md) for the full model.

| Issue kind | Branched off | PR base |
|---|---|---|
| Standalone issue (no parent epic) | `develop` | `develop` |
| Epic issue (parent of sub-issues) | `develop` | `develop` *(opens at end of epic)* |
| Task of an epic | `feature/<epic-N>-<short>` (the epic branch) | the same epic branch |
| Release roll-up (rare) | `develop` | `main` |

## Format rules

- Short description is kebab-case, ≤60 chars, derived from the issue title.
- The issue number is **mandatory** — it's how `pr-reviewer`,
  `qa-tester`, and `notion-docs` correlate work across the lifecycle.
- Dev sub-agents create the branch as their first step on an issue.
  The orchestrator tells them which base to root from (`develop` for
  standalone work; the epic branch name for sub-issues).
- Without an issue (ad-hoc spike), use `spike/<date>-<desc>` and don't
  open a PR.
- The epic branch itself is created by the orchestrator at epic
  kickoff (not by a dev sub-agent) and pushed to `origin` immediately
  so all sub-issue worktrees can branch off `origin/<epic-branch>`.

## Validation by pr-reviewer

`pr-reviewer` validates **branch format** AND **base branch** on first
PR creation (PRs into `develop`). For task PRs into an epic branch,
which `pr-reviewer` does not see, the main thread checks both before
`gh pr create`:

- Branch-name mismatch → bounce back to dev sub-agent for rename.
- Wrong base (e.g. a task PR opened against `develop` when an epic
  branch exists) → fix the base before merging.

## Parallel sub-agents — one branch each

When the orchestrator dispatches two or more sub-agents in **parallel**
(an epic fan-out, two backend-dotnet instances on different issues),
each sub-agent works on its own branch in its own working tree. Never
share a checkout — commits will interleave, one will stomp the other's
`git add`, and PRs will mix unrelated diffs.

### Worktree pattern

The orchestrator (or the sub-agent itself) creates a throwaway worktree
rooted at `.worktrees/<issue-number>-<short>/`. The base ref depends
on whether the issue is part of an epic:

**Standalone issue** (no parent epic):
```
git worktree add .worktrees/123-nutrition-publish \
    -b feature/123-nutrition-plan-publish origin/develop
```

**Sub-issue of epic #66** (epic branch already pushed as
`feature/66-photos-epic`):
```
git fetch origin feature/66-photos-epic
git worktree add .worktrees/142-mobile-profile-photos \
    -b feature/142-mobile-profile-photos origin/feature/66-photos-epic
```

The sub-agent works inside that path, pushes its branch, opens the PR
against the correct base, and hands back. After merge, `pr-reviewer`
removes the worktree (`git worktree remove .worktrees/<issue>-<short>`).
`.worktrees/` is gitignored.

### Serial dispatch

If two sub-agents run sequentially (backend finishes → web starts), the
second can reuse the main working tree — but **check out the right
base** first. For standalone work that's `develop`; for sub-issue work
it's the epic branch (`git checkout <epic-branch> && git pull --ff-only`).

Worktrees are the fix for *concurrent* work, not a blanket ritual.

### Cross-package PRs stay on one branch

A single GitHub issue requiring backend + web + mobile changes ends up
on **one branch with one PR** — sub-agents run sequentially on the same
branch (each re-pulls before editing). Parallel fan-out is for
*different issues*, never for splitting one issue across packages.

## One-branch-per-PR enforcement

`pr-reviewer` enforces one-branch-per-PR. If it sees commits on the
branch that don't match the PR's issue number (e.g. another sub-agent's
stray commit), it refuses to merge and returns BLOCKED with "branch
contains unrelated commits".

## Smells that break isolation

- Two sub-agents both running `git checkout <same branch>` in the same
  working tree — one of them is about to lose work.
- A sub-agent running `git stash` to make room for a parallel task —
  stash is a band-aid; the correct answer is a worktree.
- A branch with commits authored by more than one sub-agent covering
  more than one issue number — split it before opening the PR.
