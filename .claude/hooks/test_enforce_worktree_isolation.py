#!/usr/bin/env python3
"""Tests for enforce-worktree-isolation.py.

Run: python3 .claude/hooks/test_enforce_worktree_isolation.py

Each case drives the hook the way Claude Code does — JSON on stdin, decision
on stdout — against a temporary project dir, so the assertions exercise the
real path logic rather than a re-implementation of it.
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile

HOOK = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                    "enforce-worktree-isolation.py")

FAILURES: list[str] = []


def run(payload: dict, project_dir: str, env_extra: dict | None = None) -> dict | None:
    env = {**os.environ, "CLAUDE_PROJECT_DIR": project_dir}
    env.pop("FP_ALLOW_MAIN_TREE_EDITS", None)
    if env_extra:
        env.update(env_extra)
    proc = subprocess.run(
        [sys.executable, HOOK],
        input=json.dumps(payload),
        capture_output=True,
        text=True,
        env=env,
    )
    assert proc.returncode == 0, f"hook must always exit 0, got {proc.returncode}"
    out = proc.stdout.strip()
    return json.loads(out) if out else None


def decision(result: dict | None) -> str:
    if result is None:
        return "allow"
    return result["hookSpecificOutput"]["permissionDecision"]


def check(name: str, actual: str, expected: str) -> None:
    if actual == expected:
        print(f"  ok   {name}")
    else:
        print(f"  FAIL {name}: expected {expected}, got {actual}")
        FAILURES.append(name)


def make_project(with_worktree: bool) -> str:
    root = tempfile.mkdtemp(prefix="fp-hook-test-")
    for d in ("backend", "web", "mobile", ".pipeline/state"):
        os.makedirs(os.path.join(root, d), exist_ok=True)
    if with_worktree:
        os.makedirs(os.path.join(root, ".worktrees", "935-client-timezone", "backend"),
                    exist_ok=True)
    return root


SUBAGENT = {"agent_type": "backend-dotnet", "agent_id": "a123"}


def main() -> int:
    print("enforce-worktree-isolation")

    wt = make_project(with_worktree=True)
    no_wt = make_project(with_worktree=False)

    # The core regression: a dev subagent writing package source into main
    # while a worktree is active.
    check("denies subagent editing main backend/ when a worktree exists",
          decision(run({**SUBAGENT, "tool_input": {
              "file_path": os.path.join(wt, "backend/FormUp.Application/Foo.cs")}}, wt)),
          "deny")

    check("denies main web/ too",
          decision(run({**SUBAGENT, "tool_input": {
              "file_path": os.path.join(wt, "web/src/pages/Foo.tsx")}}, wt)),
          "deny")

    check("denies main mobile/ too",
          decision(run({**SUBAGENT, "tool_input": {
              "file_path": os.path.join(wt, "mobile/app/foo.tsx")}}, wt)),
          "deny")

    # The correct destination must stay open, or the hook breaks the pipeline.
    check("allows the same relative path INSIDE the worktree",
          decision(run({**SUBAGENT, "tool_input": {"file_path": os.path.join(
              wt, ".worktrees/935-client-timezone/backend/FormUp.Application/Foo.cs")}}, wt)),
          "allow")

    # Handoffs go to .pipeline/state/ — the main checkout's or the agent's own worktree's.
    check("allows handoff JSON in main .pipeline/state/",
          decision(run({**SUBAGENT, "tool_input": {
              "file_path": os.path.join(wt, ".pipeline/state/handoff-dev-935.json")}}, wt)),
          "allow")
    check("allows handoff JSON in the worktree's own .pipeline/state/",
          decision(run({**SUBAGENT, "tool_input": {
              "file_path": os.path.join(wt, ".worktrees/935-client-timezone/.pipeline/state/handoff-dev-935.json")}}, wt)),
          "allow")

    # rules/branch-and-pr.md#serial-dispatch permits reusing main when no
    # worktrees are in play.
    check("allows main backend/ when NO worktrees exist",
          decision(run({**SUBAGENT, "tool_input": {
              "file_path": os.path.join(no_wt, "backend/Foo.cs")}}, no_wt)),
          "allow")

    # The main thread is never filtered.
    check("allows main thread (no agent identifiers)",
          decision(run({"tool_input": {
              "file_path": os.path.join(wt, "backend/Foo.cs")}}, wt)),
          "allow")

    # Transcript-path fallback detection.
    check("detects subagent via /subagent/ transcript path",
          decision(run({"transcript_path": "/x/subagent/abc.jsonl", "tool_input": {
              "file_path": os.path.join(wt, "backend/Foo.cs")}}, wt)),
          "deny")

    check("escape hatch disables the guard",
          decision(run({**SUBAGENT, "tool_input": {
              "file_path": os.path.join(wt, "backend/Foo.cs")}}, wt,
              {"FP_ALLOW_MAIN_TREE_EDITS": "1"})),
          "allow")

    # Paths outside the project (scratchpad, /tmp) are not ours to police.
    check("allows paths outside the project dir",
          decision(run({**SUBAGENT, "tool_input": {
              "file_path": "/tmp/scratch/backend/Foo.cs"}}, wt)),
          "allow")

    check("allows unguarded top-level dirs (docs/)",
          decision(run({**SUBAGENT, "tool_input": {
              "file_path": os.path.join(wt, "docs/PROGRESS.md")}}, wt)),
          "allow")

    check("no file_path is a no-op",
          decision(run({**SUBAGENT, "tool_input": {}}, wt)),
          "allow")

    print()
    if FAILURES:
        print(f"{len(FAILURES)} FAILED: {', '.join(FAILURES)}")
        return 1
    print("all passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
