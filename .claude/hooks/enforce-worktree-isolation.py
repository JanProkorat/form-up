#!/usr/bin/env python3
"""enforce-worktree-isolation.py — PreToolUse[Write|Edit|MultiEdit] hook.

Stop a subagent from editing package source in the MAIN checkout while
worktree-based parallel work is in flight.

WHY THIS EXISTS
  On 2026-08-16/17 a batch of eight issues ran in parallel, each dispatched
  to its own `.worktrees/<issue>-<slug>/` per rules/branch-and-pr.md. FOUR
  dev subagents nonetheless edited the MAIN checkout instead:

    - #902's agent edited main, then ran build+test against its worktree —
      so its first green run was measured against unmodified code. It caught
      that itself and re-applied, but only by luck of noticing.
    - #935's agent wrote its entire P1 production sweep (33 files) into main
      while its assigned worktree sat empty. Had it committed, a P1 timezone
      fix would have landed on a docs branch.
    - #897's agent did the same and reverted.
    - The stray files in main then poisoned an UNRELATED review: #798's
      blind reviewer invoked the `review` skill, which resolved paths against
      the session cwd, read main's uncommitted backend work, and returned
      findings about files belonging to a different issue entirely.

  The dispatch prompt naming the worktree is a suggestion; a `cd` at the top
  of a prompt is not a constraint. This hook is the constraint.

DETECTION
  Same subagent detection as deny-subagent-merge.py: Claude Code passes
  agent_id / agent_type / subagent_type in the payload when a subagent is
  running; transcripts under "/subagent/" are the fallback signal.

SCOPE — deliberately narrow, to avoid false denials
  Only three package roots are guarded: backend/, web/, mobile/. Subagents
  legitimately write elsewhere in the main checkout — most importantly the
  handoff JSONs under `.pipeline/state/`, plus `.qa-artifacts/`. Those are
  untouched.

  The guard only engages while at least one `.worktrees/*` exists. When no
  worktrees are in play, rules/branch-and-pr.md#serial-dispatch explicitly
  permits a sequential agent to reuse the main working tree, so denying
  would be wrong.

ESCAPE HATCH
  Set FP_ALLOW_MAIN_TREE_EDITS=1 to disable. Intended for a deliberate
  serial dispatch that must use main while unrelated worktrees happen to
  exist — not for routine use.

OUTPUT PROTOCOL
  Matches block-generated-client.py and deny-subagent-merge.py: a denial is
  one JSON object on stdout with hookSpecificOutput.permissionDecision =
  "deny", then exit 0. An allow writes nothing, also exit 0. The decision is
  carried by the JSON, never by the exit code.
"""
from __future__ import annotations

import json
import os
import sys
from datetime import date, datetime

LOG_DIR = os.path.join(os.environ.get("CLAUDE_PROJECT_DIR", "."), ".claude/hooks/log")

# Package roots that must only ever be edited inside a worktree while
# worktree-based parallel work is in flight. Keep this list tight — every
# entry is a potential false denial.
GUARDED_ROOTS = ("backend", "web", "mobile")

WORKTREES_DIR = ".worktrees"


def log(msg: str) -> None:
    os.makedirs(LOG_DIR, exist_ok=True)
    log_path = os.path.join(LOG_DIR, f"{date.today().isoformat()}.log")
    with open(log_path, "a") as f:
        f.write(msg + "\n")


def project_dir() -> str:
    return os.path.realpath(os.environ.get("CLAUDE_PROJECT_DIR", "."))


def is_subagent(payload: dict) -> bool:
    agent_id = payload.get("agent_id") or ""
    agent_type = payload.get("agent_type") or payload.get("subagent_type") or ""
    transcript = payload.get("transcript_path") or ""
    return bool(agent_id or agent_type) or "/subagent/" in transcript


