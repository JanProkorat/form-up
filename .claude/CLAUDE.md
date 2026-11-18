# Form Up — Orchestration & Sub-agent Routing

This file supplements the root `CLAUDE.md`. It tells the main (orchestrator)
Claude how to route work to specialist sub-agents so each package's conventions
are applied consistently.

Detailed conventions live in [`rules/*.md`](rules/) — cite anchors, never
restate. Citation format: `rules/<file>.md#<anchor>`.

Rules are path-scoped (`paths:` frontmatter): backend rules load on `backend/**`,
client rules on `web/**`/`mobile/**`, pipeline rules on `.claude/state/**`.
None preload. A citation is a load instruction — `Read` the cited rule file
before acting on it if it is not already in context.

## Sub-agents

Project-local dev agents (live in `.claude/agents/`):

| Agent              | Use when work primarily touches…                                |
|--------------------|-----------------------------------------------------------------|
| `backend-dotnet`   | `/backend/**` — ASP.NET Core 10, FastEndpoints, EF/Mongo, SignalR |
| `web-react`        | `/web/**` — React 19, Vite, shadcn, TanStack Query, RHF + Zod   |
| `mobile-expo`      | `/mobile/**` — React Native, Expo Router, Zustand, design tokens |

Project-local workflow agents (also in `.claude/agents/`):

| Agent              | Role                                                            |
|--------------------|-----------------------------------------------------------------|
| `github-issues`    | GitHub issue lifecycle (create, edit, label, triage, close). Not for PRs or code. |
| `qa-tester`        | Verify a GitHub issue's ✅ Acceptance criteria after dev agents finish (for an epic: once, on the whole epic branch). Read-only at the source-tree level. Drives Playwright + iOS Simulator (XcodeBuildMCP) for AC flows. Returns PASS / PARTIAL / FAIL. |
| `pr-reviewer`      | Runs after `qa-tester` PASS, on PRs into `develop` only (never on a task PR into an epic branch). Creates/updates the PR (against the base the orchestrator passes — see [`rules/branch-and-pr.md`](rules/branch-and-pr.md)), runs a two-pass review (self + fresh-eyes Agent), and merges per [`rules/merge-strategy.md`](rules/merge-strategy.md). |
| `design-reviewer`  | Pre-implementation gate. Reads issue + dispatch brief BEFORE dev agents start. Returns APPROVE / NEEDS-REVISION / BLOCK with structured `approved_scope`. |

## Project facts

- **Repo:** `JanProkorat/form-up`
- **Local path:** `/Users/jan/Projects/form-up`
- **Active base branch:** `develop` (release: `main`)

## Label taxonomy

Ten labels, defined in `.github/labels.yml` (the sync job deletes any
other). **Every new issue gets them** — one kind, one priority, and one
package label per package whose code it changes:

- **Kind (exactly one)** — `Epic` (a whole feature, screen or module),
  `Task` (one implementation step of an epic), `Bug`, `Chore`
  (refactoring, docs, CI, cleanup).
- **Package (one per package touched)** — `BE`, `Web`, `Mobile`. A
  `Chore` that touches no package code gets none.
- **Priority (exactly one)** — `High`, `Medium`, `Low`.

## Key cross-references

| What | Where |
|---|---|
| Scope → dev-agent mapping, package boundaries, scope → stack map | [`rules/scope-boundaries.md`](rules/scope-boundaries.md) |
| Branch naming, worktree pattern, parallel safety | [`rules/branch-and-pr.md`](rules/branch-and-pr.md) |
| Epic-branch model (two-tier integration) | [`rules/epic-branch.md`](rules/epic-branch.md) |
| Merge strategy, task merge into the epic branch, exclusion list | [`rules/merge-strategy.md`](rules/merge-strategy.md) |
| Hardcoded-value bans, write-locked generated files | [`rules/code-style.md`](rules/code-style.md) |
| i18n mechanism (generic) — locale list is in root `CLAUDE.md` | [`rules/i18n.md`](rules/i18n.md) |
| Verification surfaces per scope | [`rules/verification-contract.md`](rules/verification-contract.md) |
| Backend test layers, builders, isolation, CI time budget | [`rules/testing.md`](rules/testing.md) |
| Web test layers (Vitest component vs Playwright E2E), double coverage | [`rules/web-testing.md`](rules/web-testing.md) |

