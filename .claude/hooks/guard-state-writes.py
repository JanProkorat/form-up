#!/usr/bin/env python3
"""guard-state-writes.py — only the main thread (conductor) may write shared
pipeline state. Subagents may only write their own handoff file.

WHEN DOES THIS RUN?
  This is a PreToolUse hook for Write and Edit tool calls.
  It only acts on files inside a ".pipeline/state/" directory — the main
  checkout's or a worktree's (`.claude/worktrees/<name>/` or
  `.worktrees/<issue>-<slug>/`).
  All other file writes pass through immediately without inspection.

WHY NOT .claude/state/?
  `.claude/` is a protected path: in auto mode every write there is prompted,
  even when a hook allows it. And an isolated worktree agent may not write the
  main checkout at all, so each worktree keeps its own state folder.

WHAT DOES IT DO?
  The pipeline uses a shared state file (pipeline.json) that only the conductor
  (main Claude session) should update. If a subagent could write it freely, it
  could corrupt or hijack the pipeline. This hook enforces:
    - Main thread: unrestricted writes to .pipeline/state/
    - Subagents: ONLY their own handoff file (see ACCEPTED FILENAMES below)
    - Everything else from a subagent: DENY

WHY HANDOFF FILES?
  Handoff files are how subagents communicate results back to the conductor.
  A subagent writes "handoff-developer.json" with its output; the conductor
  reads it, validates it, and updates pipeline.json itself.

ACCEPTED FILENAMES (and why the set is wider than the agent type alone)
  This repo's .claude/CLAUDE.md instructs two workflow agents to write a
  PER-ISSUE handoff — rule 5.5 names `state/handoff-design-<issue>.json` and
  rule 6.5 names `state/handoff-qa-<issue>.json`. Those use a SHORT STEM
  ("design", "qa") rather than the full agent type ("design-reviewer",
  "qa-tester"), so an agent-type-only match denied every documented write and
  the verdict silently fell back to the generic filename. Two concurrent QA
  dispatches would then overwrite each other's verdict.

  So each agent type maps to a small, fixed set of allowed stems (its own type
  plus any documented short form), and the filename may carry an optional
  `-WI-<N>` or `-<issue>` numeric suffix:

    handoff-qa-tester.json        handoff-qa-857.json
    handoff-design-reviewer.json  handoff-design-857.json
    handoff-backend-dotnet.json   handoff-backend-dotnet-WI-3.json

  The suffix is digits only, and the stems are per-agent-type, so this stays a
  strict widening: a subagent still cannot write pipeline.json, and cannot
  write a DIFFERENT agent's handoff (e.g. backend-dotnet cannot forge the QA
  verdict). If you add an agent whose documented handoff stem differs from its
  type, add it to AGENT_FILENAME_STEMS — do not loosen the pattern.

EXIT CODES (Claude Code hook contract):
  0 = allow the write to proceed
  (deny is communicated via stdout JSON — exit code stays 0)

Ported 1:1 from guard-state-writes.sh (an earlier project's .claude/hooks).
"""
from __future__ import annotations

import json
import os
import re
import sys
from datetime import date, datetime

# kit hooks run in many repos — anchor logs to CLAUDE_PROJECT_DIR (falls back to cwd, matching source when unset)
LOG_DIR = os.path.join(os.environ.get("CLAUDE_PROJECT_DIR", "."), ".claude/hooks/log")

# Documented short stems, per agent type — see ACCEPTED FILENAMES in the module
# docstring. The agent's own type is always allowed and does not need listing.
#
# These MUST stay in sync with the agent_type → prefix `case` in gate-check.sh
# (the SubagentStop schema validator), which locates a handoff by prefix:
#   backend-dotnet|web-react|mobile-expo → handoff-dev-
#   qa-tester                            → handoff-qa-
#   pr-reviewer                          → handoff-review-
#   design-reviewer                      → handoff-design-
# If the two lists disagree, the agent writes a name the validator never finds,
# gate-check logs "finished WITHOUT writing ..." and the handoff ships
# UNVALIDATED — a silent gate failure, not a visible error. Change both together.
AGENT_FILENAME_STEMS = {
    "backend-dotnet": ("dev",),
    "web-react": ("dev",),
    "mobile-expo": ("dev",),
    "qa-tester": ("qa",),
    "pr-reviewer": ("review",),
    "design-reviewer": ("design",),
}

# Optional trailing discriminator: a work-item id, or a GitHub issue number with
# an optional short qualifier. The qualifier is required by names this pipeline
# already produces — `handoff-review-865-pass4.json` (pr-reviewer's multi-pass
# reviews), `handoff-review-865-merge.json`, `handoff-qa-854-e2e.json`,
# `handoff-dev-778-web.json` (the web slice of a cross-package issue). Digits
# only would deny all of those and push agents back onto the shared filename.
# Kept to a short alphanumeric run: no dots, separators, or path characters.
SUFFIX_PATTERN = r"(?:-WI-[0-9]+|-[0-9]+(?:-[A-Za-z0-9]{1,20})?)?"