def active_worktrees(root: str) -> list[str]:
    """Names of worktree directories currently present. Empty list means
    worktree-based parallel work is not in play, so the guard stays off."""
    wt_root = os.path.join(root, WORKTREES_DIR)
    if not os.path.isdir(wt_root):
        return []
    try:
        return sorted(
            name for name in os.listdir(wt_root)
            if os.path.isdir(os.path.join(wt_root, name)) and not name.startswith(".")
        )
    except OSError:
        return []


def guarded_root_for(file_path: str, root: str) -> str | None:
    """Return the guarded package root the path falls under in the MAIN
    checkout, or None if the path is fine.

    A path inside `<root>/.worktrees/<something>/backend/...` is fine — that
    is exactly where the agent is supposed to be writing. Only a path
    directly under `<root>/backend|web|mobile/` is a violation.
    """
    abs_path = os.path.realpath(
        file_path if os.path.isabs(file_path) else os.path.join(root, file_path)
    )

    try:
        rel = os.path.relpath(abs_path, root)
    except ValueError:
        return None

    # Outside the project entirely (scratchpad, /tmp, another repo) — not ours.
    if rel.startswith(os.pardir):
        return None

    parts = rel.split(os.sep)
    if not parts:
        return None

    # Inside a worktree — the correct place. Allow.
    if parts[0] == WORKTREES_DIR:
        return None

    if parts[0] in GUARDED_ROOTS:
        return parts[0]

    return None


def deny(file_path: str, pkg: str, agent_label: str, worktrees: list[str]) -> int:
    shown = ", ".join(worktrees[:6]) + ("…" if len(worktrees) > 6 else "")
    reason = (
        f"Blocked: '{file_path}' is package source in the MAIN checkout "
        f"('{pkg}/'), but {len(worktrees)} worktree(s) are active ({shown}).\n\n"
        "You were dispatched to work inside your own worktree under "
        ".worktrees/<issue>-<slug>/. Editing the main checkout instead means "
        "your commits can land on the wrong branch, your build/test runs may "
        "measure code you did not change, and your stray files leak into "
        "unrelated agents' reviews.\n\n"
        "Fix: re-run this edit against the SAME relative path inside your "
        "assigned worktree, and scope every subsequent command with "
        "`-C <worktree>` or cd there first. If you do not know which worktree "
        "is yours, stop and ask the orchestrator rather than guessing.\n\n"
        "This guard covers backend/, web/ and mobile/ only — writing your "
        "handoff JSON to .pipeline/state/ (relative path) is still fine. "
        "See rules/branch-and-pr.md#parallel-sub-agents-one-branch-each."
    )

    log(f"[{datetime.now().astimezone().isoformat()}] enforce-worktree-isolation: "
        f"DENIED agent={agent_label!r} path={file_path!r} pkg={pkg!r} "
        f"worktrees={len(worktrees)}")

    print(json.dumps({
        "hookSpecificOutput": {
            "hookEventName": "PreToolUse",
            "permissionDecision": "deny",
            "permissionDecisionReason": reason,
        }
    }))
    return 0


def main() -> int:
    if os.environ.get("FP_ALLOW_MAIN_TREE_EDITS") == "1":
        return 0

    try:
        payload = json.load(sys.stdin)
    except json.JSONDecodeError:
        return 0

    if not is_subagent(payload):
        return 0

    tool_input = payload.get("tool_input") or {}
    file_path = tool_input.get("file_path") or tool_input.get("path") or ""
    if not file_path:
        return 0

    root = project_dir()
    pkg = guarded_root_for(file_path, root)
    if pkg is None:
        return 0

    worktrees = active_worktrees(root)
    if not worktrees:
        # No parallel work in flight — serial dispatch may reuse main.
        return 0

    agent_label = (
        payload.get("agent_type")
        or payload.get("subagent_type")
        or payload.get("agent_id")
        or "unknown-subagent"
    )
    return deny(file_path, pkg, agent_label, worktrees)


if __name__ == "__main__":
    sys.exit(main())