Scope → stack map: [`rules/scope-boundaries.md#scope-to-stack-mapping`](rules/scope-boundaries.md#scope-to-stack-mapping).
Locales: root `CLAUDE.md` → Shared Conventions.

## Branch / PR / merge precedence

Branch/PR/merge in this repo follow [`rules/branch-and-pr.md`](rules/branch-and-pr.md)
+ [`rules/merge-strategy.md`](rules/merge-strategy.md) — issue+epic-based,
with task PRs merged into the epic branch on green CI, without QA,
review or a user pause.
Where the seeded hub
[`rules/pr-workflow.md`](rules/pr-workflow.md) / [`rules/git-workflow.md`](rules/git-workflow.md)
differ (they assume a `/conductor`-style pipeline), **the local rules win**:
this repo's pipeline is issue+epic-based, and `pr-reviewer` is the sub-agent
that opens PRs into `develop` and **clears** their merges per
[`rules/merge-strategy.md#authorized-merge`](rules/merge-strategy.md#authorized-merge).
One part of the hub rule does hold: subagents never push or merge
(`deny-subagent-merge.py`). The main thread pushes, and runs the pinned
`gh pr merge` command `pr-reviewer` hands back.

## Design source of truth

The **Form Up redesign canvases** are the design source of truth, one per app,
each with one page per module:

| App | Canvas | Boards |
|---|---|---|
| Web portal (and emails) | <https://claude.ai/artifact/5GDTsJDGYJ9RevCTva3ciK> | `Page*`, `Email*` |
| Client app | <https://claude.ai/artifact/KMXvDF1KzZuqkq6LAsTDr9> | `Glass*` |
| Coach app | <https://claude.ai/artifact/9ePcFr5gAj5uqzPHzqzXyR> | `Coach*` |

Every design check, design review and UI test compares against them — light
**and** dark — unless the user says otherwise for that task. The old combined
canvas (`HFzM8WqykBxJvkLdiqU85h`) is frozen; do not use it.

- **Who can read what.** The canvases are private claude.ai artifacts: only the
  main thread can open them (`Artifact` tool, `action: "read"`). Subagents read
  the repo snapshot `docs/prototypes/formup-redesign/` instead (gallery
  `index.html`, one HTML + PNG per board, clickable prototypes in
  `interactive/` (web), `interactive-client/` and `interactive-coach/`).
- **Matching a screen to a board.** Boards are named after the screen:
  `source/project/Page<Screen>{C,D}.dc.html` for the web portal (`C` light,
  `D` dark), `Glass<Screen>{Light,Dark}` for the client app,
  `Coach<Screen>{Light,Dark}` for the coach app. The exported copies are under
  `web/`, `mobile-light|dark/`, `coach-light|dark/`. Issues and briefs name the
  board (e.g. `PageTemplateDay`), not a file path.
- **Keep the snapshot current.** Its README states the version of each canvas it
  was exported from. Before a design check, the main thread compares those with
  the live canvases; if one is newer, re-export (README "Rebuilding") before
  dispatching agents that rely on it.
- **Superseded:** the older `docs/prototypes/{mobile,trainer,notion}/scenes/*.html`
  prototypes. Use them only when the user asks for them explicitly.
- **Not yet redesigned.** A fix on an existing screen that has not been
  rebuilt to the redesign (the issue names no board) is checked against the
  current app, not a board — fidelity is "not applicable", not a FAIL. New
  screens and screens the issue ties to a board are always checked.

## Routing rules

1. **Single-package task** → delegate to the matching sub-agent via the `Agent`
   tool. Brief it with the user's goal, relevant file paths, and any constraints
   already established in conversation.
2. **Cross-package task** → orchestrate sequentially per
   [`rules/scope-boundaries.md#cross-package-coordination`](rules/scope-boundaries.md#cross-package-coordination).
3. **Unsure which package** → ask the user with `AskUserQuestion` before delegating.
4. **Never** let a sub-agent modify files outside its package boundary
   (see [`rules/scope-boundaries.md#package-boundary-rule`](rules/scope-boundaries.md#package-boundary-rule)).
   `pr-reviewer` enforces this on the diff.
5. **Issue work** (create/edit/close/label/triage) routes to `github-issues`
   regardless of which package the issue is about. Package sub-agents never
   touch the GitHub API — they focus on code.
5.5. **Design-review gate.** Before dispatching a dev sub-agent for an issue,
   invoke `design-reviewer` with the issue number + the dispatch brief
   (target sub-agent, base branch, scope summary, guessed files-in-scope).
   - **APPROVE** → proceed with dispatch. Pass `approved_scope.files_in_scope`,
     `required_reads`, and `error_paths` to the dev agent as its scope contract
     (it reads `state/handoff-design-<issue>.json` as its first action).
   - **NEEDS-REVISION** → address the listed findings (usually tighten the
     brief — drop out-of-scope files, add missing test plan), re-submit.
     Loop up to **3 rounds total**. Round 4 NEEDS-REVISION → surface to user.
   - **BLOCK** → surface `blocked_reason` to user. Common causes: AC needs
     clarification (route to `github-issues`), missing parent epic branch
     (create it first), fundamental architecture conflict.
   Skip only for ad-hoc spikes that don't originate from a GitHub issue.
   Doc tweaks and chore PRs still go through it (lightweight pass).
   Each task of an epic goes through it too — this gate is per task.

5.6. **Epic tasks skip QA and review.** A task PR into an epic branch
   gets no `qa-tester` (rule 6) and no `pr-reviewer` (rule 7). Its gate
   is the dev agent's own verify skill plus green CI, then the main
   thread merges it without asking (rule 8). Rules 6 and 7 run once on
   the whole epic: `qa-tester` on the epic branch with the epic and all
   task numbers, then `pr-reviewer` on the epic PR to `develop`. See
   [`rules/epic-branch.md#branch-merge-flow`](rules/epic-branch.md#branch-merge-flow).
6. **Acceptance-criteria gate.** Work that starts from a GitHub issue is not
   "done" until `qa-tester` returns OVERALL ✅ PASS. For an epic this runs
   once, on the epic branch, after every task has merged (rule 5.6).
   Sequence:
   a. Dev sub-agent(s) finish their slice and report back via the dev-handoff
      JSON (see [`schemas/dev-handoff.v1.json`](schemas/dev-handoff.v1.json)).
      List its `noticed` entries to the user; each becomes an issue only if
      the user says so.
   b. Orchestrator dispatches `qa-tester` with the issue number.
   c. ❌ FAIL → route the fix list to the owning dev sub-agent, then re-run
      `qa-tester` on the delta (`since: <sha of its last verdict>`, see
      rule 7d). Iterate until at least the static + bash-smoke surface
      passes (PASS / PARTIAL / INTERACTIVE-REQUIRED).
   c2. ⚠️ INTERACTIVE-REQUIRED → orchestrator runs the interactive QA
      playbook on the main thread (see rule 6.5), consolidates evidence
      into the per-AC results, and produces a final verdict. If interactive
      drive surfaces a defect, route the fix back to dev and restart the
      gate from (b). If interactive drive passes all flagged ACs, proceed
      to the code-review gate (rule 7).
   c3. ⚠️ PARTIAL (missing fixture / data, not tooling) → either fix the
      gap (e.g. extend `QaSeedRunner` via `backend-dotnet`) or accept the
      gap with a follow-up issue; user decides. PARTIAL with a documented
      gap may proceed to code-review on user authorization.
   d. PASS → orchestrator tells the user the task is done and (if applicable)
      suggests closing via `github-issues` with `Fixes #<N>`.
   Skip only if the task did not originate from a GitHub issue. Never skip to
   save a round trip — the AC is the contract.

6.5. **Orchestrator-driven interactive QA playbook.** Triggered when
   `qa-tester` returns ⚠️ INTERACTIVE-REQUIRED, or when the user asks for an
   ad-hoc smoke test. `Read` [`docs/interactive-qa-playbook.md`](docs/interactive-qa-playbook.md)
   first and follow it.
7. **Code-review gate.** Once `qa-tester` returns ✅ PASS, the work is not
   "ready for merge" until `pr-reviewer` returns ✅ READY FOR MERGE. Applies
   to PRs into `develop` — standalone and epic PRs, never task PRs into an
   epic branch (rule 5.6). Sequence:
   a. Orchestrator dispatches `pr-reviewer` with the issue number, the
      working branch, and the explicit `base` branch (per
      [`rules/branch-and-pr.md`](rules/branch-and-pr.md)).
   b. `pr-reviewer` opens/updates the PR and runs the two-pass review:
      first-pass self-review (invokes the `review` skill + the project's
      hard-rule gate); then — only if the self-review is clean — a second
      pass delegated to a fresh-eyes sub-reviewer via `Agent` (briefed
      blind, no orchestrator context beyond PR body + diff). Both passes
      must be clean for READY FOR MERGE.
   c. 🔁 NEEDS REWORK → `pr-reviewer` returns a scope-tagged fix list.
      Orchestrator routes each section to the owning dev sub-agent.
   d. After fixes, push a settled head, then run a **rework round** (#1106):
      - **Delta only.** Pass both agents `since: <sha of their last verdict>`;
        they check `git diff <since>..HEAD` (plus what it touches), not the
        whole branch. The first review of a PR is always full. If the round
        merged the base branch in, review only the PR's own new changes —
        compare `git diff origin/<base>...<since>` with `git diff origin/<base>...HEAD`.
      - **In parallel.** Dispatch `qa-tester` and `pr-reviewer` (`mode:
        re-review`) together; commit nothing while they read.
      - **Skip QA when no AC behaviour changed** — test-only, docs,
        wording or CI-config fixes. CI plus the delta review gate that
        round. Say in the dispatch which case it is and why.
      - Hand over evidence (exact commands, counts, CI run ids) so they
        spot-check instead of re-running.
      Iterate dev → (qa ∥ review) until READY FOR MERGE.
      Stop and surface to the user when the same finding (file:line +
      rule) survives two rework rounds, or a round fixes nothing from the
      last fix list.
   e. READY FOR MERGE → hand off to the merge gate (rule 8). Skip only for
      tasks that don't produce a PR (doc-only commits, infra-only tweaks
      the user explicitly merges out-of-band).
8. **Merge gate.** Subagents can't merge; the main thread always runs the
   merge command. Behaviour depends on the PR's base branch:
   - **Task PR (base = epic branch)** → once CI is green, the main thread
     merges it itself, without `pr-reviewer` and without asking, per
     [`rules/merge-strategy.md#task-merge-into-the-epic-branch`](rules/merge-strategy.md#task-merge-into-the-epic-branch).
     The task issue stays open until the epic merges.
   - **Epic PR / standalone PR (base = `develop`)** → `pr-reviewer` clears
     it and returns a pinned `gh pr merge … --match-head-commit <sha>`;
     require explicit same-turn user authorization per
     [`rules/merge-strategy.md#authorized-merge`](rules/merge-strategy.md#authorized-merge).
   - **Excluded PRs** (base=`main`, Mongo data-mutation scripts, one-shot
     data commands; EF migrations are *not* excluded) →
     refuse and BLOCK per
     [`rules/merge-strategy.md#exclusion-list`](rules/merge-strategy.md#exclusion-list).

## Skills

| Skill             | What it does                                                                                                                 |
|-------------------|------------------------------------------------------------------------------------------------------------------------------|
| `dotnet-feature`  | Scaffolds a new FastEndpoints vertical slice (request/response/validator/endpoint) per `.claude/rules/*.md`. From `backend-dotnet`; `dotnet-tdd` for tests. |
| `mongo-document`  | Scaffolds a new MongoDB root aggregate (Id, ExternalId, Version, audit fields, collection registration). From `backend-dotnet`. |
| `signalr-event`   | Wires a realtime event end-to-end across backend → web → mobile. Orchestrator-run.                                           |
| `regen-api`       | Regenerates the TypeScript API client from Swagger. Run by the client sub-agent that needs it.                                |
| `react-page`      | Scaffolds a trainer-portal page (TanStack Query + RHF/Zod + shadcn primitives + i18n). From `web-react`.                     |
| `expo-screen`     | Scaffolds an Expo Router screen (theme tokens, data fetching, i18n once the app has them). From `mobile-expo`.               |
| `notion-docs`     | Builds + incrementally maintains the project's documentation in Notion.                                                       |
| `prototype-scene` | Adds a scene to an existing HTML prototype, or scaffolds a new prototype file.                                                |
| `ask-user-async`  | Posts a blocking question to Slack and ends the session cleanly. Use only when the user has declared AFK mode in the current turn. |
| `resume-pending`  | Paired skill for `ask-user-async` — pick up the answer at session start.                                                      |
| `ui-tradeoff`     | Enforces Working Principles §4 (two-attempt stop rule).                                                                       |
| `root-cause-swarm`| Enforces Working Principles §1 (no speculative patches) for multi-layer bugs.                                                 |
| `ship-epic`       | Full epic-to-PR lifecycle. Orchestrator-only. See [`rules/epic-branch.md`](rules/epic-branch.md).                            |

## Chainable plugin skills (external)

Invoke by their fully-qualified name. **Every entry below was verified against
`~/.claude/plugins/installed_plugins.json` on 2026-08-06** — if you add a row,
check the skill actually resolves first.

| Skill                          | Use after…                                                |
|--------------------------------|------------------------------------------------------------|
| `claude-security`              | Adding/changing auth, ownership, upload, or invite endpoints — the security gate. Deep scan at a chosen effort tier; every finding is challenged by a verifier agent before it is reported |
| `owasp-security`               | Reference guidance while *writing* auth code (OWASP Top 10, ASVS). Not a substitute for the scan above |
| `code-review:code-review`      | Non-trivial backend changes or cross-package diffs        |
| `frontend-design:frontend-design` | New web page or mobile screen ready for review          |
| `wcag-audit`                   | Any screen with forms, tables, modals, or colour-critical UI |

No `engineering:*`, `design:*` or `gc-sec-review` skill is part of this workflow (#911).
Security review → `claude-security`; code review → `code-review:code-review`;
design → `frontend-design:frontend-design`; accessibility → `wcag-audit`;
testing strategy → the stack `*-verify` skills; anything else → do it inline.

## Guardrails (enforced by hooks)

- `src/api/generated.ts` in `/web` and `/mobile` is write-locked. The
  `block-generated-client.py` PreToolUse hook rejects Edit/Write/MultiEdit on
  those paths. To change shapes, regenerate via the `regen-api` skill.
- Compound commands (`&&` / `;`) get split via `split-compound-commands.py`
  so each part passes permission validation independently.
- Subagents cannot run `gh pr merge` or any `git push` — `deny-subagent-merge.py`
  blocks them. `pr-reviewer` clears a merge and hands back the pinned command;
  the main thread pushes branches and runs the merge.
- Each agent has a curated bash allowlist via `agent-bash-allowlist.sh` —
  e.g. `qa-tester` is blocked from `git commit`, `backend-dotnet` from `npm`.
- Sub-agent handoffs are JSON-schema-validated before control returns
  (`gate-check.sh` SubagentStop hook). Schemas under `.claude/schemas/`.
- Long-running orchestration state persists to `.claude/state/ship-epic.json`;
  `reinject-state.py` re-hydrates context after `/clear` or compact.

## Task lifecycle reminder

0. **Session start — check for pending async questions.** Look for
   `.claude/pending-question.md`. If present with `status: waiting`, invoke
   `resume-pending`. Same on phrases like "resume", "continue", "check Slack".
1. Read root `CLAUDE.md` before starting. Live documentation is in Notion
   (`notion-docs` maintains it); `docs/PROGRESS.md` is frozen historical context.
2. **Determine the integration tier.** If a GitHub issue, fetch and check for
   the `Epic` label or task references. Standalone → branch off `develop`.
   Epic → first create + push the epic branch off `develop`, then dispatch
   tasks (`ship-epic` for ≥2 tasks). Task of an existing epic → confirm the
   epic branch is pushed; create it first if not.
3. Delegate to the correct sub-agent. Always tell the dev sub-agent which
   base branch to root from.
4. **Stop between phases and wait for confirmation.** If a genuine blocker
   appears and the user has declared AFK mode in the current turn, invoke
   `ask-user-async`. In-session questions still use `AskUserQuestion`.
5. Task of an epic → push, open the PR against the epic branch, merge it
   yourself on green CI (no QA, no review, no user pause). Next task.
6. Standalone issue, or an epic once all its tasks have merged → dispatch
   `qa-tester`, loop dev → qa until PASS, then `pr-reviewer` with base
   `develop`. Loop dev → (qa ∥ review, delta-only — routing rule 7d) until
   READY FOR MERGE.
7. Wait for explicit same-turn merge auth, then run `pr-reviewer`'s pinned
   merge command yourself. For an epic, check every task issue closed.
8. After merge to `develop` (epic or standalone) → run `notion-docs`
   (update mode) on the main thread. It creates any missing wiki pages
   itself; `build` mode is for recovery only.
