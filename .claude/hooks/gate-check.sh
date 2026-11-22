#!/usr/bin/env bash
# gate-check.sh — SubagentStop hook.
#
# Runs after every Agent dispatch returns to the orchestrator. If the
# subagent wrote a handoff file under .pipeline/state/handoff-*.json,
# validate it against its declared schema before letting control flow
# back. A schema-violating handoff exits non-zero so the agent sees
# the error and self-corrects.
#
# How we find the handoff:
#   The hook receives a JSON payload on stdin carrying agent_id /
#   agent_type / transcript_path. We look for a handoff file in
#   .pipeline/state/ that matches the agent_type prefix AND was written
#   during this agent's run. If none exists we exit cleanly — not every
#   dispatch produces a handoff (ad-hoc Explore queries, or a dispatch
#   the orchestrator told to reply in prose).
#
# Why the run-window check (#910):
#   This used to take whichever prefix-matching file had the newest
#   mtime, with no check that the agent had actually written it. Two
#   consequences, both observed in this repo's own log:
#     - a dispatch that writes no handoff gets gated against a NEIGHBOUR's
#       file (2026-08-06: a pr-reviewer run on PR #908 validated
#       handoff-review-894.json; a qa-tester run on #906 validated
#       handoff-qa-898.json);
#     - conversely a PASS proved nothing about the current agent, and one
#       invalid file left on disk blocked every later dispatch of that
#       agent type until someone noticed.
#   The payload carries no issue number, so we cannot key on the issue
#   directly. We use the transcript's birth time as the agent's start
#   instead: anything the agent wrote must be at least that new. Where the
#   filesystem cannot report a birth time we say so in the log rather than
#   silently falling back to the old guess.

set -euo pipefail

PROJECT_DIR="${CLAUDE_PROJECT_DIR:-$(pwd)}"
LOG_DIR="$PROJECT_DIR/.claude/hooks/log"
mkdir -p "$LOG_DIR"
LOG="$LOG_DIR/$(date +%F).log"

INPUT="$(cat)"

# Extract agent type/id (best effort — fields may be absent on older Claude
# Code versions). Sanitise so a hostile value can't reach the shell.
AGENT_TYPE="$(printf '%s' "$INPUT" | jq -r '.agent_type // .subagent_type // empty' 2>/dev/null | tr -dc '[:alnum:]_-' | head -c 50)"
AGENT_ID="$(printf '%s'   "$INPUT" | jq -r '.agent_id // empty'                       2>/dev/null | tr -dc '[:alnum:]_-' | head -c 80)"
TRANSCRIPT="$(printf '%s' "$INPUT" | jq -r '.transcript_path // empty'                 2>/dev/null)"

# Map agent type → handoff filename prefix. Unknown agent types skip.
case "$AGENT_TYPE" in
    backend-dotnet|web-react|mobile-expo)
        prefix="handoff-dev-"
        ;;
    qa-tester)
        prefix="handoff-qa-"
        ;;
    pr-reviewer)
        prefix="handoff-review-"
        ;;
    design-reviewer)
        prefix="handoff-design-"
        ;;
    *)
        # No matching agent type — main thread, Explore, general-purpose, etc.
        # Nothing to validate.
        exit 0
        ;;
esac

# Handoffs live in .pipeline/state/ of the checkout the agent worked in: the main
# checkout, or a worktree (.claude/worktrees/<name> or .worktrees/<issue>-<slug>).
# An isolated worktree agent cannot write the main checkout, so each worktree has
# its own folder. Look in the agent's own worktree first (payload cwd), and scan
# the main checkout and every worktree only when that finds nothing.
WT_PATTERN='/(\.claude/worktrees|\.worktrees)/'
MAIN_DIR="$(printf '%s' "$PROJECT_DIR" | sed -E "s#${WT_PATTERN}.*##")"
CWD="$(printf '%s' "$INPUT" | jq -r '.cwd // empty' 2>/dev/null)"
OWN_STATE=""
if printf '%s' "$CWD" | grep -qE "$WT_PATTERN"; then
    OWN_STATE="$(printf '%s' "$CWD" | sed -E "s#(${WT_PATTERN}[^/]+).*#\\1#")/.pipeline/state"
fi

# mtime of $1 via BSD (macOS) then GNU stat. Empty if neither works.
file_mtime() {
    stat -f %m "$1" 2>/dev/null || stat -c %Y "$1" 2>/dev/null || printf ''
}

