# `.claude/` — Structure & Navigation

This folder is the Claude Code setup for **form-up**, a multi-package
project (ASP.NET Core 10 backend + React 19 web portal + React Native / Expo
mobile app). It wires an issue → design-review → dev → qa → pr-review → merge
pipeline on top of rules, sub-agents, skills, and hooks.

The folder is portable in shape — the rules, hooks, schemas, and most agents
are generic enough to lift into any TypeScript / .NET project. Project-specific
facts live in the root `CLAUDE.md` and in this folder's `CLAUDE.md`.

---

## The Map

```
.claude/
├── CLAUDE.md            — Orchestration & sub-agent routing (project-local)
├── README.md            — (this file) folder navigation guide
├── settings.json        — Permissions (allow / deny / ask) + hook registrations
├── settings.local.json  — User-local permission allowlist (gitignored)
├── PLUGINS.md           — Audit log of enabled / disabled plugins (in ~/.claude/)
│
├── agents/              — 7 sub-agent definitions (isolated context each)
│   ├── backend-dotnet.md     blue   — /backend specialist
│   ├── web-react.md          cyan   — /web specialist
│   ├── mobile-expo.md        purple — /mobile specialist
│   ├── design-reviewer.md    magenta — pre-impl gate (NEW; Rule 5.5)
│   ├── qa-tester.md          green  — AC verification (read-only at source)
│   ├── pr-reviewer.md        red    — PR lifecycle + merge
│   └── github-issues.md      yellow — issue lifecycle (no code, no PRs)
│   └── pr-reviewer/references/review-checklist.md  — 12-point hard-rule gate
│
├── skills/              — Invocable workflows (frontmatter `argument-hint`)
│   ├── ship-epic/          (orchestrator-only, `disable-model-invocation: true`)
│   ├── dotnet-feature/     FastEndpoints vertical-slice scaffolder
│   ├── dotnet-tdd/         test-first flow for .NET endpoints
│   ├── dotnet-debug/       systematic per-status-code investigation
│   ├── dotnet-migrate/     EF Core migration workflow
│   ├── dotnet-review/      .NET convention review
│   ├── dotnet-build/ dotnet-verify/   .NET build floor / full verification
│   ├── mongo-document/     MongoDB root-aggregate scaffolder
│   ├── signalr-event/      end-to-end realtime event wiring
│   ├── regen-api/          NSwag-driven generated.ts regen
│   ├── react-page/         trainer-portal page scaffolder
│   ├── react-build/ react-verify/     web build floor / full verification
│   ├── expo-screen/        Expo Router screen scaffolder
│   ├── expo-build/ expo-verify/       mobile typecheck floor / full verification
│   ├── frontend-review/    cross-framework frontend review checklist
│   ├── prototype-scene/    HTML prototype scene scaffolder
│   ├── notion-docs/        Notion wiki maintenance
│   ├── daily-resercher/    Claude Code ecosystem survey
│   ├── root-cause-swarm/   parallel hypothesis exploration (multi-layer bugs)
│   └── ui-tradeoff/        two-attempt stop rule for animation/layout
│
├── hooks/               — Deterministic shell + Python triggered by Claude Code
│   ├── split-compound-commands.sh    PreToolUse[Bash] — split && and ;
│   ├── deny-subagent-merge.sh        PreToolUse[Bash] — block sub-agent merges
│   ├── agent-bash-allowlist.sh       PreToolUse[Bash] — per-agent toolchain
│   ├── block-generated-edits.sh      PreToolUse[Edit|Write] — generated.ts lock
│   ├── reinject-state.sh             SessionStart — state recovery on /clear
│   ├── gate-check.sh                 SubagentStop — handoff JSON validation
│   ├── validate-handoff.py           Python — schema + citation validator
│   ├── typecheck-on-stop.sh          Stop — background TS typecheck
│   ├── typecheck-on-submit.sh        UserPromptSubmit — surface results
│   └── log/                          (gitignored) per-day audit trail
│
├── rules/               — Conventions cited by anchor (no auto-load; explicit only)
│   ├── scope-boundaries.md
│   ├── branch-and-pr.md
│   ├── epic-branch.md
│   ├── merge-strategy.md
│   ├── code-quality.md
│   ├── i18n.md
│   └── verification.md
│
├── schemas/             — JSON Schema for handoff artifacts (validate-handoff.py)
│   ├── design-reviewer-result.v1.json
│   ├── dev-handoff.v1.json
│   ├── qa-tester-result.v1.json
│   ├── pr-reviewer-result.v1.json
│   └── ship-epic-state.v1.json
│
└── state/               — (gitignored) runtime artifacts
    ├── ship-epic.json                  pipeline state (validated by reinject)
    ├── handoff-design-<N>.json         per design-review output
    ├── handoff-dev-<N>.json            per dev-agent output
    ├── handoff-qa-<N>.json             per qa-tester output
    └── handoff-review-<PR>.json        per pr-reviewer output
```

---

## The Pipeline

