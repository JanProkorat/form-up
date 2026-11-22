---
paths:
  - ".pipeline/state/**"
---

# Rules: Scope boundaries

Universal across the project — every dev sub-agent and `pr-reviewer`
follow these. Cite anchors; never restate.

## Scope to dev-agent mapping

| Package label | Dev agent        | Folder            |
|---------------|------------------|-------------------|
| `BE`          | `backend-dotnet` | `/backend/**`     |
| `Web`         | `web-react`      | `/web/**`         |
| `Mobile`      | `mobile-expo`    | `/mobile/**`      |
| (none — `Chore` only) | (orchestrator) | `/docs/**`, `.github/**`, `.claude/**`, root configs |

## Scope to stack mapping

Used by pack `<stack>-verify`/`<stack>-build` skills to decide which stack
pack(s) a work item's `files_touched` implicate (see
[`rules/verification-contract.md`](verification-contract.md)):

| Path glob      | Stack    | Verify skill    | Build-floor skill |
|-----------------|----------|-----------------|--------------------|
| `/backend/**`   | `dotnet` | `dotnet-verify`  | `dotnet-build`     |
| `/web/**`       | `react`  | `react-verify`   | `react-build`      |
| `/mobile/**`    | `expo`   | `expo-verify`    | `expo-build`       |

A work item spanning more than one glob (rare — cross-package issues are
sequenced per [#cross-package-coordination](#cross-package-coordination))
runs every implicated stack's verify skill; all must pass.

## Package-boundary rule

A sub-agent **never** modifies files outside its package's folder. If a
cross-cut is required, return to the orchestrator and route explicitly.
`pr-reviewer` enforces this on the diff — a backend-dotnet PR that
touches `/web/src/**` is an automatic BLOCKING finding.

## Cross-package coordination

Cross-package issues are dispatched **sequentially**, not in parallel:

1. `backend-dotnet` finishes the backend slice + opens the PR (or
   commits to the shared issue branch).
2. Orchestrator hands off to `web-react` / `mobile-expo` (each runs
   `regen-api` in its own package before touching call sites).
3. The orchestrator runs `regen-api` directly **only** when no client
   work follows.

A single issue requiring backend + web + mobile changes ends up on **one
branch with one PR** — sub-agents run sequentially on the same branch
(each re-pulls before editing). Parallel fan-out is for *different
issues*, never for splitting one issue across packages.

## When in doubt

If you cannot tell which package an issue belongs to, ask via
`AskUserQuestion` before delegating. Never let a sub-agent guess across
boundaries.
