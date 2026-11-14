# pr-reviewer — Step 6: orchestrator-facing verdict template

> On-demand section of `.claude/agents/pr-reviewer.md`, moved verbatim (#1214).

Structure exactly like this so the orchestrator can parse:

```
OVERALL: ✅ READY FOR MERGE  (or 🔁 NEEDS REWORK, or BLOCKED,
                              or NEEDS SECURITY REVIEW)

PR: <url>
Branch: <branch>
Base: <develop | main>
Tier: <standalone | epic-level>
Labels: <kind>, <package…>, <priority>
Merge strategy: --squash | (excluded — human merges)

Self-review (first pass — pr-reviewer):
  Verdict: ✅ CLEAN | 🔁 NEEDS REWORK (short-circuited, did not run second pass)
  Summary: <one paragraph of what you saw in the diff>

Sub-reviewer (second pass — fresh-eyes Agent):
  Verdict: ✅ READY | 🔁 NEEDS REWORK | (not run — self-review short-circuited)
  Summary: <paste the sub-reviewer's one-paragraph Summary verbatim — shows the
            orchestrator what an external reader inferred from the code>

BLOCKING findings from EITHER pass (routed by scope):
  [scope:backend]
    - (pass: self | sub) <file:line> — <what to fix>
  [scope:web]
    - (pass: self | sub) <file:line> — <what to fix>
  [scope:mobile]
    - (pass: self | sub) <file:line> — <what to fix>

NITs (not blocking — surface to orchestrator for optional follow-up):
  - (pass: self | sub) ...

Questions for the dev agent (non-blocking):
  - (pass: self | sub) ...

Hard-rule gate hits (union of both passes):
  - <none | list>

Merge exclusion check:
  - base branch = main             <✅ no | ❌ yes — excluded, human-only>
  - Mongo data-mutation script     <✅ no | ❌ yes — excluded, human-only>
  - one-shot data command          <✅ no | ❌ yes — excluded, human-only>
  - EF migrations (not excluded)   <none | list each backend/**/Migrations/** file>
  - user opted out this turn       <✅ no | ❌ yes>

Recommended next step:
  - Route fix list to <backend-dotnet | web-react | mobile-expo>,
    then run a rework round per rule 7d: qa-tester ∥ pr-reviewer
    (mode: re-review), both with since: <this verdict's head>.
  OR
  - ✅ Ready to merge — epic-level / standalone PR. Orchestrator
    should report the PR URL to the user and wait for same-turn
    authorization, then re-dispatch me in mode: merge.
```
