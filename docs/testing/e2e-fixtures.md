# E2E fixture credentials

The compose harness (`docker-compose.test.yml`) seeds a deterministic set of QA users via `backend/FormUp.Application/Seed/QaSeedRunner.cs` before the API service starts.

The harness API listens on `https://localhost:<ephemeral-port>` with a self-signed dev cert — pass `-k` to curl, or `rejectUnauthorized: false` to Node `https`. The interactive dev API at `:5001` is a **separate** stack with a separate database; never point Playwright at it.

## scripts/test-env — branch-scoped CLI

`scripts/test-env` is the recommended entry point for the harness. It derives a per-branch `COMPOSE_PROJECT_NAME` so parallel CI runs (or two local checkouts) coexist on ephemeral host ports without colliding. Every call emits a JSON envelope to stdout so the orchestrator, `qa-tester`, and CI workflow can read the resolved API URL programmatically.

| Subcommand                          | What it does                                                                                |
| ----------------------------------- | ------------------------------------------------------------------------------------------- |
| `scripts/test-env up [<branch>]`    | Bring up an isolated stack; prints `{"project","branch","api_url"}`.                        |
| `scripts/test-env down [<branch>]`  | Tear down + remove all volumes/networks for that branch's stack.                            |
| `scripts/test-env seed [--kind=K]`  | Re-run the seed container against the current branch's stack. `K`=`minimal`\|`rich`.        |
| `scripts/test-env run <flow>`       | Execute a named Playwright flow inside the harness (Phase 2+ — playwright container).      |
| `scripts/test-env logs <service>`   | Tail a service's logs (api/postgres-test/mongo-test/minio-test/seed/...).                   |
| `scripts/test-env health`           | curl the resolved api host port; print HTTP status.                                          |
| `scripts/test-env ports`            | Print the active stack's project name + resolved api host port.                              |

The CLI persists state to `.test-env.<project>.env` at the repo root (gitignored, removed on `down`). The host port for `api` is **ephemeral** — read it from the JSON envelope or via `npm run e2e:ports`. Do NOT assume `:5101`; that was the previous fixed mapping.

`npm run e2e:up` / `e2e:down` / `e2e:logs` / `e2e:health` are thin wrappers that call into the CLI — both interfaces work, but new automation should call `scripts/test-env` directly so the JSON envelope is parseable.

### Project-name derivation

Project names follow `fp-<sanitized-branch>-<sha1[:6]>`, where the sha1 is computed from `<branch>|<hostname>`. Same branch on the same host → same project name (deterministic, so re-running `up` on a half-up stack fails fast with a clear message instead of silently piling on). Different branches → different project names → coexist on different ephemeral ports.

### Two parallel runs (smoke)

```bash
# In one shell
scripts/test-env up feature/aaa
# {"project":"fp-feature-aaa-1f4d2b","branch":"feature/aaa","api_url":"https://localhost:53241"}

# In another shell
scripts/test-env up feature/bbb
# {"project":"fp-feature-bbb-9c7e0a","branch":"feature/bbb","api_url":"https://localhost:53248"}

docker ps --format '{{.Names}}'
# fp-feature-aaa-1f4d2b-api-1
# fp-feature-aaa-1f4d2b-postgres-test-1
# fp-feature-bbb-9c7e0a-api-1
# fp-feature-bbb-9c7e0a-postgres-test-1
# (etc.)
```

## Seeded users