```
GitHub issue
    │
    ├─ Rule 5.5 ─▶ design-reviewer  ──▶ APPROVE / NEEDS-REVISION / BLOCK
    │                  │ (handoff-design-<N>.json)
    │                  ▼
    │              dev sub-agent (backend / web / mobile)
    │                  │ (handoff-dev-<N>.json)
    │                  ▼
    │              qa-tester  ──▶ PASS / PARTIAL / FAIL
    │                  │ (handoff-qa-<N>.json)
    │                  ▼
    │              pr-reviewer (two-pass: self + fresh-eyes)
    │                  │ (handoff-review-<PR>.json)
    │                  ▼
    │              merge gate  ──▶ 8a auto-merge to epic branch
    │                                  ──▶ 8b explicit auth → develop
    └─ ship-epic skill drives this loop for an epic + N children
```

Each agent writes a schema-validated JSON handoff to `.pipeline/state/` that the
next phase reads as its sole structured input. `gate-check.sh` validates
on the way out (SubagentStop). `reinject-state.sh` re-hydrates pipeline
state on session restart (SessionStart).

---

## Where new knowledge goes

Use this decision tree when you learn something the system should remember:

| Knowledge type | Where it goes |
|---|---|
| Universal (any project) — model-selection, Working Principles | `~/.claude/CLAUDE.md` (global) |
| Project-wide rule (cross-package) — branch format, code-quality bans, i18n | `.claude/rules/<topic>.md` AND add anchor citation to every agent that enforces it |
| Project-specific fact — DbContext name, env-var, fixture, port | `CLAUDE.md` (root, "Project facts" section) |
| Orchestration / routing rule | `.claude/CLAUDE.md` (Routing rules section) |
| Invocable workflow / slash command | new skill under `.claude/skills/<name>/SKILL.md` |
| Specialist with isolated context | new agent under `.claude/agents/<name>.md` |
| Deterministic LLM-less automation | hook script + registration in `.claude/settings.json` |
| Inter-agent contract | new schema under `.claude/schemas/<name>.v1.json` |

**A rule not cited anywhere is dead weight.** Every rule in `rules/` must
appear in the "Required rules" list of at least one agent or skill that
enforces / follows it.

---

## Citation conventions

- Format: `rules/<file>.md#<anchor>` (or `schemas/<file>.json` for schema refs).
- `<anchor>` is the slugified H2 heading — lowercase, non-alphanumeric runs collapsed to `-`.
- `validate-handoff.py` cross-checks every `rule_citations` entry in dev-handoff
  JSON against actual H2 anchors in the cited file. A typo (`rules/api-desing.md#x`)
  fails validation with closest-match suggestions.
- H3 anchors are NOT valid citation targets — only H2.

---

## Model selection

Per-project mapping (lifted from the global `~/.claude/CLAUDE.md` matrix):

| Agent | Model | Reason |
|---|---|---|
| `backend-dotnet`, `web-react`, `mobile-expo` | sonnet | Implementation from a clear spec. |
| `qa-tester` | opus | AC-met-or-not is a judgement call. |
| `pr-reviewer` (incl. fresh-eyes sub-reviewer) | opus | Highest-leverage gate. |
| `design-reviewer` | opus | Pre-impl review of architecture/scope/security. |
| `github-issues` | sonnet | Mechanical lifecycle work. |

Skill-internal: `root-cause-swarm` falsification probes → haiku;
`notion-docs` runs on the main thread (browser + Notion tools); its
page-text drafting sub-agents use sonnet.

---

## Pre-landing checks

Before committing changes to the `.claude/` folder, run:

1. **Schema validity** — `jq empty .claude/schemas/*.json` parses every schema.
2. **Citation resolution** — every `rules/*.md#anchor` reference in agent
   prompts and skill bodies resolves to an actual `## ` heading.
3. **Tools-vs-Steps audit** — for each agent, the `tools:` frontmatter list
   covers what its numbered Steps actually do (e.g. a step that says "write"
   needs `Edit` or `Write` in `tools:`).
4. **Schema additive-only** — modifying `.claude/schemas/*.json` should add
   optional fields, never remove required fields or enum values. Breaking
   changes need a new `*.v2.json` file alongside the v1.

The optional `bin/.claude-pre-landing.sh` script (Phase 25) bundles
checks 1–4 into one command.

---

## Adopting elsewhere

If you want to lift this setup into another project:

- **Keep as-is**: `rules/*.md`, `agents/{design-reviewer,qa-tester,pr-reviewer,github-issues}.md`,
  `hooks/*` (except `typecheck-on-{stop,submit}.sh` which are TS-specific),
  `schemas/*.json` (at minimum design/dev/qa/review handoffs), `settings.json`
  permission shape (deny + ask blocks).
- **Replace**: dev agents (`backend-dotnet` etc. are project-specific —
  swap for your stack's specialists). Project-fact section of root
  `CLAUDE.md`. Skill set (lift `root-cause-swarm` and `ui-tradeoff`; replace
  stack skills like `dotnet-feature` / `dotnet-debug` for your framework).
- **Re-evaluate**: `ship-epic` is GitHub-issue-driven; if your project
  doesn't use the epic-branch model, the skill needs heavy rewriting.
  Same for `notion-docs` (Notion-specific).

---

## Further reading

- Claude Code subagents: https://docs.claude.com/en/docs/claude-code/subagents
- Agent Skills: https://docs.claude.com/en/docs/agent-skills
- Memory & CLAUDE.md: https://docs.claude.com/en/docs/claude-code/memory
- Hooks: https://docs.claude.com/en/docs/claude-code/hooks
