---
paths:
  - ".pipeline/state/**"
description: What "verified" means at the common layer — the per-stack `<stack>-verify` seam, the scope→stack map, and the reporting discipline every pack inherits
---

# Verification Contract

Cite an anchor here whenever you report verification status. Working
Principles §2 ("verify before declaring done") is enforced by this contract:
the common layer defines the discipline, and each stack pack defines the
actual commands.

## What "verification" means

**Verification is invoking a stack's `<stack>-verify` skill.** A repo can
adopt more than one pack (e.g. a `dotnet` pack for `api/**` and a `react`
pack for `app/**`). Every stack pack under `packs/<stack>/` ships a
`<stack>-verify` skill (and a `<stack>-build` skill for the compile-only
floor) named after its `pack.json.stack` value — e.g. the `dotnet` pack
provides `dotnet-verify`/`dotnet-build`. Each owns the concrete build/test
commands, the tool-name vocabulary, known-failure allowlists, and any
stack-specific caveats for that one stack. The common layer intentionally
names none of them here — naming a concrete build/test tool in this file
would break the stack-agnostic boundary the hub depends on.

**Which stack(s) a work item touches** is not something a common agent
guesses at: the repo's own `CLAUDE.md` declares a scope→stack path map (e.g.
`api/**→dotnet`, `app/**→react`). Match the WI's `files_touched` against that
map and run the named stack's `<stack>-verify`/`<stack>-build` skill for
**each** stack it touches — a WI spanning `api/**` and `app/**` runs both the
`dotnet` and the `react` pack's verify skill. This is a documented
convention the agent applies with judgment (map lookup, not automation) —
never invent a stack from the file extension alone if the repo's map says
otherwise.

If you are working in a repo and don't know what a stack's `<stack>-verify`
runs, find and read that pack's own skill definition before claiming
anything is verified. Do not substitute a command you happen to remember
from a different repo or a different stack.

## Stack verify skills

For each stack a WI touches, that stack's `<stack>-verify` skill is the sole
authority on:

- which command(s) constitute the verification floor (the minimum required
  for *every* work item — typically a compile/build-only check, run via the
  same pack's `<stack>-build` skill) versus a fuller run (compile plus a
  scoped or full test pass, required when the work item changes behavior);
- the exact invocation syntax, including how to scope a run to a subset of
  tests without accidentally running (or silently skipping) the whole suite;
  a wrong or unsupported filter form can make a scoped run report full-suite
  results without any visible error, which is worse than an obvious failure;
- any commands or gates that are known-currently-broken for reasons unrelated
  to the work at hand (a transitive dependency advisory, a pre-existing
  warning backlog, a flaky environment precondition) — and, symmetrically,
  which failures are *not* pre-existing and are therefore the work item's to
  fix;
- environment prerequisites that turn a real failure into a false alarm if
  unmet (a required background service, a required auth claim, a required
  fixture shape) — Working Principles §1: decide bad data/bad environment vs.
  buggy code before theorizing, and that stack's `<stack>-verify` is where
  the decision tree for that stack lives.

A pack's `<stack>-verify` skill is the load-bearing seam between this
stack-agnostic rule and any actual tool invocation. If a stack in the repo's
scope→stack map has no adopted pack, or its pack has no `<stack>-verify`
skill yet, that is a gap in onboarding/the pack, not license to invent a
command ad hoc.

## Reporting discipline

This discipline applies regardless of which pack or which command ran:

- **Read the full output**, not just the exit code or the last few lines. A
  scoped run that silently fell back to running (and passing) the entire
  suite is not evidence the scoped thing works; a truncated log that cuts off
  before the summary is not evidence of anything.
- **Never claim a pass on partial evidence.** An unrun check is a failure to
  report, never an omitted-and-assumed pass. If you didn't run it, say so and
  report it as not passed — don't leave the field blank and let a reader
  infer success.
- **Name the exact command you ran**, including any filter/scope argument, in
  whatever verification-output field the handoff schema provides. A reviewer
  or the next agent in the pipeline must be able to reconstruct exactly what
  ran without guessing.
- **A mismatch between a claimed result and a freshly re-run, observed
  result is a critical finding** — whoever re-verifies (a reviewer, a gate
  hook) re-runs the command fresh and reads full output; they do not take the
  claim on faith. Treat your own claim with the same skepticism before you
  write it down.

## Declaring the result

Handoff schemas (see `common/schemas/`) carry a `verification` object with a
free-form `tool` string — free-form specifically so each stack pack can
populate it with its own vocabulary without a schema change. Populate `tool`
with the exact command family you ran (as that stack's `<stack>-verify` names
it, not a paraphrase) and report the outcome you actually observed. When a WI
touches more than one stack, name every stack's command in the free-form
`verification_output` field and let `passed` reflect the AND of all of them
— one stack failing means the WI is not verified, regardless of the others.
