---
paths:
  - ".pipeline/state/**"
description: PR base branch, remote-agnostic PR tooling, and the review gate before work lands
---

# PR Workflow Rules

> Orchestration rules, same loading contract as `rules/git-workflow.md`: no
> path-scoped file read triggers them, so the conductor/orchestrator loads
> this explicitly at PR time.

## PR base branch

A pull/merge request targets the same base branch the feature/bugfix branch
was cut from (`rules/git-workflow.md#never-commit-to-main`) — typically
`main`, sometimes `master` or `trunk`. Confirm the actual base with
`origin/HEAD` or the repo's own docs rather than assuming; don't retarget a PR
to a different base without an explicit reason.

## Opening a PR is remote-specific

The command that opens, updates, or merges a PR depends on which remote this
repo lives on — this rule set intentionally does not hardcode one:

- **GitHub** uses `gh pr create`, `gh pr merge`, etc.
- **Azure DevOps** uses `az repos pr create`, `az repos pr update
  --status completed`, etc.
- Some remotes have no usable CLI at all — the PR is opened through the web
  UI.

Check the repo's remote (`git remote -v`) and its own `.claude/` wiring before
reaching for either CLI. Don't assume `gh` works just because it's on `PATH`;
an inert `gh` entry in a repo's `settings.json` `ask` list is sometimes an
inherited leftover from a different remote, not a working integration.

## Review gate before landing

The pipeline prepares work; it does not land it by default. The
orchestrating session stages changes on the correct branch and **stops** —
the user reviews and finishes the task (push, open PR, merge) manually, unless
they explicitly ask otherwise in the current turn.

This gate applies with extra force to **subagents**: a designer, developer,
reviewer, or researcher subagent hands its result back to the
orchestrator and never touches the remote. Only the orchestrating/main thread
pushes, opens, or completes a PR — see
`rules/branch-and-pr.md#parallel-sub-agents-one-branch-each` for the
matching commit-level restriction.

`git push` and the PR-completion commands belong on a repo's `ask`/`deny`
permission list for exactly this reason — that prompt is the mechanism, not
an obstacle to route around.
