# Guide walk (orchestrator main thread)

Claude tests the epic in a real browser before the maintainer does (decided 2026-11-18, after #592:
10 of the maintainer's 15 notes were bugs, layout defects or wrong copy a browser pass would have found).
Loaded from `ship-epic` Phase 2 step 2b and from the `test-guide` skill. Main thread only: subagents
can't reach the Playwright tools or the claude.ai guide page.

**Done when** every `ready` step in the guide has `by: "claude"` with `pass` or `fail`, or carries a
`yourCheck` saying why only the maintainer can judge it, and every `fail` has been fixed or filed.

## 1. Stack

The walk runs on the **dev stack**, like the maintainer's own testing. The compose harness has no
Stripe keys and no admin account, so it can't run billing flows.

- **Reuse what is running.** If the maintainer's backend (:5000/:5001), web (:5173) and `stripe listen` are
  already up, check that the backend started after the epic branch's last commit (`ps -o lstart`), then
  use them and leave them running. Start only what is missing, and stop only what you started.
- Check out the epic branch in the main checkout (`git switch` + `git pull --ff-only`).
- Backend: `dotnet run` in `backend/FormUp.Application`, in the background. Stripe test keys come from
  the gitignored `Properties/launchSettings.json` (`Stripe__SecretKey`, `Stripe__WebhookSecret`).
  Never print or copy them.
- Web: `npm run dev` in `web`, in the background (http://localhost:5173).
- Stripe relay: the `stripe listen` line from the guide's `meta/guide.setup`, in the background. Only
  for epics that touch billing.
- Restart the backend after every fix merge, as the setup lines say.

Before stopping the walk, stop the processes you started, and free their ports.

**Sign-in rate limit.** Register, login, verify-email and password endpoints share one limit: 10 calls
per 15 minutes per IP (`Program.cs` `AppPolicies.AuthRateLimit`). Refresh has its own, looser limit
(`AppPolicies.RefreshRateLimit`, 120 per 15 minutes). The walk seed (§2) signs every account in without
a login call, so the walk normally never touches the auth limit. Don't run the backend with
`RateLimiting__Disabled=true`: the walk should test the real limits.

## 2. Accounts

Test accounts only: never the maintainer's own password for anything.

**Prepare them with one command before the walk** (#2228), with the dev backend's database and Stripe
test keys from the gitignored `Properties/launchSettings.json` `local` profile:

```bash
cd backend/FormUp.Application && dotnet run --launch-profile local -- --walk-seed
```

It refuses unless the environment is Development or Local and the Stripe key starts with `sk_test_`, and
it needs Pro priced in Stripe (admin Plans page). It creates or resets these accounts, all verified, and takes under a minute:

| Key | State |
|---|---|
| `admin` | Admin role |
| `free` | Trainer on the Free plan |
| `pro` | Trainer paying Pro, real Stripe sandbox subscription, card without 3D Secure (admin Change plan / End plan refunds work) |
| `pro3ds` | Trainer paying Pro; later charges need 3D Secure (Add role flow) |
| `given` | Trainer on a Pro plan given by Form Up |
| `manyclients` | Trainer paying Pro with 3 linked clients (Free allows 2) |

- It writes `.claude/private/walk-accounts.json` (gitignored):
  `{ "admin": {email, password}, "coaches": [{key, email, password, note}] }`. Read it with `Read`; never
  echo a password into chat, the guide, a commit or a screenshot caption.
- It also writes a signed-in session per account to `.qa-artifacts/walk-sessions/<key>.json`, which
  `walk.cjs` loads. Steps call `as('<key>')` or `login('<key>')` with the key from the table, so the
  file names match.
- Re-running resets every account to its state: roles, plan, Stripe subscription (a new one each run),
  linked clients, password and session. Run it again before each walk, and after a step changed an
  account you need in its original state.
- Sessions rotate on use: `walk.cjs` saves the new token back. If a run crashed or two pages used the
  same old session, the backend revokes that account's tokens and the page lands on `/login`. Re-run
  the seed rather than signing in through the form.
- A step needs a brand-new coach (registration itself is under test) → register one through the app
  (`walk.<epic>.<n>@formup.test`, a generated password) with `s-register.cjs`. It appends that coach to
  the accounts file; the next seed run rewrites the file and drops it.

## 3. Driving a step

**Use the scripts in `.claude/skills/test-guide/walk/`** (headless Brave through the repo's own Playwright,
one script per step; the Playwright MCP plugin loses its Brave setting on every plugin update):

- `walk.cjs <step.cjs> [width] [light|dark]` is the driver. It keeps one saved session per account, and
  logs console errors and 4xx/5xx calls. Set `EPIC=<n>` for the screenshot folder.
- To walk a branch's own web server instead of the main checkout's, set these variables (each falls back
  to the main checkout's default): `WALK_BASE` (the server URL, default `http://localhost:5173`),
  `WALK_ACCOUNTS` (the accounts file), `WALK_STATE` (the saved-session folder), `WALK_SHOTS` (the
  screenshot folder) and `WALK_PLAYWRIGHT` (the branch's `web/node_modules/playwright`). Give each backend
  its own `WALK_STATE` folder: a saved sign-in is replaced each time it is used, so two backends sharing
  one folder overwrite each other's sessions. `s-buy.cjs` waits for Stripe to return to `WALK_BASE`, so a
  branch walk that covers checkout needs that branch's backend to send Stripe's return URL to the same
  address; otherwise the step ends with a `checkout-stuck` screenshot.
- `s-seeded.cjs` (KEYS, default all six) opens the app as each seeded account and logs who is signed in
  and how many login calls were made (expected 0). Run it first to check the seed's sessions.
- `s-register.cjs` (KEY, ROLE), `s-signin.cjs` (KEYS), `s-verify.cjs` (PAIRS) and `mail.cjs <email>` handle
  accounts. Mail goes to the local MailHog on :8025.
- `s-buy.cjs` (KEY, PLAN, CARD) does Stripe Checkout in sandbox only. It fills the card inside Stripe's
  `elements-inner-habanero` frame, closes the address suggestions before submitting, and presses Complete
  on the 3D Secure test page. It stops if an hCaptcha challenge becomes visible.
- `s-give.cjs` (COACH, PLAN, ACTION=change|end) is the admin Change plan / End plan now. It records the
  busy state and the toast.
- `s-plancard.cjs` and `s-features.cjs` (KEY) read a coach's plan card and `GET /users/me/subscription`.
- Known gap: the 3D Secure step on Stripe's hosted **invoice** page (second-role payment) did not complete
  headless in #592. Flag it with `yourCheck`.

The Playwright MCP tools work too once their Brave arguments are restored (memory
`feedback_use_brave_for_browser_automation`).

For each `ready` section, in `order`, and each step:

1. Do exactly what `text` says. Prefer `browser_snapshot` (the page's accessibility tree) for clicks and
   reads, and use a screenshot only to look at layout.
2. Compare with `expect`. Read real UI text (Czech by default), not your memory of the code.
3. **Two-window steps** (a live update reaching another open page) → open a second tab with
   `browser_tabs`, signed in as the other user, and check it updates without a reload.
4. **Stripe:**
   - Pay on Stripe's test-mode Checkout page with the guide's published test cards only.
   - On the 3D Secure test page, press the "complete" button.
   - Check the Stripe side with read-only CLI calls (`stripe subscriptions retrieve`, `stripe refunds list
     --limit 5`, `stripe invoices list --customer …`), not the Dashboard.
   - A captcha or any real-money prompt → stop that step and set `yourCheck`. Never solve or bypass a
     captcha.
5. **Waits longer than ~2 minutes** (the 23-hour Stripe expiry, a scheduled job) → don't fake time. Set
   `yourCheck` with what to look for.
6. Console errors or failed network calls during a step count as a finding, even if `expect` holds.

## 4. The UI pass

Every step whose screen changed in the epic also gets this pass:

- Widths: 375, 768, 1024 and 1440 px (`browser_resize`). Themes: light and dark
  (`browser_emulate_media colorScheme`).
- Compare with the redesign board the issue names (`docs/prototypes/formup-redesign/`), light and dark.
  A screen with no board is checked against itself (`.claude/CLAUDE.md` → "Not yet redesigned").
- Look for the defects the maintainer found by hand on #592:
  - Text cut off or ellipsed where the full value matters (a plan name next to a badge).
  - Parts overlapping or wrapping badly at narrow widths. Buttons not where the board puts them.
  - An empty or blank area while something loads: there should be a spinner or skeleton.
  - A slow action with no busy state on the button that started it.
  - Two controls that do the same thing, or a dead/disabled control that no longer has a purpose.
  - Copy that describes behaviour the app doesn't have. Check the claim against the code path.
  - A success with no visible feedback, or feedback missing the fact the user needs (an amount, a date).
  - Layout jumps when content arrives.

## 5. Writing results into the guide

- One `ArtifactData` `batch` per section. Pin each step with `if_version`.
- Step fields, added to the existing doc:
  `status` (`pass`/`fail`), `by: "claude"`, `at` (ISO time), `finding` (one plain sentence: what you saw,
  only when it adds something to "as expected"), `evidence` (`[{id, url, label}]`), `yourCheck` (only
  when the maintainer must judge it, saying why).
- Evidence: save screenshots under `.qa-artifacts/<epic>/walk/<step-id>-<width>-<theme>.png` (gitignored),
  upload the ones that prove the result with `Artifact` `publish`, `url` = the guide, `asset: true`,
  `file_paths`, and store the returned `id` and `url`. One or two per step. Crop out anything personal.
- Don't overwrite a maintainer's mark: skip steps with `by: "maintainer"` unless their section changed
  since.
- Taste calls you are unsure of ("is this spacing right?") → `pass` with `yourCheck`, not `fail`.

## 6. Failures

- A `fail` is a defect: root-cause it (Working Principles §1), then fix it as a task-style branch off the
  epic branch (`fix/<epic>-walk-<k>`) through the owning dev subagent. Merge it on green CI, as in
  `ship-epic` Phase 1d.
- Also write a `notes` doc on the section (`state: "seen"`, `reply` naming the fix branch or issue), so
  the maintainer sees what was found.
- Something that needs a product call (not a defect) → a `decisions` doc, never a guess.
- After the fixes merge, restart the backend and re-walk only the affected sections.
- Stop and hand over when the same step fails after two fix rounds.

## 7. Handing over

Tell the maintainer in chat, in at most three lines:
- how many steps Claude passed,
- how many need their eye (the guide's **Needs you** filter),
- how many decisions wait.

Plus the guide link. They test only the **Needs you** list. Then ship-epic continues to `pr-reviewer`.