# Birth time of the agent's transcript ≈ when the agent started. BSD reports it
# as %B; GNU's %W is 0 or - on filesystems that don't record one. Empty when
# unavailable, which the caller must treat as "cannot scope", not as "0".
AGENT_STARTED=""
if [ -n "$TRANSCRIPT" ] && [ -f "$TRANSCRIPT" ]; then
    birth="$(stat -f %B "$TRANSCRIPT" 2>/dev/null || stat -c %W "$TRANSCRIPT" 2>/dev/null || printf '')"
    case "$birth" in
        ''|0|-) ;;                    # unsupported — leave AGENT_STARTED empty
        *) AGENT_STARTED="$birth" ;;
    esac
fi

# Pick the newest prefix-matching handoff that this agent could plausibly have
# written. Allowing 2s of clock skew keeps a handoff written in the agent's very
# first moments from being excluded.
LATEST=""
LATEST_MTIME=0
SKIPPED_STALE=0

scan_state_dir() { # $1 = a .pipeline/state directory
    local f mtime
    [ -d "$1" ] || return 0
    for f in "$1"/${prefix}*.json; do
        [ -f "$f" ] || continue
        mtime="$(file_mtime "$f")"
        [ -n "$mtime" ] || continue
        if [ -n "$AGENT_STARTED" ] && [ "$mtime" -lt "$((AGENT_STARTED - 2))" ]; then
            # Predates this agent — it belongs to some other issue's run.
            SKIPPED_STALE=$((SKIPPED_STALE + 1))
            continue
        fi
        if [ "$mtime" -gt "$LATEST_MTIME" ]; then
            LATEST_MTIME="$mtime"
            LATEST="$f"
        fi
    done
}

shopt -s nullglob
[ -n "$OWN_STATE" ] && scan_state_dir "$OWN_STATE"
if [ -z "$LATEST" ]; then
    # Fallback when the payload cwd is not the agent's worktree. A parallel agent
    # of the same type in another worktree could match here; the log names the file.
    for d in "$MAIN_DIR/.pipeline/state" "$MAIN_DIR"/.claude/worktrees/*/.pipeline/state "$MAIN_DIR"/.worktrees/*/.pipeline/state; do
        [ "$d" = "$OWN_STATE" ] && continue
        scan_state_dir "$d"
    done
fi
shopt -u nullglob

if [ -z "$AGENT_STARTED" ]; then
    printf '[%s] gate-check: %s — no transcript birth time available, cannot scope the handoff to this run; result below may belong to another issue\n' \
        "$(date -Iseconds)" "${AGENT_TYPE:-unknown}" >> "$LOG"
fi

if [ -z "$LATEST" ]; then
    # Nothing this agent wrote. Not an error: plenty of dispatches are told to
    # reply in prose. Distinguish it in the log from "nothing on disk at all",
    # so a genuinely missing handoff is still visible.
    if [ "$SKIPPED_STALE" -gt 0 ]; then
        printf '[%s] gate-check: %s wrote no handoff; ignored %d older %s*.json from earlier runs (not this agent to answer for)\n' \
            "$(date -Iseconds)" "${AGENT_TYPE:-unknown}" "$SKIPPED_STALE" "$prefix" >> "$LOG"
    else
        printf '[%s] gate-check: %s finished WITHOUT writing %s*.json — orchestrator may proceed but flag missing handoff to next dispatch\n' \
            "$(date -Iseconds)" "${AGENT_TYPE:-unknown}" "$prefix" >> "$LOG"
    fi
    exit 0
fi

# Validate via Python validator. Capture both stdout (success) and
# stderr (failure diagnostics). On non-zero exit, propagate the error
# to Claude Code so the agent sees it and self-corrects.
if python3 "$PROJECT_DIR/.claude/hooks/validate-handoff.py" "$LATEST" 1>/dev/null 2>/tmp/gate-check.err; then
    printf '[%s] gate-check: %s OK (%s)\n' \
        "$(date -Iseconds)" "${AGENT_TYPE:-unknown}" "$(basename "$LATEST")" >> "$LOG"
    rm -f /tmp/gate-check.err
    exit 0
else
    EXIT_CODE=$?
    printf '[%s] gate-check: %s REJECTED (%s)\n' \
        "$(date -Iseconds)" "${AGENT_TYPE:-unknown}" "$(basename "$LATEST")" >> "$LOG"
    cat /tmp/gate-check.err >&2
    cat /tmp/gate-check.err >> "$LOG"
    rm -f /tmp/gate-check.err
    # Propagate failure so Claude Code surfaces the error to the agent.
    exit "$EXIT_CODE"
fi
