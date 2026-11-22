# Interactive QA playbook (orchestrator main thread)

Loaded on demand from `.claude/CLAUDE.md` routing rule 6.5.

The `xcodebuildmcp` tool server is disabled in `.claude/settings.json`
while the iOS path is paused — re-enable it there when resuming.

Use when `qa-tester` returns ⚠️ INTERACTIVE-REQUIRED. The orchestrator's main
thread has the MCP tool surface the sub-agent lacks (`mcp__xcodebuildmcp__*`
ui-automation, `mcp__plugin_playwright_playwright__*`,
`mcp__a11y-accessibility__*`). For each AC the qa-tester flagged with
`met: false` and an interactive-evidence note:

- **iOS native flows** — **PAUSED since #1160** (the new app has no
  dev-client build script or test sign-in yet; see the notice in
  `agents/qa-tester.md`). When resumed — load the schemas:
  `ToolSearch select:mcp__xcodebuildmcp__list_sims,boot_sim,install_app_sim,launch_app_sim,stop_app_sim,screenshot,snapshot_ui,tap,type_text,swipe,gesture,button,long_press`.
  Resolve the simulator via `list_sims` (precedence: booted → config-
  name → newest installed). Use the dev-client `.app` from
  `mobile/.qa-cache/<sha>.app` (built fresh by qa-tester in step C of
  its iOS path). Drive via `snapshot_ui` → `tap` by accessibility id
  → `type_text` / `swipe` as the AC requires. Capture evidence to
  `.qa-artifacts/<issue>/orchestrator-<scene>.png`. If the iOS
  "Open in App?" prompt appears after `xcrun simctl openurl`, snapshot
  and tap the "Otevřít" / "Open" button before proceeding.

- **Web spec drive** — load the schemas:
  `ToolSearch select:mcp__plugin_playwright_playwright__browser_navigate,browser_click,browser_fill_form,browser_snapshot,browser_take_screenshot,browser_wait_for,browser_evaluate`.
  Point at `:5173` (which proxies to compose harness `:5101`). Pull
  auth from `.auth/<role>.json` (produced by `web/tests/e2e/auth.setup.ts`)
  (the `mobile/scripts/qa-fetch-refresh-token.sh` alternative is paused
  with the iOS path). Capture accessibility-tree snapshots + screenshots
  under `.qa-artifacts/<issue>/orchestrator-web-<scene>.png`.

- **a11y audits** — load the schemas:
  `ToolSearch select:mcp__a11y-accessibility__test_accessibility,test_html_string,check_aria_attributes,check_color_contrast`.
  Run after the interactive drive lands on the target screen.

- **Consolidation** — write the orchestrator's findings as a final
  section in `.pipeline/state/handoff-qa-<issue>.json` (extend the existing file
  in place, do not rewrite the qa-tester sub-agent's portion). Update
  `verdict` from `INTERACTIVE-REQUIRED` to `PASS` if all flagged ACs
  are now verified, `FAIL` if any interactive check shows a defect, or
  keep `PARTIAL` if some ACs remain blocked on fixture gaps.

- **Teardown** — same rule as qa-tester step 8: leave a pre-booted
  user-owned simulator running, only shut down sims the orchestrator
  itself booted. Uninstall the dev-client `.app` either way.

This playbook is also the path for ad-hoc smoke tests the user asks for
directly ("run the deep-link bypass against the booted sim"), without
going through a full qa-tester dispatch.