| Role         | Email                            | Stable user id                            |
| ------------ | -------------------------------- | ----------------------------------------- |
| Client       | `qa.client@formup.test` | `11111111-1111-1111-1111-111111111111`    |
| Trainer      | `qa.trainer@formup.test`| `22222222-2222-2222-2222-222222222222`    |
| Nutritionist | `qa.nutri@formup.test`  | `33333333-3333-3333-3333-333333333333`    |
| Client 2     | `qa.client2@formup.test`| `55555555-5555-5555-5555-555555555555`    |
| Trainer 2    | `qa.trainer2@formup.test`| `66666666-6666-6666-6666-666666666666`   |
| Client 3     | `qa.client3@formup.test`| `77777777-7777-7777-7777-777777777777`    |
| Former client | `qa.client.former@formup.test` | `88888888-8888-8888-8888-888888888888` |
| Ending client "QA Ending" | `qa.client.ending@formup.test` | `10101010-1010-1010-1010-101010101010` |
| Coach (trainer + nutritionist) | `qa.coach@formup.test` | `99999999-9999-9999-9999-999999999999` |
| Client "QA Duo" | `qa.client.both@formup.test` | `44444444-4444-4444-4444-444444444444` |
| Lapsed coach (trainer) | `qa.coach.lapsed@formup.test` | prefix `20202020` — see [lapsed and gift coaches](#seeded-lapsed-and-gift-ended-coaches-2051) |
| Gift-ended coach (trainer) | `qa.coach.gift@formup.test` | prefix `30303030` — see [lapsed and gift coaches](#seeded-lapsed-and-gift-ended-coaches-2051) |

The second pair (`Client 2` / `Trainer 2`) is dedicated to the multi-section shared-exercise fixture (#474). Client 3 is linked to the QA trainer only — see [the inbox fixture](#seeded-inbox-conversation--weekly-check-ins-1095) — and has no conversation and no active plan. The former client has an **ended** link to the QA trainer and one conversation — see [the ended-link fixture](#ended-link-conversation-1101). They share the same password as the other accounts (`QA_SEED_PASSWORD` from `.env.test`).

The ending client ("QA Ending", client profile public id `10101010-1010-1010-aaaa-000000000001`) is for the #1752 ended-link e2e. It has a **live** link to the QA trainer, no conversation, no plan, no payment terms, and the QA trainer has no package. The spec logs in as this client and ends the link with `DELETE /client/collaborations/{publicId}`, so after that spec runs its link is ended until the next `/test/reset`.

All three accounts share the password held in `QA_SEED_PASSWORD` in your local `.env.test` (gitignored). Copy `.env.test.example` to `.env.test` and fill `JWT_SECRET` (≥32 chars) and `QA_SEED_PASSWORD` before the first `npm run e2e:up`. The seed runner refuses to start if `QA_SEED_PASSWORD` is unset, so a missing env file fails fast instead of creating users with a default password.

The GUIDs above are referenced by spec assertions and trainer↔client link fixtures — changing them is a fixture-version bump (update specs and any DB seeders that hard-code them in the same PR).

## Reset between specs

`POST https://localhost:<ephemeral-port>/test/reset` (no auth) drops and recreates the Postgres schema and Mongo collections, then re-runs `QaSeedRunner`. Playwright's `globalSetup` (`web/tests/e2e/global-setup.ts`, `mobile/tests/e2e/global-setup.ts`) calls it once per `playwright test` run so specs start from a known baseline. Resolve the host port with `scripts/test-env ports` or read it from the `up` JSON envelope; the previous fixed `:5101` mapping is gone.

The endpoint is **double-gated**: it only responds when `Testing__Enabled=true` AND `ASPNETCORE_ENVIRONMENT=Development`. The compose stack hard-codes both — the dev API at `:5001` does not, so calling `/test/reset` against the dev API correctly returns 404. It is also hidden from `/swagger/v1/swagger.json` via `ExcludeFromDescription()`, so a prod scanner cannot enumerate it via the OpenAPI document.

## Seeded training plans

A deterministic training plan is seeded for the QA client on every `/test/reset` call.

### Stable GUIDs

| Constant                          | Value                                  | What it maps to                                    |
| --------------------------------- | -------------------------------------- | -------------------------------------------------- |
| `QaTrainingPlanExternalId`        | `dddddddd-dddd-dddd-dddd-dddddddddddd` | The plan's `ExternalId` (used in API responses)    |
| `ClientUserId`                    | `11111111-1111-1111-1111-111111111111` | `TrainingPlan.ClientId` (ApplicationUser.Id — see note) |
| `ClientProfilePublicId`           | `aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa` | `ClientProfile.PublicId` — trainer-facing routes only, NOT any Mongo `ClientId` |
| `TrainerUserId`                   | `22222222-2222-2222-2222-222222222222` | `TrainingPlan.TrainerId` (ApplicationUser.Id — see note) |
| `ForTimeSectionId`                | `eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee` | `TrainingSection.SectionId` for Section 1          |
| `AmrapSectionId`                  | `ffffffff-ffff-ffff-ffff-ffffffffffff` | `TrainingSection.SectionId` for Section 2          |
| `StandardSectionId`               | `00000000-0000-0000-aaaa-000000000001` | `TrainingSection.SectionId` for Section 3          |
| `QaSessionId`                     | `00000000-0000-0000-bbbb-000000000001` | `TrainingSession.SessionId`                        |

> **Note on ClientId.** `TrainingPlan.ClientId` is keyed on `ApplicationUser.Id`
> (`11111111-...`), **not** on `ClientProfile.PublicId` (`aaaaaaaa-...`), since the #840
> migration. `GetClientPlansEndpoint.cs:59` resolves `var clientId = clientProfile.UserId`
> and filters on that, so seeding the profile `PublicId` would make the plan invisible.
> Every Mongo document keys the same way — `SessionExecution`, `SessionLog`,
> `PersonalRecord`, `MealLog`.
>
> The profile `PublicId` is still the right value in **API query parameters** on
> trainer-facing routes: `GET /training/plans?clientId=<ClientProfilePublicId>` resolves
> `cp.PublicId == req.ClientId` and only then filters `TrainingPlan.ClientId` by that
> profile's `UserId` (`GetTrainingPlansEndpoint.cs:85-87`). Query parameter = `PublicId`,
> stored Mongo field = `ApplicationUser.Id`; conflating the two is what this note
> originally got wrong.
>
> This note asserted the exact opposite before #847. The old wording was correct only
> before #840, and a join by `PublicId` now matches zero documents silently rather than
> failing, so treat any surviving copy of the old claim as stale.
>
> **Note on TrainerId.** `TrainingPlan.TrainerId` is keyed on `ApplicationUser.Id`
> (`22222222-...`), **not** on `ProfessionalProfile.PublicId` (`bbbbbbbb-...`).
> `GET /training/plans` and `GET /training/plans/{planId}` scope by
> `Guid.Parse(AppClaims.UserId)` which is `ApplicationUser.Id`. Using the profile
> `PublicId` would make the plan invisible to those trainer endpoints.

### Seeded plan shape

```
TrainingPlan (ExternalId = dddddddd-...)
  Status: Active
  Weeks:
    Week 1 (Status = Published, DatePublished set)
      Session "QA Session" (DayOfWeek = 1 = Monday)
        Section 1 "ForTime 30min"
          Format:        ForTime
          TimeCapSeconds: 1800  (30 minutes)
          Exercises:     []     ← intentionally empty (#258 non-regression)
        Section 2 "AMRAP test"
          Format:        AMRAP
          TimeCapSeconds: 600   (10 minutes)
          Exercises:     [QA Pull-up, QA Box Jump]
        Section 3 "Standard test"
          Format:        null   (Standard)
          Exercises:     [QA Squat, QA Deadlift]
```

### Curl recipe — fetch as the QA client

```bash
# 0. Resolve the api URL for the current branch's stack
API_URL=$(scripts/test-env ports | jq -r '.api_url')

# 1. Log in as the QA client and capture the access token
ACCESS=$(curl -sk -X POST "$API_URL/auth/login" \
  -H "Content-Type: application/json" \
  -d '{"email":"qa.client@formup.test","password":"<QA_SEED_PASSWORD>"}' \
  | jq -r '.accessToken')

# 2. Fetch the client's training plans (should include the seeded plan)
curl -sk -H "Authorization: Bearer $ACCESS" \
  "$API_URL/client/plans" | jq '.items[] | select(.type=="training")'
```

Replace `<QA_SEED_PASSWORD>` with the value from your `.env.test` file. The plan
appears in the response because the week `Status = Published` — a Draft week would
be silently excluded by the `ElemMatch` filter in `GetClientPlansEndpoint`.

## Seeded past-dated training plan (#326)

A second training plan is seeded for the QA client alongside the ForTime fixture. Its `StartDate` is anchored to the Monday that is exactly 28 days before the seed instant, ensuring all Week 1 + Week 2 sessions resolve to past calendar dates regardless of when the harness boots.

### Purpose

The plan exercises the three past-session states the web portal classifies in `completionState.ts`:

| State | Definition | Fixture session |
|-------|------------|-----------------|
| **completed** (read-only) | `SessionExecution` exists with `Status = Completed` | `QaPastSessionCompletedId` (Week 1, Mon) |
| **skipped** (editable + Mark-finished) | `SessionExecution` exists with `Status = Partial` | `QaPastSessionSkippedId` (Week 1, Wed) |
| **untouched** (editable + Mark-finished) | No `SessionExecution` at all | `QaPastSessionUntouchedId` (Week 2, Mon) |

### Stable GUIDs

| Constant | Value | What it maps to |
|---|---|---|
| `QaPastTrainingPlanExternalId` | `11111111-1111-1111-2222-000000000001` | Plan `ExternalId` |
| `QaPastSessionCompletedId` | `11111111-1111-1111-2222-000000000002` | Session in Week 1, DayOfWeek=1 (Monday) |
| `QaPastSessionSkippedId` | `11111111-1111-1111-2222-000000000003` | Session in Week 1, DayOfWeek=3 (Wednesday) |
| `QaPastSessionUntouchedId` | `11111111-1111-1111-2222-000000000004` | Session in Week 2, DayOfWeek=1 (Monday) |
| `QaPastCompletedExecutionId` | `11111111-1111-1111-2222-000000000005` | SessionExecution for the completed session |
| `QaPastSkippedExecutionId` | `11111111-1111-1111-2222-000000000006` | SessionExecution for the skipped session |

### Plan ownership

- `TrainingPlan.ClientId` = `ClientUserId` (`11111111-...`) — keyed on **`ApplicationUser.Id`** since #840, NOT `ClientProfile.PublicId`. This document previously stated the opposite; that was correct only before #840 and joining by `PublicId` now matches zero documents silently. `SessionExecution.ClientId` uses the same identifier, so plan and execution join directly.
- `TrainingPlan.TrainerId` = `TrainerUserId` (`22222222-...`) — keyed on **`ApplicationUser.Id`**, NOT `ProfessionalProfile.PublicId`. `GetTrainingPlansEndpoint` and `GetTrainingPlanEndpoint` scope by `Guid.Parse(AppClaims.UserId)` which is `ApplicationUser.Id`. Using the profile `PublicId` (`bbbbbbbb-...`) would make the plan invisible to `GET /training/plans`.
- `SessionExecution.ClientId` = `ClientUserId` (`11111111-...`) — also **`ApplicationUser.Id`**. `CompleteWorkoutEndpoint` scopes executions by `ClientId == Guid.Parse(AppClaims.UserId)`, which is `ApplicationUser.Id`.
- Login as `qa.trainer@formup.test` to access via `GET /training/plans/{planId}`.

### Plan shape

```
TrainingPlan (ExternalId = 11111111-1111-1111-2222-000000000001)
  Status: Active
  StartDate: <Monday ~28 days before seed time>
  Weeks:
    Week 1 (Status = Published)
      Session "QA Past Session — Completed" (DayOfWeek = 1 = Monday)
        Section "Hlavní" (Standard, PastCompletedSectionId = 11111111-1111-1111-3333-000000000001)
          [QA Bench Press, QA Overhead Press]
        → SessionExecution (ExternalId = 11111111-1111-1111-2222-000000000005, Status=Completed)
      Session "QA Past Session — Skipped" (DayOfWeek = 3 = Wednesday)
        Section "Hlavní" (Standard, PastSkippedSectionId = 11111111-1111-1111-3333-000000000002)
          [QA Back Squat, QA Romanian Deadlift]
        → SessionExecution (ExternalId = 11111111-1111-1111-2222-000000000006, Status=Partial, 1 partial set)
    Week 2 (Status = Published)
      Session "QA Past Session — Untouched" (DayOfWeek = 1 = Monday)
        Section "Hlavní" (Standard, PastUntouchedSectionId = 11111111-1111-1111-3333-000000000003)
          [QA Pull-down, QA Seated Row]
        → NO SessionExecution
```

### Playwright targeting

When driving the trainer portal to the plan detail page, navigate to
`/training/plans/11111111-1111-1111-2222-000000000001` (use the plan `ExternalId` from the API response, or derive it via `GET /training/plans?clientId=<ClientProfilePublicId>`).

Use the session IDs above as stable selectors in spec assertions (`data-session-id` attributes or API probe keys).

---

## Seeded foods, recipes, nutrition plan, blobs

Five foods, three recipes, and one nutrition plan are seeded by `QaSeedRunner` alongside the training plan. All constants are in `QaSeedRunner.cs`.

### Foods

| Constant              | External ID                              | Name                    | Category          |
| --------------------- | ---------------------------------------- | ----------------------- | ----------------- |
| `QaFood1ExternalId`   | `00000000-0000-0000-eeee-000000000001`   | Chicken Breast          | Meat              |
| `QaFood2ExternalId`   | `00000000-0000-0000-eeee-000000000002`   | White Rice (cooked)     | GrainsAndCereals  |
| `QaFood3ExternalId`   | `00000000-0000-0000-eeee-000000000003`   | Broccoli                | Vegetables        |
| `QaFood4ExternalId`   | `00000000-0000-0000-eeee-000000000004`   | Banana (medium)         | Fruit             |
| `QaFood5ExternalId`   | `00000000-0000-0000-eeee-000000000005`   | Rolled Oats             | GrainsAndCereals  |

All five foods are owned by the QA Nutri (`NutritionistId = NutriUserId = 33333333-...`), visibility `Public`.

### Recipes

| Constant               | External ID                              | Name                           | Foods included            |
| ---------------------- | ---------------------------------------- | ------------------------------ | ------------------------- |
| `QaRecipe1ExternalId`  | `00000000-0000-0000-ffff-000000000001`   | Chicken, Rice & Broccoli Bowl  | Food 1 + Food 2 + Food 3  |
| `QaRecipe2ExternalId`  | `00000000-0000-0000-ffff-000000000002`   | Oats & Banana Breakfast        | Food 5 + Food 4           |
| `QaRecipe3ExternalId`  | `00000000-0000-0000-ffff-000000000003`   | Chicken & Broccoli Stir-fry    | Food 1 + Food 3           |

All three recipes are owned by the QA Nutri, visibility `Public`.

### Nutrition plan

| Constant                     | Value                                  | What it maps to                                    |
| ---------------------------- | -------------------------------------- | -------------------------------------------------- |
| `QaNutritionPlanExternalId`  | `dddddddd-eeee-ffff-0000-111111111111` | The plan's `ExternalId`                            |
| `ClientUserId`               | `11111111-1111-1111-1111-111111111111` | `NutritionPlan.ClientId` (ApplicationUser.Id, #840) |
| `NutriUserId`                | `33333333-3333-3333-3333-333333333333` | `NutritionPlan.NutritionistId` (nutritionist user id) |

Plan shape:

```
NutritionPlan (ExternalId = dddddddd-eeee-ffff-0000-111111111111)
  Status: Active
  StartDate: <Monday of the previous (seed-time) week>   (#1303; was this Monday)
  GlobalSettings:
    DailyKcal:    2500
    ProteinGrams: 160   (640 kcal)
    CarbsGrams:   240   (960 kcal)
    FatGrams:     100   (900 kcal)
    — chosen so 4*protein + 4*carbs + 9*fat sums to DailyKcal EXACTLY
      (640 + 960 + 900 = 2500), which is what keeps the client-detail
      meal-plan card's three rounded percentages (26/38/36) summing to
      100 rather than drifting to 99 or 101 (#1094).
  Weeks (#1303 — rich seed):
    Week 1 and Week 2 (both Status = Published, DatePublished set)
      Days 1–7, each with
        Meal 1 — Breakfast  (08:00, no foods pre-loaded)
        Meal 2 — Lunch      (12:00, no foods pre-loaded)
        Meal 3 — Dinner     (18:00, no foods pre-loaded)
      Every meal has its own deterministic id.
```

The plan still ends on this Monday + 7, so plan status and the "Ending soon"
chip are unchanged. See [the client detail Overview fixture](#seeded-client-detail-overview-1303)
for the meal logs written against it.

> **Note on StartDate (#1094).** Anchored to the Monday of the current
> (seed-time) week — same anchoring idiom as the ForTime training plan's
> #898 fix — so `PlanWindowResolver`'s `[StartDate, StartDate + weeks*7)`
> window (here 1 week, i.e. 7 days) covers today. This is the SAME plan
> the client has always had (`QaNutritionPlanExternalId`), not a second
> Active nutrition plan: giving it an explicit window keeps every
> `ResolveCurrentPlan` call site (`GetTodayPlan`, `GetWeekPlan`,
> `LogMealEaten`, `GetShoppingList`, the client-detail dashboard, …)
> resolving to this exact plan via the window branch instead of via the
> resolver's legacy single-candidate fallback — the plan they resolve to
> does not change, only how it's matched. Since #1303 the window is 14 days
> (last Monday to this Monday + 7). A harness left running past the window
> without a `/test/reset` will see it go stale until the next reset
> re-anchors it, same caveat as the training plan.

### Blob assets

| Constant              | MinIO key                            | Content          |
| --------------------- | ------------------------------------ | ---------------- |
| `QaAvatarBlobKey`     | `avatars/qa-client-11111111.png`     | 1×1 pixel PNG    |
| `QaFoodImageBlobKey`  | `foods/qa-food-1.png`                | 1×1 pixel PNG    |

Both blobs are loaded from embedded resources (`Seed/Assets/qa-avatar.png` and `qa-food.png`) and uploaded to MinIO via `IBlobStorageService.UploadAsync` during seed. Upload is idempotent: `ObjectExistsAsync` is checked first; existing blobs are left in place.

## Seeded multi-section fixture — shared exercise across Standard + AMRAP (#474)

A third training plan is seeded for the **second** QA client/trainer pair. Its purpose is to let the web coach-detail screen demonstrate section-keyed planned-vs-actual values for a session where the same exercise appears in two different section types.

### Ownership

- Client: `qa.client2@formup.test` (`Client2UserId = 55555555-5555-5555-5555-555555555555`)
- Trainer: `qa.trainer2@formup.test` (`Trainer2UserId = 66666666-6666-6666-6666-666666666666`)
- `TrainingPlan.TrainerId` = `Trainer2UserId` (ApplicationUser.Id — same rule as all other plans)
- `TrainingPlan.ClientId` = `Client2UserId = 55555555-5555-5555-5555-555555555555` (ApplicationUser.Id — same rule as all other plans, per #840)

### Stable GUIDs

| Constant | Value | What it maps to |
|---|---|---|
| `QaMultiSectionPlanExternalId` | `55555555-5555-5555-dddd-000000000001` | Plan `ExternalId` |
| `QaMultiSectionSessionId` | `55555555-5555-5555-bbbb-000000000001` | Session in Week 1, DayOfWeek=2 (Tuesday) |
| `MultiSectionStandardSectionId` | `55555555-5555-5555-aaaa-000000000001` | Standard section SectionId |
| `MultiSectionAmrapSectionId` | `55555555-5555-5555-aaaa-000000000002` | AMRAP section SectionId |
| `SharedExerciseId` | `55555555-5555-5555-cccc-000000000001` | "QA Kettlebell Swing" (appears in BOTH sections) |
| `QaMultiSectionExecutionId` | `55555555-5555-5555-4455-000000000001` | Completed SessionExecution for this session |

### Plan shape

```
TrainingPlan (ExternalId = 55555555-5555-5555-dddd-000000000001)
  Status: Active
  Weeks:
    Week 1 (Status = Published)
      Session "QA Multi-Section Session" (DayOfWeek = 2 = Tuesday)
        Section 1 "Standard work" (Standard, SectionId = 55555555-5555-5555-aaaa-000000000001)
          [QA Kettlebell Swing — 3 prescribed sets × 15 reps @ 24 kg]
        Section 2 "AMRAP 10 min" (AMRAP 600s, SectionId = 55555555-5555-5555-aaaa-000000000002)
          [QA Kettlebell Swing — no prescribed sets (AMRAP accumulates rounds)]
```

### SessionExecution shape

```
SessionExecution (ExternalId = 55555555-5555-5555-4455-000000000001)
  Status: Completed
  ClientId: 55555555-5555-5555-5555-555555555555  (Client2UserId — ApplicationUser.Id)

  Section "Standard work" (SectionId = 55555555-5555-5555-aaaa-000000000001)
    QA Kettlebell Swing:
      Set 1 — MODIFIED:      actual Reps=12 WeightKg=28 | planned Reps=15 WeightKg=24 → IsModified=true  → shows "upraveno"
      Set 2 — AS-PRESCRIBED: actual Reps=15 WeightKg=24 | planned Reps=15 WeightKg=24 → IsModified=false → no badge
      Set 3 — MODIFIED:      actual Reps=10 WeightKg=28 | planned Reps=15 WeightKg=24 → IsModified=true  → shows "upraveno"

  Section "AMRAP 10 min" (SectionId = 55555555-5555-5555-aaaa-000000000002)
    QA Kettlebell Swing:
      Set 1 — no planned snapshot: actual Reps=15 WeightKg=24 | planned=null → IsModified=false → no "upraveno"
```

### What to verify on the coach-detail screen

Log in as `qa.trainer2@formup.test` and navigate to the client's workout log for this session. On the coach-detail planned-vs-actual view:

- **Standard section**: Set 1 and Set 3 for "QA Kettlebell Swing" must show the "upraveno" indicator and display the actual values (`12 reps / 28 kg` and `10 reps / 28 kg`). Set 2 shows `15 reps / 24 kg` without an "upraveno" indicator.
- **AMRAP section**: "QA Kettlebell Swing" shows `15 reps / 24 kg` with no "upraveno" indicator, even though the same exercise ID was edited in the Standard section — because section keying means each section is independent.

### Curl recipe — fetch as Trainer 2

```bash
# Resolve the api URL for the current branch's stack
API_URL=$(scripts/test-env ports | jq -r '.api_url')

# Log in as qa.trainer2 and capture the access token
ACCESS=$(curl -sk -X POST "$API_URL/auth/login" \
  -H "Content-Type: application/json" \
  -d '{"email":"qa.trainer2@formup.test","password":"<QA_SEED_PASSWORD>"}' \
  | jq -r '.accessToken')

# Fetch the multi-section plan
curl -sk -H "Authorization: Bearer $ACCESS" \
  "$API_URL/training/plans/55555555-5555-5555-dddd-000000000001" | jq '.'
```

---

## Seeded questionnaire template + submitted response (#715)

A questionnaire template owned by the QA trainer, a SUBMITTED response for the
QA client, and a link from the main training plan (`QaTrainingPlanExternalId`)
to that response via `QuestionnaireResponseId`. This unblocks interactive QA
of the "Dotaznik" answers tab on the training plan detail page (#697) —
without it, the tab can only ever render its empty state against seed data.

> **#720 update.** The seeded nutrition plan (`QaNutritionPlanExternalId`) used
> to link to this SAME trainer-owned response (#715's original shape). #720
> replaced that with a separate nutritionist-owned template + response (see
> [below](#seeded-nutritionist-owned-questionnaire-template--submitted-response-720))
> so the nutrition plan links a response the QA nutritionist actually owns —
> see the cross-professional-visibility note below for why that matters.

### Stable GUIDs

| Constant | Value | What it maps to |
|---|---|---|
| `QaQuestionnaireExternalId` | `00000000-0000-0000-7777-000000000000` | `Questionnaire.PublicId` |
| `QaQuestionnaireResponseExternalId` | `00000000-0000-0000-7777-000000000099` | `QuestionnaireResponse.PublicId` |
| `QaQuestionSectionBasicInfoId` | `00000000-0000-0000-7777-000000000001` | Section header, OrderIndex 0 |
| `QaQuestionGoalId` | `00000000-0000-0000-7777-000000000002` | short_text question, OrderIndex 1 |
| `QaQuestionWeightId` | `00000000-0000-0000-7777-000000000003` | number question, OrderIndex 2 |
| `QaQuestionTrainingDaysId` | `00000000-0000-0000-7777-000000000004` | single_choice question, OrderIndex 3 |
| `QaQuestionSectionHealthId` | `00000000-0000-0000-7777-000000000005` | Section header, OrderIndex 4 |
| `QaQuestionEnergyId` | `00000000-0000-0000-7777-000000000006` | scale question, OrderIndex 5 |
| `QaQuestionInjuriesId` | `00000000-0000-0000-7777-000000000007` | multi_select question, OrderIndex 6 |
| `QaQuestionMedicalDocId` | `00000000-0000-0000-7777-000000000008` | file_upload question, OrderIndex 7 |

### Ownership

- Questionnaire template: `ProfessionalId = TrainerUserId` (`22222222-...`) — owned by the QA trainer.
- Response: `ClientId = ClientUserId` (`11111111-...`), `ProfessionalId = TrainerUserId`, `LinkId` = the existing QA trainer↔client `ClientProfessionalLink`.
- Response `Status = Submitted`, `SubmittedAt` = a fixed time-of-day 2 days before the seed run (`DateTime.UtcNow.Date.AddDays(-2).AddHours(9)`, computed once at first seed and never overwritten on re-seed).

> **Note on cross-professional visibility.** The response's `ProfessionalId` is
> the QA trainer. The trainer/nutritionist-scoped `GetClientResponsesEndpoint`
> (`GET /trainer/clients/{clientPublicId}/questionnaire-responses`, used by the
> web portal's `PlanQuestionnairePanel`) filters by
> `r.ProfessionalId == <calling professional>`, so today only the QA trainer's
> own view resolves this response through that specific endpoint — the QA
> nutritionist's view of the nutrition plan's questionnaire panel will show it
> as "linked" (via `NutritionPlan.QuestionnaireResponseId`) but the panel's
> `submitted.find(...)` lookup will come up empty until #697/#698's
> implementers reconcile the professional-scoping. This is a known
> limitation surfaced during #715, not something this seed fixes — the
> client-facing `GetClientResponseByIdEndpoint` (scoped by `ClientId` only)
> already supports the shared-response shape natively.

### Template shape

```
Questionnaire "QA Onboarding Questionnaire" (ExternalId = 00000000-0000-0000-7777-000000000000)
  Section "Basic Info" (OrderIndex 0)
    Q1 short_text     "What is your main fitness goal?"
    Q2 number         "What is your current body weight (kg)?"     Config: {"min":30,"max":250,"unit":"kg","step":1}
    Q3 single_choice  "How many days per week do you currently train?"  Config: {"options":["0","1-2","3-4","5+"]}
  Section "Health & Preferences" (OrderIndex 4)
    Q4 scale          "Rate your current energy level"             Config: {"min":1,"max":10,"labelMin":"Low","labelMax":"High"}
    Q5 multi_select   "Which areas have you previously injured?"   Config: {"options":["Knee","Back","Shoulder","None"]}
    Q6 file_upload    "Upload a recent medical clearance document (optional)"
```

### Submitted answers (as `formatAnswerValue` renders them)

| Question | Raw value stored | Rendered value |
|---|---|---|
| Q1 — goal (short_text) | `ValueText = "Build lean muscle and improve overall strength"` | `Build lean muscle and improve overall strength` |
| Q2 — weight (number) | `ValueNumber = 78` | `78` |
| Q3 — training days (single_choice) | `ValueText = "3-4"` | `3-4` |
| Q4 — energy (scale) | `ValueNumber = 7` | `7 / 10` |
| Q5 — injuries (multi_select) | `ValueJson = "[\"Knee\",\"Shoulder\"]"` | `Knee, Shoulder` |
| Q6 — medical doc (file_upload) | `FileUrl = "https://storage.qa.formup.test/qa-fixtures/medical-clearance-checkup.pdf"` | link text `medical-clearance-checkup.pdf` |

### Curl recipe — fetch as the QA trainer

```bash
API_URL=$(scripts/test-env ports | jq -r '.api_url')

ACCESS=$(curl -sk -X POST "$API_URL/auth/login" \
  -H "Content-Type: application/json" \
  -d '{"email":"qa.trainer@formup.test","password":"<QA_SEED_PASSWORD>"}' \
  | jq -r '.accessToken')

curl -sk -H "Authorization: Bearer $ACCESS" \
  "$API_URL/trainer/clients/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/questionnaire-responses" | jq '.'
```

---

## Seeded nutritionist-owned questionnaire template + submitted response (#720)

A second questionnaire template owned by the QA **nutritionist**, a SUBMITTED
response for the QA client with `ProfessionalId = NutriUserId`, and a link
from the seeded nutrition plan (`QaNutritionPlanExternalId`) to that response
via `QuestionnaireResponseId` — **replacing** the trainer-owned link #715
used to write there. This unblocks interactive QA of the nutritionist's own
"Dotaznik" answers tab on the nutrition plan detail page (#698), which
otherwise only ever renders its empty state: `GetClientResponsesEndpoint`
filters by `r.ProfessionalId == <calling professional>`, and the #715 fixture
only seeded a trainer-owned response.

A nutritionist↔client `ClientProfessionalLink` (`ProfessionalRole =
Nutritionist`, `IsActive = true`, `CanViewNutritionPlans = true`) is seeded
as part of this fixture — `GetClientResponsesEndpoint` requires an active
link between the calling professional and the client before it returns
anything, the same rule #715's trainer-owned fixture relies on via the
trainer↔client link created unconditionally at the top of `SeedAsync`.

### Stable GUIDs

| Constant | Value | What it maps to |
|---|---|---|
| `QaNutriQuestionnaireExternalId` | `00000000-0000-0000-8888-000000000000` | `Questionnaire.PublicId` |
| `QaNutriQuestionnaireResponseExternalId` | `00000000-0000-0000-8888-000000000099` | `QuestionnaireResponse.PublicId` |
| `QaNutriQuestionSectionIntakeId` | `00000000-0000-0000-8888-000000000001` | Section header, OrderIndex 0 |
| `QaNutriQuestionDietGoalId` | `00000000-0000-0000-8888-000000000002` | short_text question, OrderIndex 1 |
| `QaNutriQuestionCaloriesId` | `00000000-0000-0000-8888-000000000003` | number question, OrderIndex 2 |
| `QaNutriQuestionMealsPerDayId` | `00000000-0000-0000-8888-000000000004` | single_choice question, OrderIndex 3 |
| `QaNutriQuestionSectionLifestyleId` | `00000000-0000-0000-8888-000000000005` | Section header, OrderIndex 4 |
| `QaNutriQuestionAppetiteId` | `00000000-0000-0000-8888-000000000006` | scale question, OrderIndex 5 |
| `QaNutriQuestionAllergiesId` | `00000000-0000-0000-8888-000000000007` | multi_select question, OrderIndex 6 |
| `QaNutriQuestionFoodDiaryId` | `00000000-0000-0000-8888-000000000008` | file_upload question, OrderIndex 7 |

### Ownership

- Questionnaire template: `ProfessionalId = NutriUserId` (`33333333-...`) — owned by the QA nutritionist.
- Response: `ClientId = ClientUserId` (`11111111-...`), `ProfessionalId = NutriUserId`, `LinkId` = the nutritionist↔client `ClientProfessionalLink` seeded by this fixture.
- Response `Status = Submitted`, `SubmittedAt` = a fixed time-of-day 1 day before the seed run (`DateTime.UtcNow.Date.AddDays(-1).AddHours(10)`, computed once at first seed and never overwritten on re-seed — distinct from #715's `-2` days so the two responses' timestamps never collide).
- The nutrition plan link is written **unconditionally** whenever the plan's current `QuestionnaireResponseId` doesn't already equal this response's `PublicId` — unlike #715's "only set if null" guard, this corrects a pre-#720 database that still points at the old trainer-owned response on the very next seed run, instead of leaving it stale forever.

### Template shape

```
Questionnaire "QA Nutrition Intake Questionnaire" (ExternalId = 00000000-0000-0000-8888-000000000000)
  Section "Nutrition Info" (OrderIndex 0)
    Q1 short_text     "What is your primary dietary goal?"
    Q2 number         "How many calories do you currently consume per day?"  Config: {"min":1000,"max":6000,"unit":"kcal","step":50}
    Q3 single_choice  "How many meals do you eat per day?"                  Config: {"options":["1-2","3-4","5+"]}
  Section "Lifestyle & Restrictions" (OrderIndex 4)
    Q4 scale          "Rate your current appetite level"                    Config: {"min":1,"max":10,"labelMin":"Low","labelMax":"High"}
    Q5 multi_select   "Which foods do you need to avoid?"                   Config: {"options":["Gluten","Dairy","Nuts","None"]}
    Q6 file_upload    "Upload a recent food diary (optional)"
```

### Submitted answers (as `formatAnswerValue` renders them)

| Question | Raw value stored | Rendered value |
|---|---|---|
| Q1 — diet goal (short_text) | `ValueText = "Lose body fat while preserving muscle mass"` | `Lose body fat while preserving muscle mass` |
| Q2 — calories (number) | `ValueNumber = 2200` | `2200` |
| Q3 — meals per day (single_choice) | `ValueText = "3-4"` | `3-4` |
| Q4 — appetite (scale) | `ValueNumber = 6` | `6 / 10` |
| Q5 — allergies (multi_select) | `ValueJson = "[\"Gluten\",\"Dairy\"]"` | `Gluten, Dairy` |
| Q6 — food diary (file_upload) | `FileUrl = "https://storage.qa.formup.test/qa-fixtures/food-diary-week1.pdf"` | link text `food-diary-week1.pdf` |

### Curl recipe — fetch as the QA nutritionist

```bash
API_URL=$(scripts/test-env ports | jq -r '.api_url')

ACCESS=$(curl -sk -X POST "$API_URL/auth/login" \
  -H "Content-Type: application/json" \
  -d '{"email":"qa.nutri@formup.test","password":"<QA_SEED_PASSWORD>"}' \
  | jq -r '.accessToken')

curl -sk -H "Authorization: Bearer $ACCESS" \
  "$API_URL/trainer/clients/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/questionnaire-responses" | jq '.'
```

This hits the same route as the trainer's curl recipe above
(`GET /trainer/clients/{clientPublicId}/questionnaire-responses`), but the
professional-scoping in `GetClientResponsesEndpoint` (`r.ProfessionalId ==
<calling professional>`) means the QA trainer's call returns the #715
trainer-owned response and the QA nutritionist's call returns this
#720 nutritionist-owned response — never both.

---

## Seeded inbox conversation + weekly check-ins (#1095)

A conversation between the QA trainer and the QA client, a third client
linked only to the QA trainer, and two `WeeklyCheckIn` rows — seeded so the
inbox's filter chips (`UnreadMessages`, `NoMessages`, `NewCheckIns`,
`MissingCheckIns`) each have at least one matching row, and the trainer's
clients-list `'1 active plan'` locator stays unique to the QA client's
seeded training plan.

### Stable GUIDs

| Constant | Value | What it maps to |
|---|---|---|
| `Client3UserId` | `77777777-7777-7777-7777-777777777777` | Third client's `ApplicationUser.Id` |
| `Client3ProfilePublicId` | `77777777-7777-7777-aaaa-000000000001` | Third client's `ClientProfile.PublicId` |
| `QaInboxConversationId` | `00000000-0000-0000-aabb-000000000001` | `Conversation.PublicId` (qa.trainer ↔ qa.client) |
| `QaInboxMessage1Id` | `00000000-0000-0000-aabb-000000000002` | Client's opening message (read) |
| `QaInboxMessage2Id` | `00000000-0000-0000-aabb-000000000003` | Coach's reply containing a YouTube URL (read) |
| `QaInboxMessage3Id` | `00000000-0000-0000-aabb-000000000004` | Client's second message (**unread**) |
| `QaWeeklyCheckInRespondedUnreviewedId` | `00000000-0000-0000-aabb-000000000005` | Responded, not yet reviewed — `NewCheckIns` chip |
| `QaWeeklyCheckInExpiredId` | `00000000-0000-0000-aabb-000000000006` | Expired unanswered — `MissingCheckIns` chip |

### Fixture shape

- **Third client** (`qa.client3@formup.test`) — linked to the QA
  trainer only (`ProfessionalRole = Trainer`, `IsActive = true`). No
  conversation, no active plan. Exists so the `NoMessages` filter chip has a
  guaranteed non-zero row, and so `clients.spec.ts`'s strict-mode `'1 active
  plan'` locator stays unique to the QA client's own plan.
- **Conversation** (qa.trainer ↔ qa.client) — three messages, oldest first:
  a client message (read), a coach reply containing
  `https://www.youtube.com/watch?v=dQw4w9WgXcQ` (read — exercises the
  inbox's YouTube-preview-card rendering), and a second client message left
  **unread** (exercises the `UnreadMessages` filter chip and the
  conversation-row unread dot). Dated via the two-pass
  insert-then-`ExecuteUpdateAsync` trick (`GetMessagesEndpointTests
  .SeedMessagesAsync`) so `DateCreated` values are deterministic rather than
  whatever `ApplyTimestamps` stamps on insert.
- **Weekly check-ins** — both scoped to `Profession.Training` (the only
  profession the qa.trainer↔qa.client link grants): one `Responded` with
  `ReviewedByTrainerAt = null` (`NewCheckIns` chip), one `Expired` with no
  response (`MissingCheckIns` chip). Distinct `WeekStartDate` values avoid
  the `(ClientUserId, ProfessionalUserId, Profession, WeekStartDate)` unique
  index.

All of the above is part of the **Rich** seed kind only — `QA_SEED_KIND=minimal`
skips the conversation, the check-ins, and the third client entirely.

### Ended-link conversation (#1101)

A former client whose link to the QA trainer has ended, with one
conversation. It exercises the inbox for a client the trainer no longer
coaches.

| Constant | Value | What it maps to |
|---|---|---|
| `ClientFormerUserId` | `88888888-8888-8888-8888-888888888888` | Former client's `ApplicationUser.Id` |
| `ClientFormerProfilePublicId` | `88888888-8888-8888-aaaa-000000000001` | Former client's `ClientProfile.PublicId` |
| `QaEndedLinkConversationId` | `00000000-0000-0000-aabb-000000000011` | `Conversation.PublicId` (qa.trainer ↔ former client) |
| `QaEndedLinkMessage1Id` | `00000000-0000-0000-aabb-000000000012` | Coach's goodbye message (read, 5 h old) |
| `QaEndedLinkMessage2Id` | `00000000-0000-0000-aabb-000000000013` | Client's follow-up (**unread**, 4 h old) |

- **Former client** (`qa.client.former@formup.test`, display name
  "QA Former") — the name deliberately does not contain "QA Client", so
  existing `QA Client` text locators stay unique. Same seed password.
- **Link** — to the QA trainer with `IsActive = false`; it grants training
  plans and hides nutrition plans. No plans or check-ins are seeded.
- **Inbox behaviour** — the thread lists only under the **All** chip, never
  under the live-link chips, and `IsSendLocked = false`: an ended link may
  still message.

Rich seed kind only, like the fixture above.

### Curl recipe — fetch the QA trainer's inbox

```bash
API_URL=$(scripts/test-env ports | jq -r '.api_url')

ACCESS=$(curl -sk -X POST "$API_URL/auth/login" \
  -H "Content-Type: application/json" \
  -d '{"email":"qa.trainer@formup.test","password":"<QA_SEED_PASSWORD>"}' \
  | jq -r '.accessToken')

# Conversation list, default filter=All
curl -sk -H "Authorization: Bearer $ACCESS" "$API_URL/conversations" | jq '.'

# Filter-chip counts for the dropdown
curl -sk -H "Authorization: Bearer $ACCESS" "$API_URL/conversations/filter-counts" | jq '.'
```

## Seeded client detail Overview (#1303)

Fills every card on the trainer portal's client detail Overview for the QA
client, so the boards PageClientDetailC/D can be checked against real data.
Rich seed only — the minimal kind is unchanged. Dates are relative to the
reset time.

| Fixture | Ids / shape | Expected values |
|---|---|---|
| Identity (QA client profile) | DateOfBirth = reset date − 32 years − 45 days, height 182 cm, weight 80.5 kg | Age 32 |
| Onboarding + nutrition targets | Male, LoseFat, sleep 6 h, stress 4, training Occasional → 4× a week, injuries `knees,back`, allergies `lactose,nuts`, blockers `time,motivation`, 4–5 meals a day, Standard diet, target weight 76 kg | BMR 1788, TDEE 2771, 2217 kcal, P 161 / C 238 / F 69 g |
| Body measurements | `00000000-0000-0000-aacc-000000000001` … `…08`, one a week, newest yesterday 07:00 UTC | Weight 84.0 → 80.5 kg, body fat 22.0 → 20.0 %, waist 94.0 → 89.5 cm |
| Meal logs | 14 logs against the [nutrition plan](#nutrition-plan) on days −1 … −6 (3, 3, 2, 2, 2, 2 per day), none today | As the QA nutritionist: timeline rows "N of 3", 7-day compliance 58.3 %, streak 6 |
| Personal record | `00000000-0000-0000-aadd-000000000001` | QA Squat 85 kg × 8, 3 days ago (QA trainer only) |
| Weekly check-ins (QA trainer, Training) | Anchor = next ISO Monday. Waiting `00000000-0000-0000-aaee-000000000001` (current cycle). Answered and reviewed: `…02` (1 week back), `…03` (4 weeks back), `…05` (6 weeks back). Missed: `…04` (5 weeks back). Plus the existing [#1095 rows](#seeded-inbox-conversation--weekly-check-ins-1095) at 2 and 3 weeks back | 4 of 6 answered overall; the 8-week strip has one `none` slot at 7 weeks back |
| Session this week | `00000000-0000-0000-aaff-000000000001`, standalone-only session, completed, dated this Monday | "Done this week" 1 of 3 on Mon–Wed, 2 of 3 on Thu–Sun (the existing #457 execution is dated reset − 3 days) |

Who sees what:

- **QA trainer** (Training only) — identity, profile, weight, PR, check-in
  trend, sessions bar. No meal data; the Meal compliance tile shows an em
  dash because the link grants no nutrition access.
- **QA nutritionist** — identity, profile, weight and the meal-log rows and
  compliance. Its own check-in trend is empty (history only returns the
  caller's rows).

New answered check-ins are marked reviewed, so the inbox "New check-ins" chip
still counts only the #1095 row. The waiting check-in is a Pending row for the
QA client, due two days after reset.

## Seeded both-roles coach (#1311)

A coach with both roles and a client of their own, so the client detail
Overview's both-roles layout (boards PageClientDetailC/D) can be checked. Rich
seed only. Because a client may have only one active coach per profession, the
coach is **not** linked to the QA client; they get the "QA Duo" client instead.
Both accounts use `QA_SEED_PASSWORD`.

| Fixture | Ids / shape | Expected values |
|---|---|---|
| Coach | `qa.coach@formup.test`, "QA Coach", roles Trainer + Nutritionist, profile PublicId `99999999-9999-9999-bbbb-000000000001` | — |
| Client | `qa.client.both@formup.test`, "QA Duo", profile PublicId `44444444-4444-4444-aaaa-000000000001` | Height 175 cm, goal LoseFat, target weight 66 kg; other onboarding answers and nutrition targets as the QA client's |
| Link | One active link, QA Coach → QA Duo, role Nutritionist, `CanViewNutritionPlans` and `CanViewTrainingPlans` both true | Overview shows the both-roles layout |
| Nutrition plan | `44444444-4444-4444-eeee-000000000001`, "QA Duo Nutrition Plan", author QA Coach, starts last Monday, 2 weeks, 3 meals a day | 2500 kcal, 26/38/36 % |
| Training plan | `44444444-4444-4444-dddd-000000000001`, "QA Duo Training Plan", author QA Coach, starts this Monday, 1 published week, sessions A/B/C on the reset weekday, +2 and +4 days | — |
| Session this week | Session A completed, dated today | "Done this week" 1 of 3 |
| Personal record | `44444444-4444-4444-aadd-000000000001`, dated today | QA Squat 72.5 kg × 8; "PRs this month" 1 on any reset day |
| Body measurements | 8 weekly, newest yesterday | Weight 73.6 → 70.0 kg |
| Meal logs | 13 on days −1 … −6 (3, 2, 3, 2, 2, 1), none today | Meal compliance 13 of 24 = 54.2 % (tile "54 %"), "6-day logging streak" (#1319). The combined figure across both disciplines is 56 % / 7 days |
| Weekly check-ins (Training) | Anchor = next ISO Monday: waiting (current cycle), answered 1, 4 and 6 weeks back (reviewed), missed 5 weeks back | 3 of 4 answered overall |

The training plan covers one week from the reset Monday, so like the other
fixtures it goes stale a week after `/test/reset`.

## Seeded lapsed and gift-ended coaches (#2051)

Two extra trainer accounts for the coach-subscription states from #1596 and
#2036. Every other QA coach stays on the Studio plan, so existing specs are
unaffected. Both new coaches are hidden from public search and use
`QA_SEED_PASSWORD`. Each has one client more than the Free plan's cap (cap read
from the Free plan row, seeded as 2, so 3 clients each). Clients are linked a
week apart, so the newest one is always the one over the cap.

| Coach | Subscription | Clients | What it exercises |
|---|---|---|---|
| `qa.coach.lapsed@formup.test` | Pro plan, status Canceled, no gift | `qa.lapsed.client1` … `client3@formup.test`; client 3 is over the cap | #1596: lapsed to Free, keep-clients screen, `CLIENT_OVER_PLAN_LIMIT`, `runsUntil` |
| `qa.coach.gift@formup.test` | Start plan given free (`GivenFreeAt` 60 days ago, `FreeUntil` 14 days ago) | `qa.gift.client1` … `client3@formup.test`; client 3 would be over the cap after choosing Free | #2036: plan-choice-required, blocking picker, choose-Free with a keep list |

Users, profiles, subscriptions and links are seeded in both seed kinds. The
rich kind also gives each coach's client 3 a training plan with one published
week: the lapsed coach's week ended about three weeks before reset (read-only
after the last week), the gift coach's week is the current one.

**These accounts change when you use them.** Choosing which clients to keep or
picking Free writes link states and the subscription row. Re-running the seed
leaves those changes in place on purpose; only `/test/reset` restores both
coaches to the states above.

---

## CI

The `e2e.yml` GitHub Actions workflow sources `JWT_SECRET` and `QA_SEED_PASSWORD` from repository-level secrets (`secrets.JWT_SECRET`, `secrets.QA_SEED_PASSWORD`) and writes them into `.env.test` before `npm run e2e:up`. Configure these secrets at `Settings → Secrets and variables → Actions` before the first CI run, or the workflow will fail at the env-file write step with a clear error message.

> **Compose harness rebuild.** After this change merges, rebuild the harness API image
> (`docker compose -f docker-compose.test.yml build api`) so `npm run e2e:up` picks up
> the new seed. The image is not rebuilt automatically on `npm run e2e:up` unless the
> Dockerfile or its dependencies change.