def allowed_stems(agent_type: str, agent_id: str) -> list[str]:
    """Filename stems this caller may write, most-specific first.

    Only non-empty identifiers are returned — an empty agent_type must never
    collapse the pattern into `handoff-<anything>.json`.
    """
    stems: list[str] = []
    for candidate in (agent_type, *AGENT_FILENAME_STEMS.get(agent_type, ()), agent_id):
        if candidate and candidate not in stems:
            stems.append(candidate)

    return stems


STATE_SUBDIR = os.path.join(".pipeline", "state")
# Worktree roots in use: Claude Code's `.claude/worktrees/<name>` and the pipeline's `.worktrees/<issue>-<slug>`.
_WORKTREE_STATE = re.compile(r"/(?:\.claude/worktrees|\.worktrees)/[^/]+/\.pipeline/state/")
_WORKTREE_MARKER = re.compile(r"/(?:\.claude/worktrees|\.worktrees)/")


def main_checkout(project_dir: str) -> str:
    """The main checkout root, also when the session itself runs in a worktree."""
    real = os.path.realpath(project_dir)
    return _WORKTREE_MARKER.split(real, 1)[0]


def is_state_path(real_path: str, project_dir: str) -> bool:
    """True for a file under <main>/.pipeline/state/ or a worktree's <wt>/.pipeline/state/."""
    if not real_path:
        return False
    root = main_checkout(project_dir)
    if real_path.startswith(os.path.join(root, STATE_SUBDIR) + os.sep):
        return True
    rest = real_path[len(root):] if real_path.startswith(root + os.sep) else ""
    return bool(_WORKTREE_STATE.match(rest))


def log(msg: str) -> None:
    os.makedirs(LOG_DIR, exist_ok=True)
    log_path = os.path.join(LOG_DIR, f"{date.today().isoformat()}.log")
    with open(log_path, "a") as f:
        f.write(msg + "\n")


def main() -> int:
    try:
        payload = json.load(sys.stdin)
    except json.JSONDecodeError:
        return 0

    tool_input = payload.get("tool_input") or {}
    file_path = tool_input.get("file_path") or tool_input.get("path") or ""
    agent_id = payload.get("agent_id") or ""
    agent_type = payload.get("agent_type") or payload.get("subagent_type") or ""

    # --- NORMALIZE THE PATH (defense against path-traversal tricks) ---
    # realpath canonicalises the path (resolves .., duplicate slashes, follows
    # existing symlinks) without requiring the file to exist yet — matches the
    # source script's `realpath -m` semantics.
    real_path = os.path.realpath(file_path) if file_path else ""
    project_dir = os.environ.get("CLAUDE_PROJECT_DIR") or os.getcwd()

    # --- EARLY EXIT: Only act on writes under a pipeline state directory.
    # Strict PREFIX check against the normalised path, not a substring match.
    if not is_state_path(real_path, project_dir):
        return 0

    # --- CHECK: Is this the main thread (conductor)? ---
    if not agent_id and not agent_type:
        return 0

    # --- CHECK: Is the subagent writing only its own handoff file? ---
    # Match on the BASENAME (never on an arbitrary tail of the full path) and
    # require the exact filename shape `handoff-<type>.json` or
    # `handoff-<type>-WI-<N>.json`.
    # The agent_id fallback exists because some Claude Code versions put the id,
    # not the type, in the handoff filename.
    basename = os.path.basename(real_path)
    stems = allowed_stems(agent_type, agent_id)
    expected = f"handoff-{stems[0]}.json" if stems else "handoff-<agent-type>.json"

    # re.escape() intentionally hardens the source's unescaped-ERE interpolation
    # (bash spliced $AGENT_TYPE straight into the regex) against regex-metacharacter injection.
    for stem in stems:
        if re.match(rf"^handoff-{re.escape(stem)}{SUFFIX_PATTERN}\.json$", basename):
            return 0

    # --- DENY: The subagent is trying to write a state file it doesn't own ---
    log(f"[{datetime.now().astimezone().isoformat()}] guard-state: DENY "
        f"{file_path!r} from agent={agent_type!r} id={agent_id!r}")

    print(json.dumps({
        "hookSpecificOutput": {
            "hookEventName": "PreToolUse",
            "permissionDecision": "deny",
            "permissionDecisionReason": (
                f"Subagents may only write to .pipeline/state/{expected} "
                "(optionally suffixed with a work-item or issue number, e.g. "
                f"{expected.removesuffix('.json')}-857.json); "
                "all other state mutations go through the conductor."
            ),
        }
    }))
    return 0


if __name__ == "__main__":
    sys.exit(main())
