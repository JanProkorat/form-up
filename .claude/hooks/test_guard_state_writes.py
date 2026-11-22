#!/usr/bin/env python3
"""Tests for guard-state-writes.py: which writes count as pipeline state, and who may write them."""
from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile

HOOK = os.path.join(os.path.dirname(os.path.abspath(__file__)), "guard-state-writes.py")
FAILURES: list[str] = []


def run(file_path: str, project: str, agent_type: str = "qa-tester") -> str:
    payload = {"tool_name": "Write", "tool_input": {"file_path": file_path}}
    if agent_type:
        payload |= {"agent_type": agent_type, "agent_id": "a1"}
    out = subprocess.run(
        [sys.executable, HOOK], input=json.dumps(payload), capture_output=True, text=True,
        env={**os.environ, "CLAUDE_PROJECT_DIR": project},
    ).stdout.strip()
    return json.loads(out)["hookSpecificOutput"]["permissionDecision"] if out else "allow"


def check(name: str, actual: str, expected: str) -> None:
    if actual == expected:
        print(f"  ok   {name}")
    else:
        print(f"  FAIL {name}: expected {expected}, got {actual}")
        FAILURES.append(name)


def main() -> int:
    root = os.path.realpath(tempfile.mkdtemp(prefix="fp-guard-test-"))
    wt_claude = os.path.join(root, ".claude/worktrees/wt-a")
    wt_pipeline = os.path.join(root, ".worktrees/2000-other")
    print("guard-state-writes.py")

    for label, base in (("main", root), ("claude worktree", wt_claude), ("pipeline worktree", wt_pipeline)):
        state = os.path.join(base, ".pipeline/state")
        check(f"{label}: sub-agent may write its own handoff",
              run(os.path.join(state, "handoff-qa-857.json"), root), "allow")
        check(f"{label}: sub-agent may not write pipeline.json",
              run(os.path.join(state, "pipeline.json"), root), "deny")
        check(f"{label}: sub-agent may not forge another agent's handoff",
              run(os.path.join(state, "handoff-review-857.json"), root), "deny")
        check(f"{label}: main thread may write any state file",
              run(os.path.join(state, "pipeline.json"), root, agent_type=""), "allow")

    check("session running inside a worktree still guards that worktree's state",
          run(os.path.join(wt_claude, ".pipeline/state/pipeline.json"), wt_claude), "deny")
    check("files outside .pipeline/state are not inspected",
          run(os.path.join(wt_claude, "web/src/x.ts"), root), "allow")
    check("old .claude/state is no longer guarded",
          run(os.path.join(root, ".claude/state/pipeline.json"), root), "allow")
    check("a '..' path that resolves outside state is not inspected",
          run(os.path.join(root, ".pipeline/state/../pipeline.json"), root), "allow")

    print()
    if FAILURES:
        print(f"{len(FAILURES)} FAILED")
        return 1
    print("all passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
