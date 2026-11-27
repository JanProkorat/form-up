---
paths:
  - ".pipeline/state/**"
description: Branch naming, base branch, and staging discipline — stack- and remote-agnostic
---

# Git Workflow Rules

> These are **orchestration** rules — no path-scoped file read triggers them.
> The conductor/orchestrator must `Read` this file at branch-creation time (and
> before any commit) if it is not already in context. A citation is a load
> instruction, not a pointer.

## Never commit to main

Every repo has a base branch (`main`, `master`, or `trunk` — check
`origin/HEAD` or the pack/repo config if unsure). **Never commit directly to
it** — always work on a branch cut from an up-to-date base:

```bash
git fetch origin
git switch -c feature/<TASK>-<slug> origin/main   # substitute the repo's actual base branch
```

Never assume the current checkout is the right base; a repo is routinely left
on a previous task's feature branch.

## Branch naming

```
feature/<TASK>-<slug>     new behaviour
bugfix/<TASK>-<slug>      defect fix
```

`<TASK>` is the tracking-system key (a Jira key, a GitHub/Azure DevOps issue
number, whatever this repo's ticketing system uses). `<slug>` is a short
kebab-case summary and is **required** — `feature/TASK-1234` alone is not
sufficient, even if older branches in a given repo omit it. The slug is what
makes a branch list readable without cross-referencing the ticket.

```
feature/TASK-1234-prod-temp-edit-propagation    correct
bugfix/TASK-1250-lock-release-clears-temp-edit  correct
feature/TASK-1234                               missing slug
TASK-1234-something                             missing prefix
```

## Stage explicit paths

**Never `git add -A` and never `git add .`.** Stage the specific paths you
changed:

```bash
git add path/to/changed-file-one path/to/changed-file-two
```

A broad add has previously swept up gitignored local state (personal worklog
symlinks, `.env` files, scratch output) that happened to sit in the working
tree. Reviewing `git status` before staging, and staging by explicit path, is
the only mechanical guard against that.

## Never

- **Never `--no-verify`, `--no-gpg-sign`, or an admin-override merge flag** to
  get past a failing hook or gate. A failing hook is telling you something —
  diagnose it. Only an explicit, current instruction from the user overrides
  this.
- **Never force-push** (`--force` / `--force-with-lease`). If a repo's
  settings deny it outright, that is enforcement, not an obstacle to route
  around.
- **Never `git add -A` / `git add .`** — see above.

## Commit hygiene

Code comments stay in the language this project's conventions specify (see
the pack/repo's own style rules) — never reference acceptance criteria, PR
numbers, or issue numbers in code comments. That context belongs in the
commit message and the PR description, not the source.
