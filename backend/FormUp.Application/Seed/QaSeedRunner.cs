using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace FormUp.Application.Seed;

/// <summary>
/// Deterministic fixture for the docker-compose end-to-end test harness.
/// Idempotent: re-running over an existing fixture is a no-op so qa-tester
/// can hit the same IDs and emails on every run.
///
/// Seeded relationships:
/// - QA Client (11111111-...) has a ClientProfile with PublicId = ClientProfilePublicId.
/// - QA Trainer (22222222-...) has a ProfessionalProfile with PublicId = TrainerProfilePublicId.
/// - A ClientProfessionalLink ties the two with IsActive=true so the trainer
///   dashboard shows "QA Client" without any further setup.
/// - QA Nutri (33333333-...) has a ProfessionalProfile. A ClientProfessionalLink
///   (ProfessionalRole=Nutritionist) to the QA client is seeded as part of the
///   #720 nutritionist-owned questionnaire fixture (Rich seed path only).
/// - A TrainingPlan (dddddddd-...) is seeded for the QA client with a Published week
///   containing (Monday) one session with four workouts:
///   Workout 1 — ForTime + 0 exercises (the #258 bug shape).
///   Workout 2 — AMRAP + 2 synthetic exercises (non-regression).
///   Workout 3 — Standard (null format) + 2 synthetic exercises (non-regression).
///   Workout 4 — Tabata (20s/10s × 8) + 1 synthetic exercise (#327 iOS QA fixture).
///   Plus (#857 phase 3a/3b), on Tuesday, a session with ONLY standalone exercises
///   (no workouts), and on Wednesday, a session where the same catalog exercise
///   appears BOTH standalone AND nested inside one of the session's workouts, each
///   with a distinct ExerciseId instance value.
/// </summary>
public static class QaSeedRunner
{
    // User IDs are spelled out here so QA fixtures stay stable across rebuilds —
    // qa-tester references them directly in evidence (curl probes, Playwright
    // selectors). Changing them is a fixture-version bump.
    public static readonly Guid ClientUserId    = new("11111111-1111-1111-1111-111111111111");
    public static readonly Guid TrainerUserId   = new("22222222-2222-2222-2222-222222222222");
    public static readonly Guid NutriUserId     = new("33333333-3333-3333-3333-333333333333");

    // Stable PublicIds for profile rows — the public identifier trainers/nutritionists
    // use to reference a client (route params, DTOs). Unrelated to the Mongo document
    // clientId key since #840 — see ClientUserId/Client2UserId for that.
    public static readonly Guid ClientProfilePublicId  = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    public static readonly Guid TrainerProfilePublicId = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    public static readonly Guid NutriProfilePublicId   = new("cccccccc-cccc-cccc-cccc-cccccccccccc");

    // Stable ExternalId for the seeded training plan (ForTime + 0-exercise fixture).
    // ClientId on the plan = ClientUserId (ApplicationUser.Id, #840) — every Mongo
    // document's clientId field uses this canonical identifier.
    public static readonly Guid QaTrainingPlanExternalId = new("dddddddd-dddd-dddd-dddd-dddddddddddd");

    // -------------------------------------------------------------------------
    // #326 past-dated training plan — exercises three past-session states so
    // Playwright can assert completed/skipped/untouched UI behaviour.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Past-dated Active training plan owned by qa.trainer for qa.client.
    /// StartDate is set to ~4 weeks before the seed instant (anchored to the
    /// preceding Monday) so all sessions in Weeks 1–2 fall in the past.
    /// </summary>
    public static readonly Guid QaPastTrainingPlanExternalId = new("11111111-1111-1111-2222-000000000001");

    /// <summary>
    /// Session in Week 1, DayOfWeek=1 (Monday): a WorkoutLog with IsCompleted=true
    /// exists → web classifies as COMPLETED (read-only).
    /// </summary>
    public static readonly Guid QaPastSessionCompletedId = new("11111111-1111-1111-2222-000000000002");

    /// <summary>
    /// Session in Week 1, DayOfWeek=3 (Wednesday): a WorkoutLog with IsCompleted=false
    /// exists → web classifies as SKIPPED (editable + Mark-finished).
    /// </summary>
    public static readonly Guid QaPastSessionSkippedId = new("11111111-1111-1111-2222-000000000003");

    /// <summary>
    /// Session in Week 2, DayOfWeek=1 (Monday): NO WorkoutLog exists
    /// → web classifies as UNTOUCHED (editable + Mark-finished).
    /// </summary>
    public static readonly Guid QaPastSessionUntouchedId = new("11111111-1111-1111-2222-000000000004");

    // Stable WorkoutLog ExternalIds.
    public static readonly Guid QaPastCompletedExecutionId = new("11111111-1111-1111-2222-000000000005");
    public static readonly Guid QaPastSkippedExecutionId   = new("11111111-1111-1111-2222-000000000006");

    // -------------------------------------------------------------------------
    // #457 — Main plan (dddd...) WorkoutLog with four-case planned-vs-actual sets.
    // A distinct GUID so the WorkoutLogs==0 minimal-kind assertion is not affected
    // (gated to Rich seed path only, same as the past-plan logs above).
    // -------------------------------------------------------------------------

    /// <summary>
    /// Completed WorkoutLog for the main QA training plan (dddddddd-...), Standard section.
    /// Exercises four per-set cases needed by the planned-vs-actual UI surface:
    ///   Exercise 1 (QA Squat) — Set 1: modified (actual != planned), Set 2: as-prescribed.
    ///   Exercise 2 (QA Deadlift) — Set 1: skipped (planned present, actual null), Set 2: extra (no planned snapshot).
    /// </summary>
    public static readonly Guid QaMainPlanCompletedExecutionId = new("11111111-1111-1111-4455-000000000001");

    // -------------------------------------------------------------------------
    // #474 — Multi-section fixture: second client/trainer pair with a session
    // where the SAME exercise appears in both a Standard section AND an AMRAP
    // section. Demonstrates section-keyed planned-vs-actual read path (coach-detail).
    // -------------------------------------------------------------------------

    // Second QA client/trainer pair — separate from the #457/#326 pair so the
    // two planned-vs-actual scenarios are independently exercisable.
    public static readonly Guid Client2UserId    = new("55555555-5555-5555-5555-555555555555");
    public static readonly Guid Trainer2UserId   = new("66666666-6666-6666-6666-666666666666");
    public static readonly Guid Client2ProfilePublicId  = new("55555555-5555-5555-aaaa-000000000001");
    public static readonly Guid Trainer2ProfilePublicId = new("66666666-6666-6666-bbbb-000000000001");

    public const string Client2Email  = "qa.client2@formup.test";
    public const string Trainer2Email = "qa.trainer2@formup.test";

    // Training plan for the multi-section fixture.
    public static readonly Guid QaMultiSectionPlanExternalId = new("55555555-5555-5555-dddd-000000000001");

    // Session in the multi-section plan.
    public static readonly Guid QaMultiSectionSessionId = new("55555555-5555-5555-bbbb-000000000001");

    // Standard section — edited reps/weights logged here.
    public static readonly Guid MultiSectionStandardWorkoutId = new("55555555-5555-5555-aaaa-000000000001");

    // AMRAP section — left at planned values (no edits).
    public static readonly Guid MultiSectionAmrapWorkoutId = new("55555555-5555-5555-aaaa-000000000002");

    // The SAME exercise appears in BOTH sections to prove section-keyed lookup
    // returns independent values per section.
    public static readonly Guid SharedExerciseId = new("55555555-5555-5555-cccc-000000000001");

    // WorkoutLog for the completed multi-section session.
    public static readonly Guid QaMultiSectionExecutionId = new("55555555-5555-5555-4455-000000000001");

    // Section ID within the main-plan completed WorkoutLog (mirrors StandardSectionId).
    public static readonly Guid MainPlanCompletedWorkoutId = new("11111111-1111-1111-4455-000000000002");

    // Section IDs within the three past sessions.
    public static readonly Guid PastCompletedWorkoutId = new("11111111-1111-1111-3333-000000000001");
    public static readonly Guid PastSkippedWorkoutId   = new("11111111-1111-1111-3333-000000000002");
    public static readonly Guid PastUntouchedWorkoutId = new("11111111-1111-1111-3333-000000000003");

    // Stable SectionIds — deterministic for test assertions.
    public static readonly Guid ForTimeSectionId   = new("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
    public static readonly Guid AmrapSectionId     = new("ffffffff-ffff-ffff-ffff-ffffffffffff");
    public static readonly Guid StandardSectionId  = new("00000000-0000-0000-aaaa-000000000001");

    // Stable SessionId.
    public static readonly Guid QaSessionId = new("00000000-0000-0000-bbbb-000000000001");

    // Stable ExternalIds for the synthetic exercises in AMRAP + Standard + Tabata sections.
    public static readonly Guid AmrapExercise1Id   = new("00000000-0000-0000-cccc-000000000001");
    public static readonly Guid AmrapExercise2Id   = new("00000000-0000-0000-cccc-000000000002");
    public static readonly Guid StandardExercise1Id = new("00000000-0000-0000-dddd-000000000001");
    public static readonly Guid StandardExercise2Id = new("00000000-0000-0000-dddd-000000000002");

    // Stable SectionId + exercise for the Tabata section (Order=3, #327 iOS QA fixture).
    public static readonly Guid TabataSectionId    = new("00000000-0000-0000-aaaa-000000000002");
    public static readonly Guid TabataExercise1Id  = new("00000000-0000-0000-eeee-000000000006");

    // #588 — the six synthetic exercise ids used by the past-dated training plan
    // (EnsurePastTrainingPlanAsync) and its WorkoutLogs. Previously inline Guid
    // literals; named here so EnsureExercisesAsync can insert matching catalog
    // docs from a single source of truth (no drift between the plan seed and
    // the catalog seed).
    public static readonly Guid PastBenchPressExerciseId       = new("11111111-1111-1111-4444-000000000001");
    public static readonly Guid PastOverheadPressExerciseId    = new("11111111-1111-1111-4444-000000000002");
    public static readonly Guid PastBackSquatExerciseId        = new("11111111-1111-1111-4444-000000000003");
    public static readonly Guid PastRomanianDeadliftExerciseId = new("11111111-1111-1111-4444-000000000004");
    public static readonly Guid PastPulldownExerciseId         = new("11111111-1111-1111-4444-000000000005");
    public static readonly Guid PastSeatedRowExerciseId        = new("11111111-1111-1111-4444-000000000006");

    // -------------------------------------------------------------------------
    // #857 phase 3b — SessionExercise.ExerciseId instance ids for every exercise
    // seeded above. Distinct per occurrence so per-exercise completion
    // (MarkExerciseComplete/-Incomplete) is reachable on this fixture: before
    // this every seeded SessionExercise left ExerciseId at its Guid.Empty
    // default, which MarkExerciseCompleteValidator rejects (NotEmpty), and
    // every exercise in a session shared one empty instance id — the exact
    // ambiguity the instance id exists to remove.
    // -------------------------------------------------------------------------
    public static readonly Guid AmrapExercise1InstanceId    = new("00000000-0000-0001-cccc-000000000001");
    public static readonly Guid AmrapExercise2InstanceId    = new("00000000-0000-0001-cccc-000000000002");
    public static readonly Guid StandardExercise1InstanceId = new("00000000-0000-0001-dddd-000000000001");
    public static readonly Guid StandardExercise2InstanceId = new("00000000-0000-0001-dddd-000000000002");
    public static readonly Guid TabataExercise1InstanceId   = new("00000000-0000-0001-eeee-000000000006");

    public static readonly Guid PastBenchPressInstanceId       = new("11111111-1111-1111-5555-000000000001");
    public static readonly Guid PastOverheadPressInstanceId    = new("11111111-1111-1111-5555-000000000002");
    public static readonly Guid PastBackSquatInstanceId        = new("11111111-1111-1111-5555-000000000003");
    public static readonly Guid PastRomanianDeadliftInstanceId = new("11111111-1111-1111-5555-000000000004");
    public static readonly Guid PastPulldownInstanceId         = new("11111111-1111-1111-5555-000000000005");
    public static readonly Guid PastSeatedRowInstanceId        = new("11111111-1111-1111-5555-000000000006");

    // #474 multi-section fixture — same catalog exercise (SharedExerciseId), two distinct
    // instance ids, one per section, proving section-independent completion tracking.
    public static readonly Guid MultiSectionStandardInstanceId = new("55555555-5555-5555-cccc-000000000002");
    public static readonly Guid MultiSectionAmrapInstanceId    = new("55555555-5555-5555-cccc-000000000003");

    // -------------------------------------------------------------------------
    // #857 QA fixture — two new session shapes the training-plan tree restructure
    // makes possible: a session with ONLY standalone exercises (no workouts), and
    // a session where the same catalog exercise appears BOTH standalone AND nested
    // inside one of that session's workouts (distinct ExerciseId instance values).
    // -------------------------------------------------------------------------
    public static readonly Guid QaStandaloneOnlySessionId  = new("00000000-0000-0000-bbbb-000000000002");
    public static readonly Guid QaStandaloneOnlyExerciseId = new("00000000-0000-0000-cccc-000000000003"); // catalog: QA Plank
    public static readonly Guid QaStandaloneOnlyInstanceId = new("00000000-0000-0001-cccc-000000000003");

    public static readonly Guid QaDualPlacementSessionId            = new("00000000-0000-0000-bbbb-000000000003");
    public static readonly Guid QaDualPlacementWorkoutId             = new("00000000-0000-0000-aaaa-000000000003");
    public static readonly Guid QaDualPlacementExerciseId            = new("00000000-0000-0000-cccc-000000000004"); // catalog: QA Wall Ball
    public static readonly Guid QaDualPlacementStandaloneInstanceId  = new("00000000-0000-0001-cccc-000000000004");
    public static readonly Guid QaDualPlacementNestedInstanceId      = new("00000000-0000-0001-cccc-000000000005");

    // #879 — SessionExecution marking ONLY the standalone occurrence of the dual-placement
    // fixture complete, so the two instance placements are observably distinguishable via
    // GET /client/training/plans/{planId} instead of requiring an interactive drive.
    public static readonly Guid QaDualPlacementSessionExecutionId = new("00000000-0000-0001-cccc-000000000006");

    // #946 — Photo diary request fixtures on the nutritionist<->client link. Both are
    // Status=Accepted so they surface on the client app's active-request list. One is
    // scoped to the seeded QA nutrition plan; the other is deliberately plan-less — a
    // legitimate backend state per PhotoDiaryRequest.PlanId's own doc comment.
    public static readonly Guid QaPhotoDiaryRequestWithPlanId  = new("00000000-0000-0000-9999-000000000001");
    public static readonly Guid QaPhotoDiaryRequestPlanlessId  = new("00000000-0000-0000-9999-000000000002");

    // -------------------------------------------------------------------------
    // #1095 — Inbox + chat fixture. A third client linked to qa.trainer with NO conversation
    // and NO active plan, so the NoMessages filter chip stays non-zero and clients.spec.ts's
    // strict-mode '1 active plan' locator (:156) stays unique to qa.client's seeded plan. A
    // conversation between qa.trainer and qa.client with >=3 messages (one unread from the
    // client, one coach message containing a YouTube URL). Two WeeklyCheckIn rows scoped to
    // Training (the profession the qa.trainer<->qa.client link grants) — one responded but
    // not yet reviewed (NewCheckIns), one expired (MissingCheckIns).
    // -------------------------------------------------------------------------
    public static readonly Guid Client3UserId          = new("77777777-7777-7777-7777-777777777777");
    public static readonly Guid Client3ProfilePublicId = new("77777777-7777-7777-aaaa-000000000001");
    public const string Client3Email = "qa.client3@formup.test";

    public static readonly Guid QaInboxConversationId = new("00000000-0000-0000-aabb-000000000001");
    public static readonly Guid QaInboxMessage1Id      = new("00000000-0000-0000-aabb-000000000002");
    public static readonly Guid QaInboxMessage2Id      = new("00000000-0000-0000-aabb-000000000003");
    public static readonly Guid QaInboxMessage3Id      = new("00000000-0000-0000-aabb-000000000004");

    public static readonly Guid QaWeeklyCheckInRespondedUnreviewedId = new("00000000-0000-0000-aabb-000000000005");
    public static readonly Guid QaWeeklyCheckInExpiredId             = new("00000000-0000-0000-aabb-000000000006");

    // -------------------------------------------------------------------------
    // #1101 — Ended-collaboration inbox fixture. A client whose link to qa.trainer is ended
    // (IsActive=false, the exact state EndCollaborationEndpoint leaves behind), with a
    // conversation holding one unread client message. The thread is off the live roster, so it
    // shows only under the inbox's All chip and under none of the other five. The display name
    // is "QA Former" — it must never contain "QA Client", which three Playwright specs use to
    // pick the first matching row.
    // -------------------------------------------------------------------------
    public static readonly Guid ClientFormerUserId          = new("88888888-8888-8888-8888-888888888888");
    public static readonly Guid ClientFormerProfilePublicId = new("88888888-8888-8888-aaaa-000000000001");
    public const string ClientFormerEmail = "qa.client.former@formup.test";

    public static readonly Guid QaEndedLinkConversationId = new("00000000-0000-0000-aabb-000000000011");
    public static readonly Guid QaEndedLinkMessage1Id     = new("00000000-0000-0000-aabb-000000000012");
    public static readonly Guid QaEndedLinkMessage2Id     = new("00000000-0000-0000-aabb-000000000013");

    // -------------------------------------------------------------------------
    // Ending client — a client with a LIVE link to qa.trainer that an e2e spec ends itself via
    // DELETE /client/collaborations/{publicId}. No conversation, no plan, no payment terms, no
    // package. The display name is "QA Ending" — it must never contain "QA Client".
    // -------------------------------------------------------------------------
    public static readonly Guid ClientEndingUserId          = new("10101010-1010-1010-1010-101010101010");
    public static readonly Guid ClientEndingProfilePublicId = new("10101010-1010-1010-aaaa-000000000001");
    public const string ClientEndingEmail = "qa.client.ending@formup.test";

    // -------------------------------------------------------------------------
    // #1303 — Client detail Overview fixtures for qa.client (Rich seed only): eight weekly body
    // measurements, onboarding answers, meal logs against qa.nutri's plan, one personal record,
    // check-in history around the NextIsoMonday anchor and one completed session this Monday.
    // -------------------------------------------------------------------------
    public static readonly Guid[] QaBodyMeasurementIds =
    [
        new("00000000-0000-0000-aacc-000000000001"),
        new("00000000-0000-0000-aacc-000000000002"),
        new("00000000-0000-0000-aacc-000000000003"),
        new("00000000-0000-0000-aacc-000000000004"),
        new("00000000-0000-0000-aacc-000000000005"),
        new("00000000-0000-0000-aacc-000000000006"),
        new("00000000-0000-0000-aacc-000000000007"),
        new("00000000-0000-0000-aacc-000000000008"),
    ];

    public static readonly Guid QaPersonalRecordExternalId = new("00000000-0000-0000-aadd-000000000001");

    // Check-in history rows, named by their distance from the NextIsoMonday anchor (the "waiting" slot).
    public static readonly Guid QaCheckInWaitingId           = new("00000000-0000-0000-aaee-000000000001");
    public static readonly Guid QaCheckInAnsweredLastWeekId  = new("00000000-0000-0000-aaee-000000000002");
    public static readonly Guid QaCheckInAnsweredWeek4Id     = new("00000000-0000-0000-aaee-000000000003");
    public static readonly Guid QaCheckInMissedWeek5Id       = new("00000000-0000-0000-aaee-000000000004");
    public static readonly Guid QaCheckInAnsweredWeek6Id     = new("00000000-0000-0000-aaee-000000000005");

    /// <summary>Completed execution of the standalone-only session dated this Monday.</summary>
    public static readonly Guid QaMondaySessionExecutionId = new("00000000-0000-0000-aaff-000000000001");

    // -------------------------------------------------------------------------
    // #1311 — Both-roles fixture (Rich seed only): qa.coach holds Trainer AND Nutritionist and
    // coaches "QA Duo" through ONE link (role Nutritionist, both capability flags), the shape
    // AcceptClientInviteEndpoint writes. qa.coach is deliberately NOT linked to qa.client:
    // a client has at most one active coach per profession.
    // -------------------------------------------------------------------------
    public static readonly Guid CoachUserId             = new("99999999-9999-9999-9999-999999999999");
    public static readonly Guid CoachProfilePublicId    = new("99999999-9999-9999-bbbb-000000000001");
    public static readonly Guid ClientDuoUserId         = new("44444444-4444-4444-4444-444444444444");
    public static readonly Guid ClientDuoProfilePublicId = new("44444444-4444-4444-aaaa-000000000001");

    public const string CoachEmail      = "qa.coach@formup.test";
    public const string ClientDuoEmail  = "qa.client.both@formup.test";

    public static readonly Guid QaDuoNutritionPlanExternalId = new("44444444-4444-4444-eeee-000000000001");
    public static readonly Guid QaDuoTrainingPlanExternalId  = new("44444444-4444-4444-dddd-000000000001");

    // -------------------------------------------------------------------------
    // Free-plan cap fixtures (both seed kinds for users, profiles, subscriptions and links; Rich
    // only for the training plans). Two coaches, each with cap+1 NEW clients (cap is read from the
    // Free plan row). Client n is activated (7 x (cap + 1 - n + 1)) days ago, so the oldest clients
    // are kept and the newest, client number cap+1, is the one over the cap.
    //  - Lapsed coach: paid plan, Canceled, no gift. Resolves lapsed with one over-cap client.
    //  - Gift coach: paid plan given free, gift ended. Resolves plan-choice-required, nobody over
    //    the cap until a plan is chosen.
    // Neither coach is on Studio and neither shows in public search.
    // -------------------------------------------------------------------------
    private const string LapsedGuidPrefix = "20202020";
    private const string GiftGuidPrefix   = "30303030";

    public const string LapsedCoachEmail = "qa.coach.lapsed@formup.test";
    public const string GiftCoachEmail   = "qa.coach.gift@formup.test";

    public static readonly Guid LapsedCoachUserId = CapFixtureGuid(LapsedGuidPrefix, "0000", 0);
    public static readonly Guid GiftCoachUserId   = CapFixtureGuid(GiftGuidPrefix, "0000", 0);

    public static string LapsedClientEmail(int clientNumber) => $"qa.lapsed.client{clientNumber}@formup.test";
    public static string GiftClientEmail(int clientNumber)   => $"qa.gift.client{clientNumber}@formup.test";

    public static Guid LapsedClientUserId(int clientNumber) => CapFixtureGuid(LapsedGuidPrefix, "0000", clientNumber);
    public static Guid GiftClientUserId(int clientNumber)   => CapFixtureGuid(GiftGuidPrefix, "0000", clientNumber);

    private static Guid CapFixtureGuid(string prefix, string segment, int index) =>
        new($"{prefix}-{prefix[..4]}-{prefix[..4]}-{segment}-{index:D12}");

    // Sessions of the Duo training plan. Session A is the completed one and always sits on the
    // seed-time weekday; B and C sit two and four weekdays later (wrapping inside the week).
    public static readonly Guid QaDuoSessionAId = new("44444444-4444-4444-bbbb-000000000001");
    public static readonly Guid QaDuoSessionBId = new("44444444-4444-4444-bbbb-000000000002");
    public static readonly Guid QaDuoSessionCId = new("44444444-4444-4444-bbbb-000000000003");

    // Exercise instance ids (distinct per plan; catalog ids are shared with the main fixture).
    public static readonly Guid QaDuoSquatInstanceId    = new("44444444-4444-4444-cccc-000000000001");
    public static readonly Guid QaDuoDeadliftInstanceId = new("44444444-4444-4444-cccc-000000000002");
    public static readonly Guid QaDuoPlankInstanceId    = new("44444444-4444-4444-cccc-000000000003");
    public static readonly Guid QaDuoPullUpInstanceId   = new("44444444-4444-4444-cccc-000000000004");

    public static readonly Guid QaDuoSessionExecutionId = new("44444444-4444-4444-aaff-000000000001");
    public static readonly Guid QaDuoPersonalRecordExternalId = new("44444444-4444-4444-aadd-000000000001");

    public static readonly Guid[] QaDuoBodyMeasurementIds =
    [
        new("44444444-4444-4444-aacc-000000000001"),
        new("44444444-4444-4444-aacc-000000000002"),
        new("44444444-4444-4444-aacc-000000000003"),
        new("44444444-4444-4444-aacc-000000000004"),
        new("44444444-4444-4444-aacc-000000000005"),
        new("44444444-4444-4444-aacc-000000000006"),
        new("44444444-4444-4444-aacc-000000000007"),
        new("44444444-4444-4444-aacc-000000000008"),
    ];

    /// <summary>Duo check-in rows: waiting, answered last week, answered week 4, missed week 5, answered week 6.</summary>
    public static readonly Guid[] QaDuoCheckInIds =
    [
        new("44444444-4444-4444-aaee-000000000001"),
        new("44444444-4444-4444-aaee-000000000002"),
        new("44444444-4444-4444-aaee-000000000003"),
        new("44444444-4444-4444-aaee-000000000004"),
        new("44444444-4444-4444-aaee-000000000005"),
    ];

    // Foods — owned by Nutri (NutritionistId = NutriUserId, the ApplicationUser.Id).
    // CreateFoodEndpoint sets NutritionistId = Guid.Parse(AppClaims.UserId) (the user id, NOT the
    // ProfessionalProfile.PublicId), and the ownership guard in UploadFoodImageUrlEndpoint compares
    // food.NutritionistId against the same user-id claim. Using NutriProfilePublicId here would
    // make the seeded foods fail the ownership check with FOOD_NOT_OWNED (HTTP 400).
    public static readonly Guid QaFood1ExternalId = new("00000000-0000-0000-eeee-000000000001"); // Chicken Breast 100g
    public static readonly Guid QaFood2ExternalId = new("00000000-0000-0000-eeee-000000000002"); // White Rice 100g cooked
    public static readonly Guid QaFood3ExternalId = new("00000000-0000-0000-eeee-000000000003"); // Broccoli 100g
    public static readonly Guid QaFood4ExternalId = new("00000000-0000-0000-eeee-000000000004"); // Banana medium
    public static readonly Guid QaFood5ExternalId = new("00000000-0000-0000-eeee-000000000005"); // Rolled Oats 50g

    // Recipes — owned by Nutri.
    public static readonly Guid QaRecipe1ExternalId = new("00000000-0000-0000-ffff-000000000001"); // Chicken + Rice + Broccoli bowl
    public static readonly Guid QaRecipe2ExternalId = new("00000000-0000-0000-ffff-000000000002"); // Oats + Banana breakfast
    public static readonly Guid QaRecipe3ExternalId = new("00000000-0000-0000-ffff-000000000003"); // Chicken + Broccoli stir-fry

    // Nutrition plan — Author = Nutri, Client = QA Client.
    public static readonly Guid QaNutritionPlanExternalId = new("dddddddd-eeee-ffff-0000-111111111111");

    // -------------------------------------------------------------------------
    // #715 — Questionnaire fixture: a submitted client response with a spread
    // of question types, linked to the main training plan (dddddddd-...) so
    // the "Dotaznik" answers tab on the training plan detail page (#697)
    // renders a populated response. The nutrition plan's link is handled by
    // the separate nutritionist-owned fixture below (#720).
    // -------------------------------------------------------------------------

    /// <summary>Questionnaire template owned by the QA trainer.</summary>
    public static readonly Guid QaQuestionnaireExternalId = new("00000000-0000-0000-7777-000000000000");

    /// <summary>Submitted response owned by the QA trainer, linked to the training plan.</summary>
    public static readonly Guid QaQuestionnaireResponseExternalId = new("00000000-0000-0000-7777-000000000099");

    // Section headers (Type="section" — non-answerable, rendered as group titles).
    public static readonly Guid QaQuestionSectionBasicInfoId = new("00000000-0000-0000-7777-000000000001");
    public static readonly Guid QaQuestionSectionHealthId    = new("00000000-0000-0000-7777-000000000005");

    // Answerable questions — one per formatAnswerValue branch (web/src/components/questionnaire/questionnaire-helpers.tsx).
    public static readonly Guid QaQuestionGoalId         = new("00000000-0000-0000-7777-000000000002"); // short_text
    public static readonly Guid QaQuestionWeightId       = new("00000000-0000-0000-7777-000000000003"); // number
    public static readonly Guid QaQuestionTrainingDaysId = new("00000000-0000-0000-7777-000000000004"); // single_choice
    public static readonly Guid QaQuestionEnergyId       = new("00000000-0000-0000-7777-000000000006"); // scale
    public static readonly Guid QaQuestionInjuriesId     = new("00000000-0000-0000-7777-000000000007"); // multi_select
    public static readonly Guid QaQuestionMedicalDocId   = new("00000000-0000-0000-7777-000000000008"); // file_upload

    // -------------------------------------------------------------------------
    // #720 — Nutritionist-owned questionnaire fixture: a second template +
    // submitted response, owned by the QA nutritionist (ProfessionalId =
    // NutriUserId), linked to the seeded nutrition plan (dddddddd-eeee-...).
    // This REPLACES the #715 trainer-owned link on the nutrition plan — a
    // nutrition plan should link a nutritionist-owned response so the
    // nutritionist's own "Dotaznik" tab (#698) renders a populated view via
    // GetClientResponsesEndpoint, which filters by the CALLING professional's
    // ProfessionalId. The training plan keeps its #715 trainer-owned link
    // unchanged (#697 is unaffected).
    // -------------------------------------------------------------------------

    /// <summary>Questionnaire template owned by the QA nutritionist.</summary>
    public static readonly Guid QaNutriQuestionnaireExternalId = new("00000000-0000-0000-8888-000000000000");

    /// <summary>Submitted response owned by the QA nutritionist, linked to the nutrition plan.</summary>
    public static readonly Guid QaNutriQuestionnaireResponseExternalId = new("00000000-0000-0000-8888-000000000099");

    // Section headers (Type="section" — non-answerable, rendered as group titles).
    public static readonly Guid QaNutriQuestionSectionIntakeId    = new("00000000-0000-0000-8888-000000000001");
    public static readonly Guid QaNutriQuestionSectionLifestyleId = new("00000000-0000-0000-8888-000000000005");

    // Answerable questions — same spread of types as #715 (short_text, number,
    // single_choice, scale, multi_select, file_upload).
    public static readonly Guid QaNutriQuestionDietGoalId    = new("00000000-0000-0000-8888-000000000002"); // short_text
    public static readonly Guid QaNutriQuestionCaloriesId    = new("00000000-0000-0000-8888-000000000003"); // number
    public static readonly Guid QaNutriQuestionMealsPerDayId = new("00000000-0000-0000-8888-000000000004"); // single_choice
    public static readonly Guid QaNutriQuestionAppetiteId    = new("00000000-0000-0000-8888-000000000006"); // scale
    public static readonly Guid QaNutriQuestionAllergiesId   = new("00000000-0000-0000-8888-000000000007"); // multi_select
    public static readonly Guid QaNutriQuestionFoodDiaryId   = new("00000000-0000-0000-8888-000000000008"); // file_upload

    // MinIO blob keys (deterministic per QA fixture).
    public const string QaAvatarBlobKey    = "avatars/qa-client-11111111.png";
    public const string QaFoodImageBlobKey = "foods/qa-food-1.png";

    public const string ClientEmail   = "qa.client@formup.test";
    public const string TrainerEmail  = "qa.trainer@formup.test";
    public const string NutriEmail    = "qa.nutri@formup.test";
    // #474 second pair — separate accounts so multi-section fixture is independently exercisable.

    // Sourced from QA_SEED_PASSWORD via .env.test (gitignored). The harness
    // refuses to seed without it so a missing env file fails fast instead of
    // creating users with a default password.
    private static string Password =>
        Environment.GetEnvironmentVariable("QA_SEED_PASSWORD")
            ?? throw new InvalidOperationException(
                "QA_SEED_PASSWORD is not set. Copy .env.test.example to .env.test and fill it in.");

    /// <summary>
    /// Selects how much of the fixture to seed.
    /// <list type="bullet">
    ///   <item><term>Rich</term><description>(default when QA_SEED_KIND is unset or "rich") — full fixture: users + profiles + link + training plan + foods + recipes + nutrition plan + image blobs.</description></item>
    ///   <item><term>Minimal</term><description>(QA_SEED_KIND=minimal) — users + profiles + trainer↔client link only. No plans, no foods, no blobs. Useful when a test only needs auth/profile and the additional rows just add noise.</description></item>
    /// </list>
    /// Switched via the QA_SEED_KIND env var so callers (compose `seed` service,
    /// `scripts/test-env seed --kind=…`) can opt in to a leaner reset without
    /// recompiling.
    /// </summary>
    public enum SeedKind { Minimal, Rich }

    /// <summary>
    /// Resolves <see cref="SeedKind"/> from the QA_SEED_KIND env var. Unset or
    /// empty → <see cref="SeedKind.Rich"/> (backwards-compatible default —
    /// the prior behaviour was always-rich). Unknown values fail fast so a
    /// typo in `--kind=ritch` doesn't silently fall through to rich.
    /// </summary>
    public static SeedKind ResolveKind()
    {
        var raw = Environment.GetEnvironmentVariable("QA_SEED_KIND");
        if (string.IsNullOrWhiteSpace(raw))
            return SeedKind.Rich;

        return raw.Trim().ToLowerInvariant() switch
        {
            "minimal" => SeedKind.Minimal,
            "rich"    => SeedKind.Rich,
            _ => throw new InvalidOperationException(
                $"QA_SEED_KIND='{raw}' is not a valid value. Expected 'minimal' or 'rich' (unset = rich).")
        };
    }

    public static async Task SeedAsync(IServiceProvider serviceProvider)
    {
        // Resolve the seed kind BEFORE touching the database. A typo in
        // QA_SEED_KIND throws here, so no users / profiles get created on
        // a bad invocation — preserves the "fail-fast on unknown" contract
        // documented on ResolveKind.
        var kind = ResolveKind();

        using var scope = serviceProvider.CreateScope();
        var sp = scope.ServiceProvider;
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("QaSeed");
        var userManager = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var db = sp.GetRequiredService<ApplicationDbContext>();
        var mongo = sp.GetRequiredService<IMongoContext>();

        await db.Database.MigrateAsync();

        await EnsureUserAsync(userManager, ClientUserId,  ClientEmail,  "QA",  "Client",   UserRole.Client,       logger);
        await EnsureUserAsync(userManager, TrainerUserId, TrainerEmail, "QA",  "Trainer",  UserRole.Trainer,      logger);
        await EnsureUserAsync(userManager, NutriUserId,   NutriEmail,   "QA",  "Nutri",    UserRole.Nutritionist, logger);

        // #474 — second pair for the multi-section fixture.
        await EnsureUserAsync(userManager, Client2UserId,  Client2Email,  "QA",  "Client2",  UserRole.Client,  logger);
        await EnsureUserAsync(userManager, Trainer2UserId, Trainer2Email, "QA",  "Trainer2", UserRole.Trainer, logger);

        // Profiles — each user requires a role-matching profile row so that
        // trainer endpoints (which look up ProfessionalProfile by UserId) and
        // client endpoints (which look up ClientProfile by UserId) work without
        // the users having gone through the normal registration flow.
        var clientProfile  = await EnsureClientProfileAsync(db, ClientUserId,  ClientProfilePublicId,  logger);
        var trainerProfile = await EnsureProfessionalProfileAsync(db, TrainerUserId, TrainerProfilePublicId, logger);
        var nutriProfile   = await EnsureProfessionalProfileAsync(db, NutriUserId,   NutriProfilePublicId,   logger);

        // #474 — profiles for the second pair.
        var client2Profile  = await EnsureClientProfileAsync(db, Client2UserId,  Client2ProfilePublicId,  logger);
        var trainer2Profile = await EnsureProfessionalProfileAsync(db, Trainer2UserId, Trainer2ProfilePublicId, logger, online: true, showInSearch: true);

        // Trainer↔client link — without this the trainer dashboard returns an
        // empty client list and Playwright's getByText('QA Client') never resolves.
        await EnsureTrainerClientLinkAsync(db, trainerProfile, clientProfile, logger);

        // #474 — trainer↔client link for the second pair (done regardless of kind
        // so minimal mode also has two clean pairs with working auth).
        await EnsureTrainerClientLinkAsync(db, trainer2Profile, client2Profile, logger);

        // Free-plan cap coaches (lapsed paid plan, ended gift) with their cap+1 clients each, in both kinds.
        var freePlanCap = await ReadFreePlanCapAsync(db);
        await EnsureCapCoachAsync(userManager, db, logger, freePlanCap, LapsedGuidPrefix, "Lapsed",
            LapsedCoachUserId, LapsedCoachEmail, LapsedClientUserId, LapsedClientEmail,
            planCode: "pro", SubscriptionStatus.Canceled, givenFreeAt: null, freeUntil: null);
        await EnsureCapCoachAsync(userManager, db, logger, freePlanCap, GiftGuidPrefix, "Gift",
            GiftCoachUserId, GiftCoachEmail, GiftClientUserId, GiftClientEmail,
            planCode: "start", SubscriptionStatus.Active,
            givenFreeAt: DateTimeOffset.UtcNow.AddDays(-60), freeUntil: DateTimeOffset.UtcNow.AddDays(-14));

        if (kind == SeedKind.Rich)
        {
            // #588 — exercise catalog docs for every synthetic SessionExercise id the
            // training-plan fixtures below reference. Must run BEFORE the training plans
            // so GET /exercises/{id} resolves for every id present in a seeded plan.
            await EnsureExercisesAsync(mongo, logger);

            // Training plan — ForTime + 0-exercise fixture for #258 non-regression.
            // ClientUserId (ApplicationUser.Id, #840) — not clientProfile.PublicId.
            await EnsureTrainingPlanAsync(mongo, ClientUserId, trainerProfile.PublicId, logger);

            // Main-plan completed WorkoutLog — exercises four planned-vs-actual set cases (#457).
            await EnsureMainPlanExecutionAsync(mongo, logger);

            // #879 — completion flag on the dual-placement fixture (QaDualPlacementSessionId)
            // so the standalone instance's completion is distinguishable from the nested one's.
            await EnsureDualPlacementCompletionAsync(mongo, logger);

            // Past-dated training plan — three sessions in distinct completion states for #326.
            await EnsurePastTrainingPlanAsync(mongo, ClientUserId, trainerProfile.PublicId, logger);

            // #474 — Multi-section plan + completed WorkoutLog for section-keying coach-detail fixture.
            await EnsureMultiSectionTrainingPlanAsync(mongo, Client2UserId, Trainer2UserId, logger);
            await EnsureMultiSectionExecutionAsync(mongo, logger);

            // Foods + Recipes + NutritionPlan.
            // NutriUserId (not nutriProfile.PublicId) — ownership guards in UploadFoodImageUrlEndpoint
            // and UploadRecipeImageUrlEndpoint compare NutritionistId against AppClaims.UserId,
            // which is ApplicationUser.Id. Using the profile PublicId would cause FOOD_NOT_OWNED /
            // RECIPE_NOT_OWNED (HTTP 400) when the e2e flow calls the upload-url endpoint.
            await EnsureFoodsAsync(mongo, NutriUserId, logger);
            await EnsureRecipesAsync(mongo, NutriUserId, logger);
            // ClientUserId (ApplicationUser.Id, #840) — not clientProfile.PublicId.
            await EnsureNutritionPlanAsync(mongo, ClientUserId, NutriUserId, logger);

            // #715 — Questionnaire template + submitted response owned by the
            // QA trainer, linked to the training plan created above.
            await EnsureQuestionnaireFixtureAsync(db, mongo, logger);

            // #720 — Second questionnaire template + submitted response owned
            // by the QA nutritionist, linked to the nutrition plan created
            // above (replacing the trainer-owned link #715 used to set there).
            await EnsureNutritionistQuestionnaireFixtureAsync(db, mongo, logger);

            // #946 — Photo diary request fixtures on the nutritionist<->client link. Must
            // run AFTER EnsureNutritionistQuestionnaireFixtureAsync, which is what creates
            // that link.
            await EnsurePhotoDiaryRequestsFixtureAsync(db, logger);

            // #1095 — a third client linked to qa.trainer with no conversation and no active
            // plan, so the inbox's NoMessages filter chip stays non-zero and clients.spec.ts's
            // strict-mode '1 active plan' locator stays unique to qa.client's seeded plan.
            await EnsureUserAsync(userManager, Client3UserId, Client3Email, "QA", "Client3", UserRole.Client, logger);
            var client3Profile = await EnsureClientProfileAsync(db, Client3UserId, Client3ProfilePublicId, logger);
            await EnsureTrainerClientLinkAsync(db, trainerProfile, client3Profile, logger);

            // #1095 — inbox conversation fixture: qa.trainer <-> qa.client, >=3 messages
            // including one unread client message and one coach message with a YouTube URL.
            await EnsureInboxConversationFixtureAsync(db, logger);

            // #1101 — a former client (ended link) with an unread conversation, so the inbox's
            // off-roster All-only rows have a fixture to exercise.
            await EnsureUserAsync(userManager, ClientFormerUserId, ClientFormerEmail, "QA", "Former", UserRole.Client, logger);
            var clientFormerProfile = await EnsureClientProfileAsync(db, ClientFormerUserId, ClientFormerProfilePublicId, logger);
            await EnsureTrainerClientLinkAsync(db, trainerProfile, clientFormerProfile, logger, isActive: false);
            await EnsureEndedLinkConversationFixtureAsync(db, logger);

            // A client with a live link that the ended-link e2e ends itself.
            await EnsureUserAsync(userManager, ClientEndingUserId, ClientEndingEmail, "QA", "Ending", UserRole.Client, logger);
            var clientEndingProfile = await EnsureClientProfileAsync(db, ClientEndingUserId, ClientEndingProfilePublicId, logger);
            await EnsureTrainerClientLinkAsync(db, trainerProfile, clientEndingProfile, logger);

            // #1095 — weekly check-in fixtures: one responded-unreviewed (NewCheckIns chip)
            // and one expired (MissingCheckIns chip), scoped to Training — the profession the
            // qa.trainer<->qa.client link grants.
            await EnsureInboxWeeklyCheckInsFixtureAsync(db, logger);

            // #1303 — client detail Overview data for qa.client. Runs after the nutrition plan
            // and training plan above because meal logs and the session execution reference them.
            await EnsureClientOverviewProfileAsync(db, clientProfile, logger);
            await EnsureBodyMeasurementsAsync(db, clientProfile, logger);
            await EnsureMealLogsAsync(mongo, logger);
            await EnsurePersonalRecordAsync(mongo, logger);
            await EnsureOverviewCheckInsAsync(db, logger);
            await EnsureMondaySessionExecutionAsync(mongo, logger);

            // #1311 — both-roles coach with its own client. qa.coach (Trainer + Nutritionist) is
            // linked to QA Duo only, by one link shaped like AcceptClientInviteEndpoint writes it
            // (role Nutritionist, both capability flags). It is NOT linked to qa.client: a client
            // has one active coach per profession. Plans are keyed on ApplicationUser ids.
            await EnsureUserAsync(userManager, CoachUserId, CoachEmail, "QA", "Coach",
                [UserRole.Trainer, UserRole.Nutritionist], logger);
            await EnsureUserAsync(userManager, ClientDuoUserId, ClientDuoEmail, "QA", "Duo", UserRole.Client, logger);
            var coachProfile = await EnsureProfessionalProfileAsync(db, CoachUserId, CoachProfilePublicId, logger);
            var clientDuoProfile = await EnsureClientProfileAsync(db, ClientDuoUserId, ClientDuoProfilePublicId, logger);
            await EnsureTrainerClientLinkAsync(db, coachProfile, clientDuoProfile, logger,
                professionalRole: UserRole.Nutritionist, canViewNutritionPlans: true, canViewTrainingPlans: true);

            await EnsureNutritionPlanAsync(mongo, ClientDuoUserId, CoachUserId, logger,
                QaDuoNutritionPlanExternalId, "QA Duo Nutrition Plan", DuoMealIdPrefix);
            await EnsureDuoTrainingPlanAsync(mongo, logger);

            await EnsureClientOverviewProfileAsync(db, clientDuoProfile, logger,
                heightCm: 175m, weightKg: 70.0m, targetWeightKg: 66m);
            await EnsureBodyMeasurementsAsync(db, clientDuoProfile, logger,
                QaDuoBodyMeasurementIds, [73.6m, 73.1m, 72.5m, 72.0m, 71.4m, 70.9m, 70.4m, 70.0m]);
            await EnsureMealLogsAsync(mongo, logger, ClientDuoUserId, QaDuoNutritionPlanExternalId,
                DuoMealIdPrefix, [3, 2, 3, 2, 2, 1]);
            await EnsureDuoSessionExecutionAsync(mongo, logger);
            await EnsureDuoPersonalRecordAsync(mongo, logger);
            await EnsureOverviewCheckInsAsync(db, logger, ClientDuoUserId, CoachUserId, QaDuoCheckInIds);

            // Over-cap clients of the cap coaches: the lapsed coach's plan already ended (read-only after
            // the last week), the gift coach's plan is still running.
            await EnsureCapClientPlanAsync(mongo, logger, LapsedGuidPrefix, "Lapsed", LapsedCoachUserId,
                LapsedClientUserId(freePlanCap + 1), freePlanCap + 1, weeksAgoStart: 4);
            await EnsureCapClientPlanAsync(mongo, logger, GiftGuidPrefix, "Gift", GiftCoachUserId,
                GiftClientUserId(freePlanCap + 1), freePlanCap + 1, weeksAgoStart: 0);

            // Image blobs in MinIO — idempotent, bucket created if absent.
            await EnsureAvatarAsync(sp, logger);
            await EnsureFoodImageAsync(sp, logger);
        }
        else
        {
            logger.LogInformation("QA seed kind=minimal — skipping training plan, foods, recipes, nutrition plan, and blobs.");
        }

        logger.LogInformation(
            "QA seed complete (kind={Kind}) — client={Client} trainer={Trainer} nutri={Nutri} client2={Client2} trainer2={Trainer2}",
            kind, ClientEmail, TrainerEmail, NutriEmail, Client2Email, Trainer2Email);
    }

    private static Task EnsureUserAsync(
        UserManager<ApplicationUser> userManager,
        Guid id,
        string email,
        string firstName,
        string lastName,
        UserRole role,
        ILogger logger) =>
        EnsureUserAsync(userManager, id, email, firstName, lastName, [role], logger);

    private static async Task EnsureUserAsync(
        UserManager<ApplicationUser> userManager,
        Guid id,
        string email,
        string firstName,
        string lastName,
        IReadOnlyCollection<UserRole> roles,
        ILogger logger)
    {
        var existing = await userManager.FindByEmailAsync(email);
        if (existing is not null)
        {
            logger.LogInformation("QA user already present: {Email}", email);
            return;
        }

        var user = new ApplicationUser
        {
            Id = id,
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            FirstName = firstName,
            LastName = lastName,
            GdprConsent = true,
        };

        var result = await userManager.CreateAsync(user, Password);
        if (!result.Succeeded)
        {
            var errors = string.Join("; ", result.Errors.Select(e => $"{e.Code}: {e.Description}"));
            throw new InvalidOperationException($"Failed to create QA user {email}: {errors}");
        }

        var roleNames = string.Join(", ", roles);
        var roleResult = await userManager.AddToRolesAsync(user, roles.Select(role => role.ToString()));
        if (!roleResult.Succeeded)
        {
            var errors = string.Join("; ", roleResult.Errors.Select(e => $"{e.Code}: {e.Description}"));
            throw new InvalidOperationException($"Failed to assign role {roleNames} to {email}: {errors}");
        }

        logger.LogInformation("QA user created: {Email} ({Role})", email, roleNames);
    }

    private static async Task<ClientProfile> EnsureClientProfileAsync(
        ApplicationDbContext db,
        Guid userId,
        Guid publicId,
        ILogger logger)
    {
        var existing = await db.ClientProfiles
            .FirstOrDefaultAsync(p => p.UserId == userId);

        if (existing is not null)
        {
            logger.LogInformation("QA ClientProfile already present for userId={UserId}", userId);
            return existing;
        }

        var profile = new ClientProfile
        {
            UserId = userId,
            PublicId = publicId,
            IsOnboardingComplete = true,
            DateCreated = DateTime.UtcNow,
        };

        db.ClientProfiles.Add(profile);
        await db.SaveChangesAsync();

        logger.LogInformation("QA ClientProfile created for userId={UserId} publicId={PublicId}", userId, publicId);
        return profile;
    }

    private static async Task<ProfessionalProfile> EnsureProfessionalProfileAsync(
        ApplicationDbContext db,
        Guid userId,
        Guid publicId,
        ILogger logger,
        bool online = false,
        bool showInSearch = false,
        bool putOnStudio = true)
    {
        var existing = await db.ProfessionalProfiles
            .FirstOrDefaultAsync(p => p.UserId == userId);

        if (existing is not null)
        {
            logger.LogInformation("QA ProfessionalProfile already present for userId={UserId}", userId);

            if (putOnStudio)
            {
                await EnsureStudioSubscriptionAsync(db, existing, logger);
            }

            return existing;
        }

        var profile = new ProfessionalProfile
        {
            UserId = userId,
            PublicId = publicId,
            ShowInSearch = showInSearch,
            AcceptNewClients = true,
            Online = online,
            DateCreated = DateTime.UtcNow,
        };

        db.ProfessionalProfiles.Add(profile);
        await db.SaveChangesAsync();
        await DefaultCheckInForm.EnsureAsync(db, userId, "en", CancellationToken.None);

        if (putOnStudio)
        {
            await EnsureStudioSubscriptionAsync(db, profile, logger);
        }

        logger.LogInformation("QA ProfessionalProfile created for userId={UserId} publicId={PublicId}", userId, publicId);
        return profile;
    }

    /// <summary>
    /// Puts a QA coach on the Studio plan (Active, its open price row) so every feature and an unlimited client
    /// count are available to the fixtures. A coach that already has a subscription row is left as it is.
    /// </summary>
    private static async Task EnsureStudioSubscriptionAsync(
        ApplicationDbContext db, ProfessionalProfile profile, ILogger logger)
    {
        if (await db.CoachSubscriptions.AnyAsync(cs => cs.ProfessionalProfileId == profile.Id))
        {
            return;
        }

        var studioPrice = await db.SubscriptionPlanPrices
            .AsNoTracking()
            .Where(p => p.SubscriptionPlan.Code == "studio" && p.ValidTo == null)
            .Select(p => new { p.Id, p.SubscriptionPlanId })
            .FirstOrDefaultAsync()
            ?? throw new InvalidOperationException(
                "The studio subscription plan must be seeded before the QA coaches (ApplicationDbContextSeed.SeedAsync).");

        db.CoachSubscriptions.Add(new CoachSubscription
        {
            ProfessionalProfileId = profile.Id,
            SubscriptionPlanId = studioPrice.SubscriptionPlanId,
            SubscriptionPlanPriceId = studioPrice.Id,
            Status = SubscriptionStatus.Active,
        });
        await db.SaveChangesAsync();

        logger.LogInformation("QA coach profileId={ProfileId} put on the studio plan", profile.Id);
    }

    /// <summary>
    /// Reads the client cap from the Free plan row. Fails fast when the plan is missing or unlimited,
    /// since the cap fixtures cannot be shaped without a number.
    /// </summary>
    private static async Task<int> ReadFreePlanCapAsync(ApplicationDbContext db)
    {
        var cap = await db.SubscriptionPlans
            .AsNoTracking()
            .Where(p => p.Code == SubscriptionPlanCodes.Free)
            .Select(p => new { p.MaxActiveClients })
            .FirstOrDefaultAsync()
            ?? throw new InvalidOperationException(
                "The free subscription plan must be seeded before the QA cap coaches (ApplicationDbContextSeed.SeedAsync).");

        return cap.MaxActiveClients
            ?? throw new InvalidOperationException("The free subscription plan has no client cap; the QA cap coaches need one.");
    }

    /// <summary>
    /// Creates one cap coach (user, profile off Studio, its own subscription row) and cap+1 new clients linked to it,
    /// each with a staggered <c>ActivatedAtUtc</c> so the kept and over-cap split is deterministic.
    /// </summary>
    private static async Task EnsureCapCoachAsync(
        UserManager<ApplicationUser> userManager,
        ApplicationDbContext db,
        ILogger logger,
        int cap,
        string guidPrefix,
        string label,
        Guid coachUserId,
        string coachEmail,
        Func<int, Guid> clientUserId,
        Func<int, string> clientEmail,
        string planCode,
        SubscriptionStatus status,
        DateTimeOffset? givenFreeAt,
        DateTimeOffset? freeUntil)
    {
        await EnsureUserAsync(userManager, coachUserId, coachEmail, "QA", $"{label}Coach", UserRole.Trainer, logger);
        var coachProfile = await EnsureProfessionalProfileAsync(
            db, coachUserId, CapFixtureGuid(guidPrefix, "bbbb", 1), logger, putOnStudio: false);
        await EnsureCapSubscriptionAsync(db, coachProfile, planCode, status, givenFreeAt, freeUntil, logger);

        var clientCount = cap + 1;
        var now = DateTime.UtcNow;

        for (var clientNumber = 1; clientNumber <= clientCount; clientNumber++)
        {
            await EnsureUserAsync(
                userManager, clientUserId(clientNumber), clientEmail(clientNumber), "QA", $"{label}Client{clientNumber}",
                UserRole.Client, logger);
            var clientProfile = await EnsureClientProfileAsync(
                db, clientUserId(clientNumber), CapFixtureGuid(guidPrefix, "aaaa", clientNumber), logger);
            await EnsureTrainerClientLinkAsync(
                db, coachProfile, clientProfile, logger,
                activatedAtUtc: now.AddDays(-7 * (clientCount - clientNumber + 1)));
        }
    }

    /// <summary>
    /// Writes a QA coach's subscription row on the named paid plan's open price row. A coach that already has a
    /// row is left as it is, so a re-seed never undoes what a QA flow changed.
    /// </summary>
    private static async Task EnsureCapSubscriptionAsync(
        ApplicationDbContext db,
        ProfessionalProfile profile,
        string planCode,
        SubscriptionStatus status,
        DateTimeOffset? givenFreeAt,
        DateTimeOffset? freeUntil,
        ILogger logger)
    {
        if (await db.CoachSubscriptions.AnyAsync(cs => cs.ProfessionalProfileId == profile.Id))
        {
            return;
        }

        var price = await db.SubscriptionPlanPrices
            .AsNoTracking()
            .Where(p => p.SubscriptionPlan.Code == planCode && p.ValidTo == null)
            .Select(p => new { p.Id, p.SubscriptionPlanId })
            .FirstOrDefaultAsync()
            ?? throw new InvalidOperationException(
                $"The '{planCode}' subscription plan must be seeded before the QA cap coaches (ApplicationDbContextSeed.SeedAsync).");

        db.CoachSubscriptions.Add(new CoachSubscription
        {
            ProfessionalProfileId = profile.Id,
            SubscriptionPlanId = price.SubscriptionPlanId,
            SubscriptionPlanPriceId = price.Id,
            Status = status,
            GivenFreeAt = givenFreeAt,
            FreeUntil = freeUntil,
        });
        await db.SaveChangesAsync();

        logger.LogInformation(
            "QA cap coach profileId={ProfileId} put on the {PlanCode} plan ({Status})", profile.Id, planCode, status);
    }

    /// <summary>
    /// One published week for a cap client, starting <paramref name="weeksAgoStart"/> Mondays ago. Four weeks ago
    /// the single week has already ended; zero keeps it running today.
    /// </summary>
    private static async Task EnsureCapClientPlanAsync(
        IMongoContext mongo,
        ILogger logger,
        string guidPrefix,
        string label,
        Guid coachUserId,
        Guid clientUserId,
        int clientNumber,
        int weeksAgoStart)
    {
        var planId = CapFixtureGuid(guidPrefix, "dddd", clientNumber);
        var existing = await mongo.TrainingPlans.Find(p => p.ExternalId == planId).FirstOrDefaultAsync();

        if (existing is not null)
        {
            logger.LogInformation("QA cap TrainingPlan already present: externalId={ExternalId}", planId);
            return;
        }

        var now = DateTime.UtcNow;
        var startDate = MondayOfWeek(now).AddDays(-7 * weeksAgoStart);

        var sessionsByDay = new Dictionary<int, List<TrainingSession>>
        {
            [1] =
            [
                new TrainingSession
                {
                    SessionId = CapFixtureGuid(guidPrefix, "eeee", clientNumber),
                    Name      = $"QA {label} Strength",
                    Order     = 1,
                    Workouts  = [],
                    StandaloneExercises =
                    [
                        new SessionExercise
                        {
                            ExerciseId         = CapFixtureGuid(guidPrefix, "ffff", clientNumber),
                            ExerciseExternalId = StandardExercise1Id,
                            ExerciseName       = "QA Squat",
                            Order              = 1,
                            MovementType       = MovementType.Reps,
                            Sets =
                            [
                                new ExerciseSet { SetNumber = 1, Type = SetType.Normal, Reps = 8, WeightKg = 60m },
                            ],
                        },
                    ],
                },
            ],
        };

        await mongo.TrainingPlans.InsertOneAsync(new TrainingPlan
        {
            ExternalId  = planId,
            ClientId    = clientUserId,
            TrainerId   = coachUserId,
            Name        = $"QA {label} Over-cap Plan",
            Status      = TrainingPlanStatus.Active,
            StartDate   = startDate,
            DateCreated = now,
            Version     = 1,
            Weeks =
            [
                new TrainingWeek
                {
                    WeekNumber    = 1,
                    Status        = WeekStatus.Published,
                    DatePublished = startDate,
                    Days          = BuildTrainingDays(sessionsByDay),
                },
            ],
        });

        logger.LogInformation("QA cap TrainingPlan created: externalId={ExternalId}", planId);
    }

    private static async Task EnsureTrainerClientLinkAsync(
        ApplicationDbContext db,
        ProfessionalProfile trainerProfile,
        ClientProfile clientProfile,
        ILogger logger,
        bool isActive = true,
        UserRole professionalRole = UserRole.Trainer,
        bool canViewNutritionPlans = false,
        bool canViewTrainingPlans = true,
        DateTime? activatedAtUtc = null)
    {
        var existing = await db.ClientProfessionalLinks
            .AnyAsync(l =>
                l.ProfessionalProfileId == trainerProfile.Id &&
                l.ClientProfileId == clientProfile.Id);

        if (existing)
        {
            logger.LogInformation(
                "QA trainer↔client link already present: trainerId={TrainerId} clientId={ClientId}",
                trainerProfile.Id, clientProfile.Id);
            return;
        }

        var link = new ClientProfessionalLink
        {
            ProfessionalProfileId = trainerProfile.Id,
            ClientProfileId = clientProfile.Id,
            ProfessionalRole = professionalRole,
            ConnectionState = isActive ? ConnectionState.Connected : ConnectionState.Ended,
            CanViewTrainingPlans = canViewTrainingPlans,
            CanViewNutritionPlans = canViewNutritionPlans,
            DateCreated = DateTime.UtcNow,
            ActivatedAtUtc = activatedAtUtc,
        };

        db.ClientProfessionalLinks.Add(link);
        await db.SaveChangesAsync();

        logger.LogInformation(
            "QA trainer↔client link created: trainerId={TrainerId} clientId={ClientId}",
            trainerProfile.Id, clientProfile.Id);
    }

    /// <summary>
    /// Seeds a deterministic training plan for the QA client.
    ///
    /// The plan contains one Published week with one session with four sections:
    ///   1. ForTime, TimeCapSeconds=1800, Exercises=[] — the #258 bug shape.
    ///   2. AMRAP, TimeCapSeconds=600, two synthetic exercises — non-regression.
    ///   3. Standard (null format), two synthetic exercises — non-regression.
    ///   4. Tabata, WorkSeconds=20, RestSeconds=10, TotalRounds=8, one exercise — #327 iOS QA fixture.
    ///
    /// ClientId = clientUserId (ApplicationUser.Id, #840) — GetClientPlansEndpoint
    /// filters TrainingPlan.ClientId by the same identifier since the #840 migration.
    ///
    /// The week Status must be WeekStatus.Published — GetClientPlansEndpoint line 142
    /// applies ElemMatch(w => w.Status == WeekStatus.Published). A Draft week silently
    /// excludes the plan.
    /// </summary>
    /// <summary>
    /// Materialises 7 <see cref="TrainingDay"/> entries (Monday..Sunday) for a
    /// <see cref="TrainingWeek"/> from a sparse day-of-week -> sessions map, mirroring
    /// <c>CreateTrainingPlanEndpoint</c>'s "always 7 days" invariant (#857 phase 2). Days
    /// absent from <paramref name="sessionsByDay"/> get an empty session list (a rest day).
    /// </summary>
    private static List<TrainingDay> BuildTrainingDays(IReadOnlyDictionary<int, List<TrainingSession>> sessionsByDay) =>
        Enumerable.Range(1, 7)
            .Select(dayOfWeek => new TrainingDay
            {
                DayOfWeek = dayOfWeek,
                Sessions = sessionsByDay.TryGetValue(dayOfWeek, out var sessions) ? sessions : []
            })
            .ToList();

    private static async Task EnsureTrainingPlanAsync(
        IMongoContext mongo,
        Guid clientUserId,
        Guid trainerProfilePublicId,
        ILogger logger)
    {
        var existing = await mongo.TrainingPlans
            .Find(p => p.ExternalId == QaTrainingPlanExternalId)
            .FirstOrDefaultAsync();

        if (existing is not null)
        {
            logger.LogInformation(
                "QA TrainingPlan already present: externalId={ExternalId}", QaTrainingPlanExternalId);
            return;
        }

        var now = DateTime.UtcNow;

        // #898 — anchor StartDate to the Monday of the current (seed-time) week so
        // PlanWindowResolver.ResolveCurrentPlan's [StartDate, StartDate + weeks*7) window
        // covers today. Same anchoring idiom as EnsurePastTrainingPlanAsync's lastMonday,
        // minus the -28 day offset (this plan's window is the CURRENT week, not a past one).
        //
        // This plan cannot rely on ResolveCurrentPlan's legacy single-plan fallback (an
        // unranged plan is only auto-selected when it is the client's ONLY same-type plan):
        // EnsurePastTrainingPlanAsync seeds a second, ranged TrainingPlan for the same
        // client, so the moment both exist this plan MUST carry its own StartDate to ever
        // be selected — do not "fix" a resolver miss here by deleting the past-plan fixture.
        //
        // A 1-week plan means a 7-day window: this anchor is only valid for 7 days after
        // seeding. POST /test/reset drops the Mongo collections and re-seeds (re-anchoring
        // to the new current Monday), so exposure is limited to a harness left running for
        // more than a week without a reset.
        var daysUntilMonday = ((int)now.DayOfWeek == 0 ? 7 : (int)now.DayOfWeek) - 1;
        var startDate = now.Date.AddDays(-daysUntilMonday);

        var plan = new TrainingPlan
        {
            ExternalId      = QaTrainingPlanExternalId,
            ClientId        = clientUserId,
            // TrainerId is keyed on ApplicationUser.Id (NOT ProfessionalProfile.PublicId) —
            // GetTrainingPlansEndpoint and GetTrainingPlanEndpoint scope by
            // Guid.Parse(User.FindFirstValue(AppClaims.UserId)) which is ApplicationUser.Id.
            // Using trainerProfilePublicId (bbbb...) makes this plan invisible to
            // GET /training/plans and GET /training/plans/{planId} for the trainer.
            TrainerId       = TrainerUserId,
            Name            = "QA Test Plan — ForTime fixture",
            Status          = TrainingPlanStatus.Active,
            StartDate       = startDate,
            DateCreated     = now,
            Version         = 1,
            Weeks =
            [
                new TrainingWeek
                {
                    WeekNumber    = 1,
                    Status        = WeekStatus.Published,
                    DatePublished = now,
                    Days = Enumerable.Range(1, 7).Select(dayOfWeek => new TrainingDay
                    {
                        DayOfWeek = dayOfWeek,
                        Sessions = dayOfWeek switch
                        {
                            1 =>
                        [
                            new TrainingSession
                            {
                                SessionId  = QaSessionId,
                                Name       = "QA Session",
                                Order      = 1,
                                Workouts =
                                [
                                // Section 1 — ForTime + 0 exercises (#258 bug shape)
                                new TrainingWorkout
                                {
                                    WorkoutId    = ForTimeSectionId,
                                    Order        = 0,
                                    Name         = "ForTime 30min",
                                    Format       = WorkoutFormat.ForTime,
                                    FormatConfig = new WodConfig { TimeCapSeconds = 1800 },
                                    Exercises    = [],
                                },
                                // Section 2 — AMRAP + 2 synthetic exercises (non-regression)
                                new TrainingWorkout
                                {
                                    WorkoutId    = AmrapSectionId,
                                    Order        = 1,
                                    Name         = "AMRAP test",
                                    Format       = WorkoutFormat.AMRAP,
                                    FormatConfig = new WodConfig { TimeCapSeconds = 600 },
                                    Exercises =
                                    [
                                        new SessionExercise
                                        {
                                            ExerciseId         = AmrapExercise1InstanceId,
                                            ExerciseExternalId = AmrapExercise1Id,
                                            ExerciseName       = "QA Pull-up",
                                            Order              = 1,
                                            MovementType       = MovementType.Reps,
                                        },
                                        new SessionExercise
                                        {
                                            ExerciseId         = AmrapExercise2InstanceId,
                                            ExerciseExternalId = AmrapExercise2Id,
                                            ExerciseName       = "QA Box Jump",
                                            Order              = 2,
                                            MovementType       = MovementType.Reps,
                                        },
                                    ],
                                },
                                // Section 3 — Standard (null format) + 2 synthetic exercises with prescribed sets.
                                // Sets are populated so the planned-vs-actual WorkoutLog (#457) can exercise
                                // all four UI cases: modified, as-prescribed, skipped, extra.
                                new TrainingWorkout
                                {
                                    WorkoutId    = StandardSectionId,
                                    Order        = 2,
                                    Name         = "Standard test",
                                    Format       = null,
                                    FormatConfig = null,
                                    Exercises =
                                    [
                                        new SessionExercise
                                        {
                                            ExerciseId         = StandardExercise1InstanceId,
                                            ExerciseExternalId = StandardExercise1Id,
                                            ExerciseName       = "QA Squat",
                                            Order              = 1,
                                            MovementType       = MovementType.Reps,
                                            // Prescribed: 2 sets × 10 reps @ 80 kg.
                                            Sets =
                                            [
                                                new ExerciseSet { SetNumber = 1, Type = SetType.Normal, Reps = 10, WeightKg = 80m },
                                                new ExerciseSet { SetNumber = 2, Type = SetType.Normal, Reps = 10, WeightKg = 80m },
                                            ],
                                        },
                                        new SessionExercise
                                        {
                                            ExerciseId         = StandardExercise2InstanceId,
                                            ExerciseExternalId = StandardExercise2Id,
                                            ExerciseName       = "QA Deadlift",
                                            Order              = 2,
                                            MovementType       = MovementType.Reps,
                                            // Prescribed: 2 sets × 5 reps @ 100 kg.
                                            Sets =
                                            [
                                                new ExerciseSet { SetNumber = 1, Type = SetType.Normal, Reps = 5, WeightKg = 100m },
                                                new ExerciseSet { SetNumber = 2, Type = SetType.Normal, Reps = 5, WeightKg = 100m },
                                            ],
                                        },
                                    ],
                                },
                                // Section 4 — Tabata 20s/10s × 8 + 1 exercise (#327 iOS QA fixture)
                                new TrainingWorkout
                                {
                                    WorkoutId    = TabataSectionId,
                                    Order        = 3,
                                    Name         = "Tabata test",
                                    Format       = WorkoutFormat.Tabata,
                                    FormatConfig = new WodConfig
                                    {
                                        WorkSeconds  = 20,
                                        RestSeconds  = 10,
                                        TotalRounds  = 8,
                                    },
                                    Exercises =
                                    [
                                        new SessionExercise
                                        {
                                            ExerciseId         = TabataExercise1InstanceId,
                                            ExerciseExternalId = TabataExercise1Id,
                                            ExerciseName       = "QA Burpee",
                                            Order              = 1,
                                            MovementType       = MovementType.Reps,
                                        },
                                    ],
                                },
                            ],
                            },
                        ],
                            // #857 QA fixture — a session with ONLY standalone exercises (no
                            // workouts at all), exercising the tree restructure's new
                            // "session with only standalone exercises" shape.
                            2 =>
                        [
                            new TrainingSession
                            {
                                SessionId = QaStandaloneOnlySessionId,
                                Name      = "QA Standalone-Only Session",
                                Order     = 1,
                                Workouts  = [],
                                StandaloneExercises =
                                [
                                    new SessionExercise
                                    {
                                        ExerciseId         = QaStandaloneOnlyInstanceId,
                                        ExerciseExternalId = QaStandaloneOnlyExerciseId,
                                        ExerciseName       = "QA Plank",
                                        Order              = 1,
                                        MovementType       = MovementType.Reps,
                                    },
                                ],
                            },
                        ],
                            // #857 QA fixture — the SAME catalog exercise appears BOTH standalone
                            // on the session AND nested inside one of the session's workouts, with
                            // distinct ExerciseId instance values. Unreachable pre-#857; exercises
                            // completion-path coverage for exactly this pairing.
                            3 =>
                        [
                            new TrainingSession
                            {
                                SessionId = QaDualPlacementSessionId,
                                Name      = "QA Standalone + Nested Session",
                                Order     = 1,
                                Workouts  =
                                [
                                    new TrainingWorkout
                                    {
                                        WorkoutId    = QaDualPlacementWorkoutId,
                                        Order        = 0,
                                        Name         = "Main workout",
                                        Format       = null,
                                        FormatConfig = null,
                                        Exercises =
                                        [
                                            new SessionExercise
                                            {
                                                ExerciseId         = QaDualPlacementNestedInstanceId,
                                                ExerciseExternalId = QaDualPlacementExerciseId,
                                                ExerciseName       = "QA Wall Ball",
                                                Order              = 1,
                                                MovementType       = MovementType.Reps,
                                                // #879 — prescribed sets so per-instance completion
                                                // (via EnsureDualPlacementCompletionAsync) is observable.
                                                Sets =
                                                [
                                                    new ExerciseSet { SetNumber = 1, Type = SetType.Normal, Reps = 15, WeightKg = 9m },
                                                    new ExerciseSet { SetNumber = 2, Type = SetType.Normal, Reps = 15, WeightKg = 9m },
                                                ],
                                            },
                                        ],
                                    },
                                ],
                                StandaloneExercises =
                                [
                                    new SessionExercise
                                    {
                                        ExerciseId         = QaDualPlacementStandaloneInstanceId,
                                        ExerciseExternalId = QaDualPlacementExerciseId,
                                        ExerciseName       = "QA Wall Ball",
                                        Order              = 1,
                                        MovementType       = MovementType.Reps,
                                        // #879 — prescribed sets so per-instance completion
                                        // (via EnsureDualPlacementCompletionAsync) is observable.
                                        Sets =
                                        [
                                            new ExerciseSet { SetNumber = 1, Type = SetType.Normal, Reps = 15, WeightKg = 9m },
                                            new ExerciseSet { SetNumber = 2, Type = SetType.Normal, Reps = 15, WeightKg = 9m },
                                        ],
                                    },
                                ],
                            },
                        ],
                            _ => [],
                        }
                    }).ToList(),
                },
            ],
        };

        await mongo.TrainingPlans.InsertOneAsync(plan);

        logger.LogInformation(
            "QA TrainingPlan created: externalId={ExternalId} clientId={ClientId}",
            QaTrainingPlanExternalId, clientUserId);
    }

    /// <summary>
    /// #588 — inserts Exercise catalog docs (mongo.Exercises) matching every synthetic
    /// SessionExercise id the QA training-plan fixtures reference (AMRAP/Standard/Tabata
    /// section exercises, the multi-section shared exercise, and the past-dated plan's
    /// six exercises). Without this, GET /exercises/{id} 404s for every one of these ids
    /// because QaSeedRunner previously only ever referenced them from SessionExercise
    /// entries — it never inserted a matching Exercise document, so the ids existed
    /// nowhere in the catalog collection. Idempotent: skips entirely if the first id is
    /// already present (all twelve are always inserted together).
    /// </summary>
    private static async Task EnsureExercisesAsync(IMongoContext mongo, ILogger logger)
    {
        var existing = await mongo.Exercises
            .Find(e => e.ExternalId == AmrapExercise1Id)
            .FirstOrDefaultAsync();

        if (existing is not null)
        {
            logger.LogInformation("QA Exercise catalog docs already present.");
            return;
        }

        var now = DateTime.UtcNow;

        Exercise Build(Guid externalId, string name, MuscleGroup muscleGroup, ExerciseEquipment equipment) => new()
        {
            ExternalId = externalId,
            Name = name,
            MuscleGroups = [muscleGroup],
            Equipment = equipment,
            Category = ExerciseCategory.Strength,
            Difficulty = ExerciseDifficulty.Intermediate,
            IsCustom = false,
            IsActive = true,
            Source = "system",
            DateCreated = now,
        };

        var exercises = new List<Exercise>
        {
            Build(AmrapExercise1Id, "QA Pull-up", MuscleGroup.Back, ExerciseEquipment.Bodyweight),
            Build(AmrapExercise2Id, "QA Box Jump", MuscleGroup.Quadriceps, ExerciseEquipment.Bodyweight),
            Build(StandardExercise1Id, "QA Squat", MuscleGroup.Quadriceps, ExerciseEquipment.Barbell),
            Build(StandardExercise2Id, "QA Deadlift", MuscleGroup.Hamstrings, ExerciseEquipment.Barbell),
            Build(TabataExercise1Id, "QA Burpee", MuscleGroup.Chest, ExerciseEquipment.Bodyweight),
            Build(SharedExerciseId, "QA Kettlebell Swing", MuscleGroup.Glutes, ExerciseEquipment.Kettlebell),
            Build(PastBenchPressExerciseId, "QA Bench Press", MuscleGroup.Chest, ExerciseEquipment.Barbell),
            Build(PastOverheadPressExerciseId, "QA Overhead Press", MuscleGroup.Shoulders, ExerciseEquipment.Barbell),
            Build(PastBackSquatExerciseId, "QA Back Squat", MuscleGroup.Quadriceps, ExerciseEquipment.Barbell),
            Build(PastRomanianDeadliftExerciseId, "QA Romanian Deadlift", MuscleGroup.Hamstrings, ExerciseEquipment.Barbell),
            Build(PastPulldownExerciseId, "QA Pull-down", MuscleGroup.Back, ExerciseEquipment.Machine),
            Build(PastSeatedRowExerciseId, "QA Seated Row", MuscleGroup.Back, ExerciseEquipment.Machine),
            // #857 QA fixture — standalone-only and standalone+nested session shapes.
            Build(QaStandaloneOnlyExerciseId, "QA Plank", MuscleGroup.Abs, ExerciseEquipment.Bodyweight),
            Build(QaDualPlacementExerciseId, "QA Wall Ball", MuscleGroup.FullBody, ExerciseEquipment.None),
        };

        await mongo.Exercises.InsertManyAsync(exercises);

        logger.LogInformation("QA Exercise catalog docs created: count={Count}", exercises.Count);
    }

    /// <summary>
    /// Seeds a completed SessionExecution against the main QA training plan (dddddddd-...)
    /// Standard section, exercising all four planned-vs-actual set cases in one session:
    ///
    ///   Exercise 1 (QA Squat):
    ///     Set 1 — MODIFIED    PlannedReps=10, PlannedWeightKg=80, actual Reps=8,  WeightKg=85  → IsModified=true.
    ///     Set 2 — AS-PRESCRIBED PlannedReps=10, PlannedWeightKg=80, actual Reps=10, WeightKg=80 → IsModified=false.
    ///
    ///   Exercise 2 (QA Deadlift):
    ///     Set 1 — SKIPPED     PlannedReps=5, PlannedWeightKg=100, Reps=null, WeightKg=null     → planned set, no actual.
    ///     Set 2 — EXTRA       PlannedReps=null (no snapshot), actual Reps=6, WeightKg=90       → no planned snapshot.
    ///
    /// ClientId = ClientUserId (ApplicationUser.Id) — SessionExecution ownership mirrors
    /// CompleteWorkoutEndpoint's filter on AppClaims.UserId.
    /// Gated to the Rich seed path only; never created for the Minimal kind.
    /// </summary>
    private static async Task EnsureMainPlanExecutionAsync(
        IMongoContext mongo,
        ILogger logger)
    {
        var existing = await mongo.SessionExecutions
            .Find(l => l.ExternalId == QaMainPlanCompletedExecutionId)
            .FirstOrDefaultAsync();

        if (existing is not null)
        {
            logger.LogInformation(
                "QA MainPlan SessionExecution already present: externalId={ExternalId}", QaMainPlanCompletedExecutionId);
            return;
        }

        var completedAt = DateTime.UtcNow.Date.AddDays(-3).AddHours(11); // 11:00 UTC, 3 days ago.
        var log = new SessionExecution
        {
            ExternalId  = QaMainPlanCompletedExecutionId,
            // ClientId = ApplicationUser.Id — CompleteWorkoutEndpoint scopes SessionExecutions by
            // Guid.Parse(AppClaims.UserId) which is ApplicationUser.Id, NOT ClientProfile.PublicId.
            ClientId      = ClientUserId,
            PlanId        = QaTrainingPlanExternalId,
            SessionId     = QaSessionId,
            Date          = SessionExecution.ToCompletionDateUtc(completedAt),
            Status        = SessionExecutionStatus.Completed,
            DateCreated   = completedAt.AddMinutes(-60),
            DateUpdated   = completedAt,
            Performance = new SessionExecutionPerformance
            {
                StartedAt   = completedAt.AddMinutes(-60),
                CompletedAt = completedAt,
                Workouts =
                [
                    new LoggedWorkout
                    {
                        WorkoutId = MainPlanCompletedWorkoutId,
                        Order     = 2,    // mirrors Standard section Order=2 in the plan
                        Name      = "Standard test",
                        Format    = null,
                        Exercises =
                        [
                            // Exercise 1: QA Squat — Set 1 modified, Set 2 as-prescribed.
                            new WorkoutExercise
                            {
                                ExerciseExternalId = StandardExercise1Id,
                                ExerciseName       = "QA Squat",
                                Sets =
                                [
                                    // MODIFIED — actual differs from planned.
                                    new WorkoutSet
                                    {
                                        SetNumber       = 1,
                                        Reps            = 8,          // actual: fewer reps
                                        WeightKg        = 85m,         // actual: heavier weight
                                        PlannedReps     = 10,          // snapshot from plan prescription
                                        PlannedWeightKg = 80m,
                                        CompletedAt     = completedAt.AddMinutes(-50),
                                    },
                                    // AS-PRESCRIBED — actual matches planned exactly.
                                    new WorkoutSet
                                    {
                                        SetNumber       = 2,
                                        Reps            = 10,
                                        WeightKg        = 80m,
                                        PlannedReps     = 10,
                                        PlannedWeightKg = 80m,
                                        CompletedAt     = completedAt.AddMinutes(-40),
                                    },
                                ],
                            },
                            // Exercise 2: QA Deadlift — Set 1 skipped (planned present, no actual),
                            //                           Set 2 extra (actual present, no planned snapshot).
                            new WorkoutExercise
                            {
                                ExerciseExternalId = StandardExercise2Id,
                                ExerciseName       = "QA Deadlift",
                                Sets =
                                [
                                    // SKIPPED — planned prescription captured, client did not perform the set.
                                    // Reps/WeightKg are null; PlannedReps/PlannedWeightKg are set.
                                    new WorkoutSet
                                    {
                                        SetNumber       = 1,
                                        Reps            = null,
                                        WeightKg        = null,
                                        PlannedReps     = 5,
                                        PlannedWeightKg = 100m,
                                        CompletedAt     = null,
                                    },
                                    // EXTRA — client logged an additional set beyond what was prescribed.
                                    // No planned snapshot (PlannedReps/PlannedWeightKg remain null).
                                    new WorkoutSet
                                    {
                                        SetNumber   = 2,
                                        Reps        = 6,
                                        WeightKg    = 90m,
                                        CompletedAt = completedAt.AddMinutes(-20),
                                    },
                                ],
                            },
                        ],
                    },
                ],
            },
        };

        await mongo.SessionExecutions.InsertOneAsync(log);
        logger.LogInformation(
            "QA MainPlan SessionExecution created: externalId={ExternalId} planId={PlanId} sessionId={SessionId}",
            QaMainPlanCompletedExecutionId, QaTrainingPlanExternalId, QaSessionId);
    }

    /// <summary>
    /// Seeds a past-dated training plan for the QA client with three sessions that
    /// represent the three past-session states the web UI classifies:
    ///
    ///   PAST-COMPLETED  (Week 1, Mon) — WorkoutLog exists, IsCompleted=true  → read-only.
    ///   PAST-SKIPPED    (Week 1, Wed) — WorkoutLog exists, IsCompleted=false → editable.
    ///   PAST-UNTOUCHED  (Week 2, Mon) — no WorkoutLog at all               → editable.
    ///
    /// StartDate is set to 28 days before seed-time anchored to the preceding Monday
    /// so all Week 1 + Week 2 sessions are firmly in the past regardless of current day.
    ///
    /// Each session carries a single Standard section with two exercises so the web
    /// can render the full section/exercise/set tree.  The WorkoutLog for COMPLETED
    /// mirrors the section structure with all sets stamped CompletedAt.  The WorkoutLog
    /// for SKIPPED has one set completed on each exercise, the rest absent (incomplete).
    /// </summary>
    private static async Task EnsurePastTrainingPlanAsync(
        IMongoContext mongo,
        Guid clientUserId,
        Guid trainerProfilePublicId,
        ILogger logger)
    {
        var existingPlan = await mongo.TrainingPlans
            .Find(p => p.ExternalId == QaPastTrainingPlanExternalId)
            .FirstOrDefaultAsync();

        // Anchor StartDate to the Monday that is (at least) 28 days in the past.
        // Using Monday ensures DayOfWeek=1 maps cleanly to that exact Monday.
        var now = DateTime.UtcNow;
        var daysUntilLastMonday = ((int)now.DayOfWeek == 0 ? 7 : (int)now.DayOfWeek) - 1;
        var lastMonday = now.Date.AddDays(-daysUntilLastMonday);
        // Go back 4 full weeks so Week 1 Mon = lastMonday-28 days.
        var startDate = lastMonday.AddDays(-28);

        // Stable dates derived deterministically from startDate.
        var completedSessionDate = startDate;                    // Week 1, Mon (+0d)
        var skippedSessionDate   = startDate.AddDays(2);        // Week 1, Wed (+2d)
        var untouchedSessionDate = startDate.AddDays(7);        // Week 2, Mon (+7d)

        if (existingPlan is null)
        {
            // Session 1 — will have a completed WorkoutLog. Scheduled Monday.
            var pastSessionCompleted = new TrainingSession
            {
                SessionId = QaPastSessionCompletedId,
                Name      = "QA Past Session — Completed",
                Order     = 1,
                Workouts  =
                [
                    new TrainingWorkout
                    {
                        WorkoutId    = PastCompletedWorkoutId,
                        Order        = 0,
                        Name         = "Hlavní",
                        Format       = null,
                        FormatConfig = null,
                        Exercises    =
                        [
                            new SessionExercise
                            {
                                ExerciseId         = PastBenchPressInstanceId,
                                ExerciseExternalId = PastBenchPressExerciseId,
                                ExerciseName       = "QA Bench Press",
                                Order              = 1,
                                MovementType       = MovementType.Reps,
                            },
                            new SessionExercise
                            {
                                ExerciseId         = PastOverheadPressInstanceId,
                                ExerciseExternalId = PastOverheadPressExerciseId,
                                ExerciseName       = "QA Overhead Press",
                                Order              = 2,
                                MovementType       = MovementType.Reps,
                            },
                        ],
                    },
                ],
            };

            // Session 2 — will have an incomplete (skipped) WorkoutLog. Scheduled Wednesday.
            var pastSessionSkipped = new TrainingSession
            {
                SessionId = QaPastSessionSkippedId,
                Name      = "QA Past Session — Skipped",
                Order     = 2,
                Workouts  =
                [
                    new TrainingWorkout
                    {
                        WorkoutId    = PastSkippedWorkoutId,
                        Order        = 0,
                        Name         = "Hlavní",
                        Format       = null,
                        FormatConfig = null,
                        Exercises    =
                        [
                            new SessionExercise
                            {
                                ExerciseId         = PastBackSquatInstanceId,
                                ExerciseExternalId = PastBackSquatExerciseId,
                                ExerciseName       = "QA Back Squat",
                                Order              = 1,
                                MovementType       = MovementType.Reps,
                            },
                            new SessionExercise
                            {
                                ExerciseId         = PastRomanianDeadliftInstanceId,
                                ExerciseExternalId = PastRomanianDeadliftExerciseId,
                                ExerciseName       = "QA Romanian Deadlift",
                                Order              = 2,
                                MovementType       = MovementType.Reps,
                            },
                        ],
                    },
                ],
            };

            // Session 3 — NO WorkoutLog (untouched). Scheduled Monday, week 2.
            var pastSessionUntouched = new TrainingSession
            {
                SessionId = QaPastSessionUntouchedId,
                Name      = "QA Past Session — Untouched",
                Order     = 1,
                Workouts  =
                [
                    new TrainingWorkout
                    {
                        WorkoutId    = PastUntouchedWorkoutId,
                        Order        = 0,
                        Name         = "Hlavní",
                        Format       = null,
                        FormatConfig = null,
                        Exercises    =
                        [
                            new SessionExercise
                            {
                                ExerciseId         = PastPulldownInstanceId,
                                ExerciseExternalId = PastPulldownExerciseId,
                                ExerciseName       = "QA Pull-down",
                                Order              = 1,
                                MovementType       = MovementType.Reps,
                            },
                            new SessionExercise
                            {
                                ExerciseId         = PastSeatedRowInstanceId,
                                ExerciseExternalId = PastSeatedRowExerciseId,
                                ExerciseName       = "QA Seated Row",
                                Order              = 2,
                                MovementType       = MovementType.Reps,
                            },
                        ],
                    },
                ],
            };

            var plan = new TrainingPlan
            {
                ExternalId    = QaPastTrainingPlanExternalId,
                // ClientId is keyed on ApplicationUser.Id (NOT ClientProfile.PublicId) since
                // #840 — GetTrainingPlansEndpoint and TrainingCompletion (written by
                // WorkoutCompletionService) are both keyed on the same ApplicationUser.Id.
                // plan.ClientId and TrainingCompletion.ClientId must match for the completions
                // fold-in in GetTrainingPlanEndpoint (line 67 filters by plan.ClientId).
                ClientId      = clientUserId,
                // TrainerId is keyed on ApplicationUser.Id (NOT ProfessionalProfile.PublicId) —
                // GetTrainingPlansEndpoint and GetTrainingPlanEndpoint scope by
                // Guid.Parse(User.FindFirstValue(AppClaims.UserId)) which is ApplicationUser.Id.
                // Using the profile PublicId (bbbb...) makes this plan invisible to GET /training/plans.
                TrainerId     = TrainerUserId,
                Name          = "QA Past Plan — #326 completion states",
                Status        = TrainingPlanStatus.Active,
                StartDate     = startDate,
                DateCreated   = startDate.AddDays(-3),
                Version       = 1,
                Weeks =
                [
                    new TrainingWeek
                    {
                        WeekNumber    = 1,
                        Status        = WeekStatus.Published,
                        DatePublished = startDate.AddDays(-1),
                        Days = BuildTrainingDays(new Dictionary<int, List<TrainingSession>>
                        {
                            [1] = [pastSessionCompleted], // Monday
                            [3] = [pastSessionSkipped],    // Wednesday
                        }),
                    },
                    new TrainingWeek
                    {
                        WeekNumber    = 2,
                        Status        = WeekStatus.Published,
                        DatePublished = startDate.AddDays(6),
                        Days = BuildTrainingDays(new Dictionary<int, List<TrainingSession>>
                        {
                            [1] = [pastSessionUntouched], // Monday
                        }),
                    },
                ],
            };

            await mongo.TrainingPlans.InsertOneAsync(plan);
            logger.LogInformation(
                "QA PastTrainingPlan created: externalId={ExternalId} startDate={StartDate}",
                QaPastTrainingPlanExternalId, startDate);
        }
        else
        {
            logger.LogInformation(
                "QA PastTrainingPlan already present: externalId={ExternalId}", QaPastTrainingPlanExternalId);
        }

        // ---------------------------------------------------------------------------
        // SessionExecution: COMPLETED — Status=Completed, all sets stamped CompletedAt.
        // ---------------------------------------------------------------------------
        var existingCompletedLog = await mongo.SessionExecutions
            .Find(l => l.ExternalId == QaPastCompletedExecutionId)
            .FirstOrDefaultAsync();

        if (existingCompletedLog is null)
        {
            var completedAt = completedSessionDate.AddHours(10); // 10:00 UTC on session day.
            var completedLog = new SessionExecution
            {
                ExternalId  = QaPastCompletedExecutionId,
                // ClientId is keyed on ApplicationUser.Id (NOT ClientProfile.PublicId) —
                // CompleteWorkoutEndpoint (live client finish) filters SessionExecutions by
                // ClientId == Guid.Parse(AppClaims.UserId), which is ApplicationUser.Id.
                // Post-#840, WorkoutCompletionService no longer resolves a ClientProfile at
                // all — it writes the completion flags straight onto the SAME document, so
                // this same ApplicationUser.Id (ClientUserId) is used throughout.
                ClientId      = ClientUserId,
                PlanId        = QaPastTrainingPlanExternalId,
                SessionId     = QaPastSessionCompletedId,
                // Date is the calendar-day key required by the unified partial unique index
                // idx_sessionexecution_clientId_sessionId_date_unique. Derived via the shared
                // SessionExecution.ToCompletionDateUtc helper so the key always agrees with
                // WorkoutCompletionService and MongoIndexInitializer.
                Date          = SessionExecution.ToCompletionDateUtc(completedAt),
                Status        = SessionExecutionStatus.Completed,
                DateCreated   = completedAt.AddMinutes(-45),
                DateUpdated   = completedAt,
                Performance = new SessionExecutionPerformance
                {
                    StartedAt   = completedAt.AddMinutes(-45),
                    CompletedAt = completedAt,
                    Workouts    =
                    [
                        new LoggedWorkout
                        {
                            WorkoutId = PastCompletedWorkoutId,
                            Order     = 0,
                            Name      = "Hlavní",
                            Format    = null,
                            Exercises =
                            [
                                new WorkoutExercise
                                {
                                    ExerciseExternalId = PastBenchPressExerciseId,
                                    ExerciseName       = "QA Bench Press",
                                    Sets               =
                                    [
                                        new WorkoutSet { SetNumber = 1, Reps = 8, WeightKg = 80m, CompletedAt = completedAt.AddMinutes(-30) },
                                        new WorkoutSet { SetNumber = 2, Reps = 8, WeightKg = 80m, CompletedAt = completedAt.AddMinutes(-25) },
                                        new WorkoutSet { SetNumber = 3, Reps = 7, WeightKg = 80m, CompletedAt = completedAt.AddMinutes(-20) },
                                    ],
                                },
                                new WorkoutExercise
                                {
                                    ExerciseExternalId = PastOverheadPressExerciseId,
                                    ExerciseName       = "QA Overhead Press",
                                    Sets               =
                                    [
                                        new WorkoutSet { SetNumber = 1, Reps = 10, WeightKg = 50m, CompletedAt = completedAt.AddMinutes(-15) },
                                        new WorkoutSet { SetNumber = 2, Reps = 10, WeightKg = 50m, CompletedAt = completedAt.AddMinutes(-10) },
                                    ],
                                },
                            ],
                        },
                    ],
                },
            };

            await mongo.SessionExecutions.InsertOneAsync(completedLog);
            logger.LogInformation(
                "QA SessionExecution COMPLETED created: externalId={ExternalId} sessionId={SessionId}",
                QaPastCompletedExecutionId, QaPastSessionCompletedId);
        }
        else
        {
            logger.LogInformation(
                "QA SessionExecution COMPLETED already present: externalId={ExternalId}", QaPastCompletedExecutionId);
        }

        // ---------------------------------------------------------------------------
        // SessionExecution: SKIPPED — Status=Partial, only one set per exercise logged.
        // The client started but did not finish the session.
        // ---------------------------------------------------------------------------
        var existingSkippedLog = await mongo.SessionExecutions
            .Find(l => l.ExternalId == QaPastSkippedExecutionId)
            .FirstOrDefaultAsync();

        if (existingSkippedLog is null)
        {
            var skippedStartedAt = skippedSessionDate.AddHours(9); // started at 09:00 UTC.
            var skippedLog = new SessionExecution
            {
                ExternalId  = QaPastSkippedExecutionId,
                // ClientId = ApplicationUser.Id — same reasoning as the completed log above.
                ClientId    = ClientUserId,
                PlanId      = QaPastTrainingPlanExternalId,
                SessionId   = QaPastSessionSkippedId,
                Date        = SessionExecution.ToCompletionDateUtc(skippedStartedAt),
                Status      = SessionExecutionStatus.Partial,
                DateCreated = skippedStartedAt,
                DateUpdated = skippedStartedAt.AddMinutes(20),
                Performance = new SessionExecutionPerformance
                {
                    StartedAt   = skippedStartedAt,
                    CompletedAt = null,
                    Workouts    =
                    [
                        new LoggedWorkout
                        {
                            WorkoutId = PastSkippedWorkoutId,
                            Order     = 0,
                            Name      = "Hlavní",
                            Format    = null,
                            Exercises =
                            [
                                new WorkoutExercise
                                {
                                    ExerciseExternalId = PastBackSquatExerciseId,
                                    ExerciseName       = "QA Back Squat",
                                    Sets               =
                                    [
                                        // Only 1 of 3 planned sets was recorded before the client stopped.
                                        new WorkoutSet { SetNumber = 1, Reps = 5, WeightKg = 100m, CompletedAt = skippedStartedAt.AddMinutes(15) },
                                    ],
                                },
                                new WorkoutExercise
                                {
                                    ExerciseExternalId = PastRomanianDeadliftExerciseId,
                                    ExerciseName       = "QA Romanian Deadlift",
                                    Sets               = [], // exercise was never started
                                },
                            ],
                        },
                    ],
                },
            };

            await mongo.SessionExecutions.InsertOneAsync(skippedLog);
            logger.LogInformation(
                "QA SessionExecution SKIPPED created: externalId={ExternalId} sessionId={SessionId}",
                QaPastSkippedExecutionId, QaPastSessionSkippedId);
        }
        else
        {
            logger.LogInformation(
                "QA SessionExecution SKIPPED already present: externalId={ExternalId}", QaPastSkippedExecutionId);
        }

        // PAST-UNTOUCHED: deliberately no SessionExecution for QaPastSessionUntouchedId.
        logger.LogInformation(
            "QA PastTrainingPlan fixture complete: planId={PlanId} startDate={StartDate} " +
            "completed={CompletedId} skipped={SkippedId} untouched={UntouchedId}",
            QaPastTrainingPlanExternalId, startDate,
            QaPastSessionCompletedId, QaPastSessionSkippedId, QaPastSessionUntouchedId);
    }

    /// <summary>
    /// Seeds a training plan for the second QA client/trainer pair (#474).
    ///
    /// The plan has one Published week with one session.  That session contains
    /// two sections that BOTH reference the same shared exercise
    /// (<see cref="SharedExerciseId"/> = "QA Kettlebell Swing"):
    ///
    ///   Section 1 — Standard (null format) + 1 prescribed set for QA Kettlebell Swing.
    ///   Section 2 — AMRAP 10 min + 1 prescribed set for QA Kettlebell Swing.
    ///
    /// This shape lets the coach-detail "planned-vs-actual" read path verify that
    /// it correctly keys actual values by (SectionId, ExerciseExternalId) rather
    /// than by ExerciseExternalId alone — a different section should return different
    /// values even when the exercise is the same object.
    ///
    /// TrainerId = Trainer2UserId (ApplicationUser.Id) — same rule as all other plans.
    /// ClientId  = Client2UserId (ApplicationUser.Id, #840) — same rule as all other plans.
    /// </summary>
    private static async Task EnsureMultiSectionTrainingPlanAsync(
        IMongoContext mongo,
        Guid client2UserId,
        Guid trainer2UserId,
        ILogger logger)
    {
        var existing = await mongo.TrainingPlans
            .Find(p => p.ExternalId == QaMultiSectionPlanExternalId)
            .FirstOrDefaultAsync();

        if (existing is not null)
        {
            logger.LogInformation(
                "QA MultiSection TrainingPlan already present: externalId={ExternalId}", QaMultiSectionPlanExternalId);
            return;
        }

        var now = DateTime.UtcNow;

        var plan = new TrainingPlan
        {
            ExternalId    = QaMultiSectionPlanExternalId,
            ClientId      = client2UserId,
            TrainerId     = trainer2UserId,
            Name          = "QA Multi-Section Plan — shared-exercise section-keying fixture",
            Status        = TrainingPlanStatus.Active,
            DateCreated   = now,
            Version       = 1,
            Weeks =
            [
                new TrainingWeek
                {
                    WeekNumber    = 1,
                    Status        = WeekStatus.Published,
                    DatePublished = now,
                    Days = BuildTrainingDays(new Dictionary<int, List<TrainingSession>>
                    {
                        [2] = // Tuesday
                        [
                        new TrainingSession
                        {
                            SessionId = QaMultiSectionSessionId,
                            Name      = "QA Multi-Section Session",
                            Order     = 1,
                            Workouts  =
                            [
                                // Section 1 — Standard: prescribed set for QA Kettlebell Swing.
                                new TrainingWorkout
                                {
                                    WorkoutId    = MultiSectionStandardWorkoutId,
                                    Order        = 0,
                                    Name         = "Standard work",
                                    Format       = null,
                                    FormatConfig = null,
                                    Exercises =
                                    [
                                        new SessionExercise
                                        {
                                            ExerciseId         = MultiSectionStandardInstanceId,
                                            ExerciseExternalId = SharedExerciseId,
                                            ExerciseName       = "QA Kettlebell Swing",
                                            Order              = 1,
                                            MovementType       = MovementType.Reps,
                                            // Prescribed: 3 sets × 15 reps @ 24 kg.
                                            Sets =
                                            [
                                                new ExerciseSet { SetNumber = 1, Type = SetType.Normal, Reps = 15, WeightKg = 24m },
                                                new ExerciseSet { SetNumber = 2, Type = SetType.Normal, Reps = 15, WeightKg = 24m },
                                                new ExerciseSet { SetNumber = 3, Type = SetType.Normal, Reps = 15, WeightKg = 24m },
                                            ],
                                        },
                                    ],
                                },
                                // Section 2 — AMRAP 10 min: same exercise but AMRAP context.
                                // No prescribed sets (AMRAP format — reps accumulate per round).
                                new TrainingWorkout
                                {
                                    WorkoutId    = MultiSectionAmrapWorkoutId,
                                    Order        = 1,
                                    Name         = "AMRAP 10 min",
                                    Format       = WorkoutFormat.AMRAP,
                                    FormatConfig = new WodConfig { TimeCapSeconds = 600 },
                                    Exercises =
                                    [
                                        new SessionExercise
                                        {
                                            ExerciseId         = MultiSectionAmrapInstanceId,
                                            ExerciseExternalId = SharedExerciseId,
                                            ExerciseName       = "QA Kettlebell Swing",
                                            Order              = 1,
                                            MovementType       = MovementType.Reps,
                                            // AMRAP: no prescribed sets — client accumulates rounds.
                                            Sets = [],
                                        },
                                    ],
                                },
                            ],
                        },
                        ]
                    }),
                },
            ],
        };

        await mongo.TrainingPlans.InsertOneAsync(plan);

        logger.LogInformation(
            "QA MultiSection TrainingPlan created: externalId={ExternalId} clientId={ClientId}",
            QaMultiSectionPlanExternalId, client2UserId);
    }

    /// <summary>
    /// Seeds a completed WorkoutLog for the multi-section session (#474).
    ///
    /// Standard section — QA Kettlebell Swing with EDITED values (actual != planned):
    ///   Set 1: actual Reps=12, WeightKg=28, planned Reps=15, WeightKg=24  → IsModified=true ("upraveno").
    ///   Set 2: actual Reps=15, WeightKg=24, planned Reps=15, WeightKg=24  → IsModified=false (as-prescribed).
    ///   Set 3: actual Reps=10, WeightKg=28, planned Reps=15, WeightKg=24  → IsModified=true ("upraveno").
    ///
    /// AMRAP section — QA Kettlebell Swing logged at planned values only (no modifications):
    ///   Set 1: actual Reps=15, WeightKg=24, no planned snapshot           → no "upraveno".
    ///
    /// This data lets the coach-detail demonstrate:
    ///   - Standard section: Set 1 and Set 3 show "upraveno"; Set 2 shows plain actual.
    ///   - AMRAP section: shows only the AMRAP count with no "upraveno" badge.
    ///
    /// SectionId is set on each logged section so the section-keying read path works (#472).
    /// </summary>
    private static async Task EnsureMultiSectionExecutionAsync(
        IMongoContext mongo,
        ILogger logger)
    {
        var existing = await mongo.SessionExecutions
            .Find(l => l.ExternalId == QaMultiSectionExecutionId)
            .FirstOrDefaultAsync();

        if (existing is not null)
        {
            logger.LogInformation(
                "QA MultiSection SessionExecution already present: externalId={ExternalId}", QaMultiSectionExecutionId);
            return;
        }

        var completedAt = DateTime.UtcNow.Date.AddDays(-1).AddHours(14); // 14:00 UTC, yesterday.
        var log = new SessionExecution
        {
            ExternalId    = QaMultiSectionExecutionId,
            // ClientId = ApplicationUser.Id — same contract as all other SessionExecutions.
            ClientId      = Client2UserId,
            PlanId        = QaMultiSectionPlanExternalId,
            SessionId     = QaMultiSectionSessionId,
            Date          = SessionExecution.ToCompletionDateUtc(completedAt),
            Status        = SessionExecutionStatus.Completed,
            DateCreated   = completedAt.AddMinutes(-40),
            DateUpdated   = completedAt,
            Performance = new SessionExecutionPerformance
            {
                StartedAt   = completedAt.AddMinutes(-40),
                CompletedAt = completedAt,
                Workouts =
                [
                    // Standard section — edited reps/weights on Set 1 + Set 3; Set 2 as-prescribed.
                    new LoggedWorkout
                    {
                        WorkoutId = MultiSectionStandardWorkoutId,
                        Order     = 0, // mirrors Standard section Order=0 in the plan
                        Name      = "Standard work",
                        Format    = null,
                        Exercises =
                        [
                            new WorkoutExercise
                            {
                                ExerciseExternalId = SharedExerciseId,
                                ExerciseName       = "QA Kettlebell Swing",
                                Sets =
                                [
                                    // MODIFIED — client used heavier KB for fewer reps.
                                    new WorkoutSet
                                    {
                                        SetNumber       = 1,
                                        Reps            = 12,
                                        WeightKg        = 28m,
                                        PlannedReps     = 15,
                                        PlannedWeightKg = 24m,
                                        CompletedAt     = completedAt.AddMinutes(-30),
                                    },
                                    // AS-PRESCRIBED — exactly as planned.
                                    new WorkoutSet
                                    {
                                        SetNumber       = 2,
                                        Reps            = 15,
                                        WeightKg        = 24m,
                                        PlannedReps     = 15,
                                        PlannedWeightKg = 24m,
                                        CompletedAt     = completedAt.AddMinutes(-20),
                                    },
                                    // MODIFIED — client again used heavier KB for fewer reps.
                                    new WorkoutSet
                                    {
                                        SetNumber       = 3,
                                        Reps            = 10,
                                        WeightKg        = 28m,
                                        PlannedReps     = 15,
                                        PlannedWeightKg = 24m,
                                        CompletedAt     = completedAt.AddMinutes(-10),
                                    },
                                ],
                            },
                        ],
                    },
                    // AMRAP section — same exercise, logged at face value (no edits).
                    // No planned snapshot because AMRAP sections don't carry prescribed sets.
                    new LoggedWorkout
                    {
                        WorkoutId = MultiSectionAmrapWorkoutId,
                        Order     = 1, // mirrors AMRAP section Order=1 in the plan
                        Name      = "AMRAP 10 min",
                        Format    = WorkoutFormat.AMRAP,
                        Exercises =
                        [
                            new WorkoutExercise
                            {
                                ExerciseExternalId = SharedExerciseId,
                                ExerciseName       = "QA Kettlebell Swing",
                                Sets =
                                [
                                    // No planned snapshot — AMRAP accumulates rounds, not prescribed sets.
                                    new WorkoutSet
                                    {
                                        SetNumber   = 1,
                                        Reps        = 15,
                                        WeightKg    = 24m,
                                        CompletedAt = completedAt.AddMinutes(-5),
                                    },
                                ],
                            },
                        ],
                    },
                ],
            },
        };

        await mongo.SessionExecutions.InsertOneAsync(log);
        logger.LogInformation(
            "QA MultiSection SessionExecution created: externalId={ExternalId} planId={PlanId} sessionId={SessionId}",
            QaMultiSectionExecutionId, QaMultiSectionPlanExternalId, QaMultiSectionSessionId);
    }

    private static async Task EnsureFoodsAsync(
        IMongoContext mongo,
        Guid nutriUserId,
        ILogger logger)
    {
        var foodIds = new[]
        {
            QaFood1ExternalId, QaFood2ExternalId, QaFood3ExternalId,
            QaFood4ExternalId, QaFood5ExternalId,
        };

        var existingCount = await mongo.Foods
            .CountDocumentsAsync(Builders<Food>.Filter.In(f => f.ExternalId, foodIds));

        if (existingCount == foodIds.Length)
        {
            logger.LogInformation("QA Foods already present ({Count}), skipping.", existingCount);
            return;
        }

        var now = DateTime.UtcNow;

        var foods = new List<Food>
        {
            new()
            {
                ExternalId    = QaFood1ExternalId,
                Name          = "Chicken Breast",
                NutritionistId = nutriUserId,
                Visibility    = FoodVisibility.Public,
                Category      = FoodCategory.Meat,
                DateCreated   = now,
                NutrientValue = new NutrientValue { Kcal = 165m, Protein = 31m, Fat = 3.6m, Carbs = 0m },
            },
            new()
            {
                ExternalId    = QaFood2ExternalId,
                Name          = "White Rice (cooked)",
                NutritionistId = nutriUserId,
                Visibility    = FoodVisibility.Public,
                Category      = FoodCategory.GrainsAndCereals,
                DateCreated   = now,
                NutrientValue = new NutrientValue { Kcal = 130m, Protein = 2.7m, Fat = 0.3m, Carbs = 28m },
            },
            new()
            {
                ExternalId    = QaFood3ExternalId,
                Name          = "Broccoli",
                NutritionistId = nutriUserId,
                Visibility    = FoodVisibility.Public,
                Category      = FoodCategory.Vegetables,
                DateCreated   = now,
                NutrientValue = new NutrientValue { Kcal = 34m, Protein = 2.8m, Fat = 0.4m, Carbs = 7m },
            },
            new()
            {
                ExternalId    = QaFood4ExternalId,
                Name          = "Banana (medium)",
                NutritionistId = nutriUserId,
                Visibility    = FoodVisibility.Public,
                Category      = FoodCategory.Fruit,
                DateCreated   = now,
                NutrientValue = new NutrientValue { Kcal = 89m, Protein = 1.1m, Fat = 0.3m, Carbs = 23m },
            },
            new()
            {
                ExternalId    = QaFood5ExternalId,
                Name          = "Rolled Oats",
                NutritionistId = nutriUserId,
                Visibility    = FoodVisibility.Public,
                Category      = FoodCategory.GrainsAndCereals,
                DateCreated   = now,
                NutrientValue = new NutrientValue { Kcal = 389m, Protein = 13.2m, Fat = 6.5m, Carbs = 68m },
            },
        };

        // Insert only those that are missing (partial re-run after partial seed).
        var existingIds = (await mongo.Foods
            .Find(Builders<Food>.Filter.In(f => f.ExternalId, foodIds))
            .Project(f => f.ExternalId)
            .ToListAsync())
            .ToHashSet();

        var toInsert = foods.Where(f => !existingIds.Contains(f.ExternalId)).ToList();
        if (toInsert.Count > 0)
        {
            await mongo.Foods.InsertManyAsync(toInsert);
        }

        logger.LogInformation("QA Foods created: {Count} inserted.", toInsert.Count);
    }

    private static async Task EnsureRecipesAsync(
        IMongoContext mongo,
        Guid nutriUserId,
        ILogger logger)
    {
        var recipeIds = new[] { QaRecipe1ExternalId, QaRecipe2ExternalId, QaRecipe3ExternalId };

        var existingCount = await mongo.Recipes
            .CountDocumentsAsync(Builders<Recipe>.Filter.In(r => r.ExternalId, recipeIds));

        if (existingCount == recipeIds.Length)
        {
            logger.LogInformation("QA Recipes already present ({Count}), skipping.", existingCount);
            return;
        }

        var now = DateTime.UtcNow;

        var recipes = new List<Recipe>
        {
            new()
            {
                ExternalId      = QaRecipe1ExternalId,
                NutritionistId  = nutriUserId,
                Name            = "Chicken, Rice & Broccoli Bowl",
                Description     = "Classic high-protein post-workout meal.",
                PrepTimeMinutes = 20,
                DietaryPreferences = [nameof(DietaryPreference.GlutenFree), nameof(DietaryPreference.LactoseFree)],
                Visibility      = RecipeVisibility.Public,
                DateCreated     = now,
                Foods =
                [
                    new MealFood { FoodExternalId = QaFood1ExternalId, FoodName = "Chicken Breast", AmountGrams = 150m,
                        NutrientValuePer100Grams = new NutrientValue { Kcal = 165m, Protein = 31m, Fat = 3.6m, Carbs = 0m } },
                    new MealFood { FoodExternalId = QaFood2ExternalId, FoodName = "White Rice (cooked)", AmountGrams = 200m,
                        NutrientValuePer100Grams = new NutrientValue { Kcal = 130m, Protein = 2.7m, Fat = 0.3m, Carbs = 28m } },
                    new MealFood { FoodExternalId = QaFood3ExternalId, FoodName = "Broccoli", AmountGrams = 100m,
                        NutrientValuePer100Grams = new NutrientValue { Kcal = 34m, Protein = 2.8m, Fat = 0.4m, Carbs = 7m } },
                ],
            },
            new()
            {
                ExternalId      = QaRecipe2ExternalId,
                NutritionistId  = nutriUserId,
                Name            = "Oats & Banana Breakfast",
                Description     = "Simple overnight oats with banana.",
                PrepTimeMinutes = 5,
                DietaryPreferences =
                [
                    nameof(DietaryPreference.Vegan), nameof(DietaryPreference.Vegetarian),
                    nameof(DietaryPreference.Pescatarian), nameof(DietaryPreference.LactoseFree),
                ],
                Visibility      = RecipeVisibility.Public,
                DateCreated     = now,
                Foods =
                [
                    new MealFood { FoodExternalId = QaFood5ExternalId, FoodName = "Rolled Oats", AmountGrams = 50m,
                        NutrientValuePer100Grams = new NutrientValue { Kcal = 389m, Protein = 13.2m, Fat = 6.5m, Carbs = 68m } },
                    new MealFood { FoodExternalId = QaFood4ExternalId, FoodName = "Banana (medium)", AmountGrams = 120m,
                        NutrientValuePer100Grams = new NutrientValue { Kcal = 89m, Protein = 1.1m, Fat = 0.3m, Carbs = 23m } },
                ],
            },
            new()
            {
                ExternalId      = QaRecipe3ExternalId,
                NutritionistId  = nutriUserId,
                Name            = "Chicken & Broccoli Stir-fry",
                Description     = "Quick lean stir-fry, no rice.",
                PrepTimeMinutes = 15,
                DietaryPreferences = [nameof(DietaryPreference.GlutenFree), nameof(DietaryPreference.LactoseFree)],
                Visibility      = RecipeVisibility.Public,
                DateCreated     = now,
                Foods =
                [
                    new MealFood { FoodExternalId = QaFood1ExternalId, FoodName = "Chicken Breast", AmountGrams = 180m,
                        NutrientValuePer100Grams = new NutrientValue { Kcal = 165m, Protein = 31m, Fat = 3.6m, Carbs = 0m } },
                    new MealFood { FoodExternalId = QaFood3ExternalId, FoodName = "Broccoli", AmountGrams = 150m,
                        NutrientValuePer100Grams = new NutrientValue { Kcal = 34m, Protein = 2.8m, Fat = 0.4m, Carbs = 7m } },
                ],
            },
        };

        // Insert only those that are missing.
        var existingIds = (await mongo.Recipes
            .Find(Builders<Recipe>.Filter.In(r => r.ExternalId, recipeIds))
            .Project(r => r.ExternalId)
            .ToListAsync())
            .ToHashSet();

        var toInsert = recipes.Where(r => !existingIds.Contains(r.ExternalId)).ToList();
        if (toInsert.Count > 0)
        {
            await mongo.Recipes.InsertManyAsync(toInsert);
        }

        logger.LogInformation("QA Recipes created: {Count} inserted.", toInsert.Count);
    }

    /// <summary>
    /// Seeds one published NutritionPlan assigned to the QA client by the QA nutri.
    /// The plan has 2 Published weeks starting last Monday, each with all 7 days
    /// carrying Breakfast, Lunch, and Dinner meals, so every recent day has a planned
    /// meal count (#1303).
    /// </summary>
    /// <remarks>
    /// #1094 — carries a <c>StartDate</c> anchored to the Monday of the current
    /// (seed-time) week so <c>PlanWindowResolver</c>'s <c>[StartDate, StartDate +
    /// weeks*7)</c> window covers today, and a <c>GlobalSettings</c> block so
    /// GET /nutrition/plans/{id} returns non-null macro targets. Same anchoring
    /// idiom as <see cref="EnsureTrainingPlanAsync"/>'s #898 fix (daysUntilMonday),
    /// applied in place to this SAME plan rather than as a second competing Active
    /// plan: the client has only ever had one Active nutrition plan, so
    /// <c>PlanWindowResolver.ResolveCurrentPlan</c>'s legacy single-candidate
    /// fallback already selected it everywhere (GetTodayPlan, GetWeekPlan,
    /// LogMealEaten, etc.) — giving it an explicit window keeps every one of
    /// those call sites resolving to this exact plan via the window branch
    /// instead of the fallback branch, with no second Active nutrition plan
    /// introduced to make that resolution ambiguous.
    /// </remarks>
    private static async Task EnsureNutritionPlanAsync(
        IMongoContext mongo,
        Guid clientUserId,
        Guid nutriUserId,
        ILogger logger,
        Guid? planExternalId = null,
        string planName = "QA Test Nutrition Plan",
        string mealIdPrefix = DefaultMealIdPrefix)
    {
        var externalId = planExternalId ?? QaNutritionPlanExternalId;

        var existing = await mongo.NutritionPlans
            .Find(p => p.ExternalId == externalId)
            .FirstOrDefaultAsync();

        if (existing is not null)
        {
            logger.LogInformation(
                "QA NutritionPlan already present: externalId={ExternalId}", externalId);
            return;
        }

        var now = DateTime.UtcNow;

        // #1094 — same anchoring idiom as EnsureTrainingPlanAsync's #898 fix, anchored to
        // the Monday of the current (seed-time) week. #1303 starts the plan one week
        // earlier so the last seven days of meal logs fall inside it: the window is
        // [last Monday, next Monday), so it still covers today for 7 days after seeding.
        // POST /test/reset drops the Mongo collections and re-seeds.
        var startDate = MondayOfWeek(now).AddDays(-7);

        var plan = new NutritionPlan
        {
            ExternalId     = externalId,
            ClientId       = clientUserId,
            NutritionistId = nutriUserId,
            Name           = planName,
            Status         = NutritionPlanStatus.Active,
            StartDate      = startDate,
            DateCreated    = now,
            Version        = 1,
            // #1094 — 190/250/82.222g would round to non-exact percentages; these
            // three gram values were chosen so 4*protein + 4*carbs + 9*fat sums to
            // DailyKcal EXACTLY (640 + 960 + 900 = 2500), which is what keeps the
            // client-detail meal-plan card's three rounded percentages (26/38/36)
            // summing to 100 rather than drifting to 99 or 101.
            GlobalSettings = new GlobalNutritionSettings
            {
                DailyKcal     = 2500,
                ProteinGrams  = 160,
                CarbsGrams    = 240,
                FatGrams      = 100,
            },
            Weeks = Enumerable.Range(1, 2).Select(weekNumber => new PlanWeek
            {
                WeekNumber    = weekNumber,
                Status        = WeekStatus.Published,
                DatePublished = now,
                Days = Enumerable.Range(1, 7).Select(dayOfWeek => new PlanDay
                {
                    DayOfWeek = dayOfWeek,
                    Meals =
                    [
                        new PlanMeal
                        {
                            MealId = PlannedMealId(weekNumber, dayOfWeek, 1, mealIdPrefix),
                            Kind   = MealKind.Breakfast,
                            Order  = 1,
                            Time   = "08:00",
                            Foods  = [],
                        },
                        new PlanMeal
                        {
                            MealId = PlannedMealId(weekNumber, dayOfWeek, 2, mealIdPrefix),
                            Kind   = MealKind.Lunch,
                            Order  = 2,
                            Time   = "12:00",
                            // One food and one recipe with stable item ids, so item ticks can be driven by QA.
                            Foods  =
                            [
                                new MealFood
                                {
                                    ItemId = PlannedItemId(weekNumber, dayOfWeek, 2, 1, mealIdPrefix),
                                    FoodExternalId = QaFood1ExternalId,
                                    FoodName = "Chicken Breast",
                                    AmountGrams = 150m,
                                    NutrientValuePer100Grams = new NutrientValue { Kcal = 165m, Protein = 31m, Fat = 3.6m, Carbs = 0m },
                                },
                            ],
                            Recipes =
                            [
                                new MealRecipe
                                {
                                    ItemId = PlannedItemId(weekNumber, dayOfWeek, 2, 2, mealIdPrefix),
                                    RecipeId = QaRecipe1ExternalId,
                                    RecipeName = "Chicken, Rice & Broccoli Bowl",
                                    NutrientValuePerServing = new NutrientValue { Kcal = 650m, Protein = 65m, Fat = 8m, Carbs = 80m },
                                    Servings = 1m,
                                },
                            ],
                        },
                        new PlanMeal
                        {
                            MealId = PlannedMealId(weekNumber, dayOfWeek, 3, mealIdPrefix),
                            Kind   = MealKind.Dinner,
                            Order  = 3,
                            Time   = "18:00",
                            Foods  = [],
                        },
                    ],
                }).ToList(),
            }).ToList(),
        };

        await mongo.NutritionPlans.InsertOneAsync(plan);

        logger.LogInformation(
            "QA NutritionPlan created: externalId={ExternalId} clientId={ClientId}",
            externalId, clientUserId);
    }

    /// <summary>
    /// Seeds a questionnaire template for the QA trainer with two sections and
    /// six answerable question types, a SUBMITTED response for the QA client,
    /// and links that response to the main training plan via
    /// QuestionnaireResponseId (#715).
    ///
    /// The response is created directly against Postgres — bypassing the HTTP
    /// CreateResponse/UpdateResponse/Submit endpoints, same as every other
    /// Ensure* fixture in this file — with Status=Submitted and a SubmittedAt
    /// timestamp, matching the shape the real submit path produces.
    ///
    /// Note on the response's ProfessionalId: it is set to the QA trainer (the
    /// questionnaire's owner). The Postgres-side link-eligibility check in
    /// TrainingPlans/NutritionPlans LinkQuestionnaireEndpoint compares
    /// response.ProfessionalId against the CALLING professional, so a real
    /// trainer-initiated "link questionnaire" HTTP call against this response
    /// would be accepted, but a nutritionist-initiated one would be rejected
    /// (403-equivalent ThrowError). This seed writes the Mongo
    /// QuestionnaireResponseId field directly — bypassing that HTTP-level
    /// check — matching every other Ensure* fixture in this file.
    ///
    /// The nutrition plan used to link to THIS trainer-owned response too
    /// (#715's original shape), but #720 replaced that with a separate
    /// nutritionist-owned template + response (see
    /// <see cref="EnsureNutritionistQuestionnaireFixtureAsync"/>) so the
    /// nutritionist's own "Dotaznik" tab (#698) can render a populated view
    /// through GetClientResponsesEndpoint, which filters by the CALLING
    /// professional's ProfessionalId.
    /// </summary>
    private static async Task EnsureQuestionnaireFixtureAsync(
        ApplicationDbContext db,
        IMongoContext mongo,
        ILogger logger)
    {
        // 1. Questionnaire template — two sections, six answerable question types.
        var questionnaire = await db.Questionnaires
            .Include(q => q.Questions)
            .FirstOrDefaultAsync(q => q.PublicId == QaQuestionnaireExternalId);

        if (questionnaire is null)
        {
            questionnaire = new Questionnaire
            {
                PublicId       = QaQuestionnaireExternalId,
                ProfessionalId = TrainerUserId,
                Title          = "QA Onboarding Questionnaire",
                Description    = "Fixture questionnaire seeded for #715 — exercises every formatAnswerValue branch.",
                IsActive       = true,
                IsDefault      = false,
                Questions =
                [
                    new QuestionnaireQuestion { PublicId = QaQuestionSectionBasicInfoId, OrderIndex = 0, Type = "section",      Label = "Basic Info" },
                    new QuestionnaireQuestion { PublicId = QaQuestionGoalId,             OrderIndex = 1, Type = "short_text",    Label = "What is your main fitness goal?", IsRequired = true },
                    new QuestionnaireQuestion { PublicId = QaQuestionWeightId,           OrderIndex = 2, Type = "number",        Label = "What is your current body weight (kg)?", IsRequired = true, Config = "{\"min\":30,\"max\":250,\"unit\":\"kg\",\"step\":1}" },
                    new QuestionnaireQuestion { PublicId = QaQuestionTrainingDaysId,     OrderIndex = 3, Type = "single_choice", Label = "How many days per week do you currently train?", IsRequired = true, Config = "{\"options\":[\"0\",\"1-2\",\"3-4\",\"5+\"]}" },
                    new QuestionnaireQuestion { PublicId = QaQuestionSectionHealthId,    OrderIndex = 4, Type = "section",      Label = "Health & Preferences" },
                    new QuestionnaireQuestion { PublicId = QaQuestionEnergyId,           OrderIndex = 5, Type = "scale",         Label = "Rate your current energy level", IsRequired = true, Config = "{\"min\":1,\"max\":10,\"labelMin\":\"Low\",\"labelMax\":\"High\"}" },
                    new QuestionnaireQuestion { PublicId = QaQuestionInjuriesId,         OrderIndex = 6, Type = "multi_select",  Label = "Which areas have you previously injured?", Config = "{\"options\":[\"Knee\",\"Back\",\"Shoulder\",\"None\"]}" },
                    new QuestionnaireQuestion { PublicId = QaQuestionMedicalDocId,       OrderIndex = 7, Type = "file_upload",   Label = "Upload a recent medical clearance document (optional)" },
                ],
            };

            db.Questionnaires.Add(questionnaire);
            await db.SaveChangesAsync();
            logger.LogInformation(
                "QA Questionnaire created: externalId={ExternalId} title={Title}",
                QaQuestionnaireExternalId, questionnaire.Title);
        }
        else
        {
            logger.LogInformation("QA Questionnaire already present: externalId={ExternalId}", QaQuestionnaireExternalId);
        }

        // 2. Submitted response for the QA client, against the QA trainer↔client link.
        var response = await db.QuestionnaireResponses
            .FirstOrDefaultAsync(r => r.PublicId == QaQuestionnaireResponseExternalId);

        if (response is null)
        {
            var clientProfile = await db.ClientProfiles.FirstOrDefaultAsync(cp => cp.UserId == ClientUserId)
                ?? throw new InvalidOperationException("QA ClientProfile must be seeded before the questionnaire fixture.");
            var trainerProfile = await db.ProfessionalProfiles.FirstOrDefaultAsync(pp => pp.UserId == TrainerUserId)
                ?? throw new InvalidOperationException("QA trainer ProfessionalProfile must be seeded before the questionnaire fixture.");
            var link = await db.ClientProfessionalLinks
                .FirstOrDefaultAsync(l => l.ClientProfileId == clientProfile.Id && l.ProfessionalProfileId == trainerProfile.Id)
                ?? throw new InvalidOperationException("QA trainer↔client link must be seeded before the questionnaire fixture.");

            // Anchored to a fixed time-of-day 2 days before the seed run, so
            // re-seeding on a later day doesn't shift an already-created row.
            var submittedAt = DateTime.UtcNow.Date.AddDays(-2).AddHours(9);

            response = new QuestionnaireResponse
            {
                PublicId        = QaQuestionnaireResponseExternalId,
                QuestionnaireId = questionnaire.Id,
                ClientId        = ClientUserId,
                ProfessionalId  = TrainerUserId,
                LinkId          = link.Id,
                Status          = QuestionnaireResponseStatus.Submitted,
                Kind            = QuestionnaireResponseKind.Onboarding,
                SubmittedAt     = submittedAt,
                Answers =
                [
                    new QuestionnaireAnswer
                    {
                        PublicId   = new Guid("00000000-0000-0000-7777-000000001002"),
                        QuestionId = questionnaire.Questions.First(q => q.PublicId == QaQuestionGoalId).Id,
                        ValueText  = "Build lean muscle and improve overall strength",
                    },
                    new QuestionnaireAnswer
                    {
                        PublicId    = new Guid("00000000-0000-0000-7777-000000001003"),
                        QuestionId  = questionnaire.Questions.First(q => q.PublicId == QaQuestionWeightId).Id,
                        ValueNumber = 78m,
                    },
                    new QuestionnaireAnswer
                    {
                        PublicId   = new Guid("00000000-0000-0000-7777-000000001004"),
                        QuestionId = questionnaire.Questions.First(q => q.PublicId == QaQuestionTrainingDaysId).Id,
                        ValueText  = "3-4",
                    },
                    new QuestionnaireAnswer
                    {
                        PublicId    = new Guid("00000000-0000-0000-7777-000000001006"),
                        QuestionId  = questionnaire.Questions.First(q => q.PublicId == QaQuestionEnergyId).Id,
                        ValueNumber = 7m,
                    },
                    new QuestionnaireAnswer
                    {
                        PublicId   = new Guid("00000000-0000-0000-7777-000000001007"),
                        QuestionId = questionnaire.Questions.First(q => q.PublicId == QaQuestionInjuriesId).Id,
                        ValueJson  = "[\"Knee\",\"Shoulder\"]",
                    },
                    new QuestionnaireAnswer
                    {
                        PublicId   = new Guid("00000000-0000-0000-7777-000000001008"),
                        QuestionId = questionnaire.Questions.First(q => q.PublicId == QaQuestionMedicalDocId).Id,
                        FileUrl    = "https://storage.qa.formup.test/qa-fixtures/medical-clearance-checkup.pdf",
                    },
                ],
            };

            db.QuestionnaireResponses.Add(response);
            await db.SaveChangesAsync();
            logger.LogInformation(
                "QA QuestionnaireResponse created: externalId={ExternalId} submittedAt={SubmittedAt}",
                QaQuestionnaireResponseExternalId, submittedAt);
        }
        else
        {
            logger.LogInformation("QA QuestionnaireResponse already present: externalId={ExternalId}", QaQuestionnaireResponseExternalId);
        }

        // 3. Link the response to the main training plan (idempotent: only sets
        //    the field the first time — a re-run must never clobber a value a
        //    later test run or manual QA click has since changed).
        var trainingPlan = await mongo.TrainingPlans
            .Find(p => p.ExternalId == QaTrainingPlanExternalId)
            .FirstOrDefaultAsync();

        if (trainingPlan is null)
        {
            logger.LogWarning(
                "QA TrainingPlan not found while linking questionnaire response: externalId={ExternalId}",
                QaTrainingPlanExternalId);
        }
        else if (trainingPlan.QuestionnaireResponseId is not null)
        {
            logger.LogInformation(
                "QA TrainingPlan already linked to a QuestionnaireResponse: externalId={ExternalId}",
                QaTrainingPlanExternalId);
        }
        else
        {
            var filter = Builders<TrainingPlan>.Filter.Eq(p => p.ExternalId, QaTrainingPlanExternalId)
                       & Builders<TrainingPlan>.Filter.Eq(p => p.Version, trainingPlan.Version);
            var update = Builders<TrainingPlan>.Update
                .Set(p => p.QuestionnaireResponseId, QaQuestionnaireResponseExternalId)
                .Set(p => p.DateUpdated, DateTime.UtcNow)
                .Set(p => p.Version, trainingPlan.Version + 1);

            await mongo.TrainingPlans.UpdateOneAsync(filter, update);
            logger.LogInformation(
                "QA TrainingPlan linked to QuestionnaireResponse: externalId={ExternalId} responseId={ResponseId}",
                QaTrainingPlanExternalId, QaQuestionnaireResponseExternalId);
        }

        // Note: the seeded nutrition plan is intentionally NOT linked to this
        // trainer-owned response — see EnsureNutritionistQuestionnaireFixtureAsync
        // (#720), which links it to a nutritionist-owned response instead.
    }

    /// <summary>
    /// Seeds a second questionnaire template + submitted response owned by the
    /// QA nutritionist (ProfessionalId = NutriUserId), and links that response
    /// to the seeded nutrition plan via QuestionnaireResponseId (#720).
    ///
    /// A nutritionist↔client ClientProfessionalLink is seeded here (Rich seed
    /// path only) — GetClientResponsesEndpoint requires an active link between
    /// the calling professional and the client before it returns anything, the
    /// same rule #715's trainer-owned fixture relies on via the trainer↔client
    /// link created unconditionally in SeedAsync.
    ///
    /// Mirrors EnsureQuestionnaireFixtureAsync's shape and idempotency pattern:
    /// stable GUIDs, direct Postgres/Mongo writes (bypassing the HTTP
    /// Create/Submit/Link endpoints), Status=Submitted with a fixed SubmittedAt
    /// anchored relative to the seed run.
    ///
    /// The nutrition plan's QuestionnaireResponseId link is written
    /// unconditionally whenever it doesn't already equal this response's
    /// PublicId — this is what "replaces" #715's original trainer-owned link
    /// (which #715 used to write there before #720) on both a fresh seed and a
    /// pre-#720 database that still has the stale trainer-response id set.
    /// </summary>
    private static async Task EnsureNutritionistQuestionnaireFixtureAsync(
        ApplicationDbContext db,
        IMongoContext mongo,
        ILogger logger)
    {
        // 0. Nutritionist↔client link — required by GetClientResponsesEndpoint's
        //    active-link check before it will return the response to the nutritionist.
        var clientProfile = await db.ClientProfiles.FirstOrDefaultAsync(cp => cp.UserId == ClientUserId)
            ?? throw new InvalidOperationException("QA ClientProfile must be seeded before the nutritionist questionnaire fixture.");
        var nutriProfile = await db.ProfessionalProfiles.FirstOrDefaultAsync(pp => pp.UserId == NutriUserId)
            ?? throw new InvalidOperationException("QA nutri ProfessionalProfile must be seeded before the nutritionist questionnaire fixture.");

        var nutriLink = await db.ClientProfessionalLinks
            .FirstOrDefaultAsync(l => l.ClientProfileId == clientProfile.Id && l.ProfessionalProfileId == nutriProfile.Id);

        if (nutriLink is null)
        {
            nutriLink = new ClientProfessionalLink
            {
                ProfessionalProfileId = nutriProfile.Id,
                ClientProfileId       = clientProfile.Id,
                ProfessionalRole      = UserRole.Nutritionist,
                ConnectionState              = ConnectionState.Connected,
                CanViewNutritionPlans = true,
                CanViewTrainingPlans  = false,
                DateCreated           = DateTime.UtcNow,
            };

            db.ClientProfessionalLinks.Add(nutriLink);
            await db.SaveChangesAsync();
            logger.LogInformation(
                "QA nutritionist↔client link created: nutriProfileId={NutriProfileId} clientProfileId={ClientProfileId}",
                nutriProfile.Id, clientProfile.Id);
        }
        else
        {
            logger.LogInformation(
                "QA nutritionist↔client link already present: nutriProfileId={NutriProfileId} clientProfileId={ClientProfileId}",
                nutriProfile.Id, clientProfile.Id);
        }

        // 1. Questionnaire template — two sections, six answerable question types.
        var questionnaire = await db.Questionnaires
            .Include(q => q.Questions)
            .FirstOrDefaultAsync(q => q.PublicId == QaNutriQuestionnaireExternalId);

        if (questionnaire is null)
        {
            questionnaire = new Questionnaire
            {
                PublicId       = QaNutriQuestionnaireExternalId,
                ProfessionalId = NutriUserId,
                Title          = "QA Nutrition Intake Questionnaire",
                Description    = "Fixture questionnaire seeded for #720 — owned by the QA nutritionist so GetClientResponsesEndpoint returns a populated response for the nutritionist's own view.",
                IsActive       = true,
                IsDefault      = false,
                Questions =
                [
                    new QuestionnaireQuestion { PublicId = QaNutriQuestionSectionIntakeId,    OrderIndex = 0, Type = "section",      Label = "Nutrition Info" },
                    new QuestionnaireQuestion { PublicId = QaNutriQuestionDietGoalId,          OrderIndex = 1, Type = "short_text",    Label = "What is your primary dietary goal?", IsRequired = true },
                    new QuestionnaireQuestion { PublicId = QaNutriQuestionCaloriesId,          OrderIndex = 2, Type = "number",        Label = "How many calories do you currently consume per day?", IsRequired = true, Config = "{\"min\":1000,\"max\":6000,\"unit\":\"kcal\",\"step\":50}" },
                    new QuestionnaireQuestion { PublicId = QaNutriQuestionMealsPerDayId,       OrderIndex = 3, Type = "single_choice", Label = "How many meals do you eat per day?", IsRequired = true, Config = "{\"options\":[\"1-2\",\"3-4\",\"5+\"]}" },
                    new QuestionnaireQuestion { PublicId = QaNutriQuestionSectionLifestyleId,  OrderIndex = 4, Type = "section",      Label = "Lifestyle & Restrictions" },
                    new QuestionnaireQuestion { PublicId = QaNutriQuestionAppetiteId,          OrderIndex = 5, Type = "scale",         Label = "Rate your current appetite level", IsRequired = true, Config = "{\"min\":1,\"max\":10,\"labelMin\":\"Low\",\"labelMax\":\"High\"}" },
                    new QuestionnaireQuestion { PublicId = QaNutriQuestionAllergiesId,         OrderIndex = 6, Type = "multi_select",  Label = "Which foods do you need to avoid?", Config = "{\"options\":[\"Gluten\",\"Dairy\",\"Nuts\",\"None\"]}" },
                    new QuestionnaireQuestion { PublicId = QaNutriQuestionFoodDiaryId,         OrderIndex = 7, Type = "file_upload",   Label = "Upload a recent food diary (optional)" },
                ],
            };

            db.Questionnaires.Add(questionnaire);
            await db.SaveChangesAsync();
            logger.LogInformation(
                "QA nutritionist Questionnaire created: externalId={ExternalId} title={Title}",
                QaNutriQuestionnaireExternalId, questionnaire.Title);
        }
        else
        {
            logger.LogInformation("QA nutritionist Questionnaire already present: externalId={ExternalId}", QaNutriQuestionnaireExternalId);
        }

        // 2. Submitted response for the QA client, against the nutritionist↔client link.
        var response = await db.QuestionnaireResponses
            .FirstOrDefaultAsync(r => r.PublicId == QaNutriQuestionnaireResponseExternalId);

        if (response is null)
        {
            // Anchored to a fixed time-of-day 1 day before the seed run — distinct
            // from the trainer response's -2 days so the two never collide, and
            // stable across re-seeds (computed once, never overwritten).
            var submittedAt = DateTime.UtcNow.Date.AddDays(-1).AddHours(10);

            response = new QuestionnaireResponse
            {
                PublicId        = QaNutriQuestionnaireResponseExternalId,
                QuestionnaireId = questionnaire.Id,
                ClientId        = ClientUserId,
                ProfessionalId  = NutriUserId,
                LinkId          = nutriLink.Id,
                Status          = QuestionnaireResponseStatus.Submitted,
                Kind            = QuestionnaireResponseKind.Onboarding,
                SubmittedAt     = submittedAt,
                Answers =
                [
                    new QuestionnaireAnswer
                    {
                        PublicId   = new Guid("00000000-0000-0000-8888-000000001002"),
                        QuestionId = questionnaire.Questions.First(q => q.PublicId == QaNutriQuestionDietGoalId).Id,
                        ValueText  = "Lose body fat while preserving muscle mass",
                    },
                    new QuestionnaireAnswer
                    {
                        PublicId    = new Guid("00000000-0000-0000-8888-000000001003"),
                        QuestionId  = questionnaire.Questions.First(q => q.PublicId == QaNutriQuestionCaloriesId).Id,
                        ValueNumber = 2200m,
                    },
                    new QuestionnaireAnswer
                    {
                        PublicId   = new Guid("00000000-0000-0000-8888-000000001004"),
                        QuestionId = questionnaire.Questions.First(q => q.PublicId == QaNutriQuestionMealsPerDayId).Id,
                        ValueText  = "3-4",
                    },
                    new QuestionnaireAnswer
                    {
                        PublicId    = new Guid("00000000-0000-0000-8888-000000001006"),
                        QuestionId  = questionnaire.Questions.First(q => q.PublicId == QaNutriQuestionAppetiteId).Id,
                        ValueNumber = 6m,
                    },
                    new QuestionnaireAnswer
                    {
                        PublicId   = new Guid("00000000-0000-0000-8888-000000001007"),
                        QuestionId = questionnaire.Questions.First(q => q.PublicId == QaNutriQuestionAllergiesId).Id,
                        ValueJson  = "[\"Gluten\",\"Dairy\"]",
                    },
                    new QuestionnaireAnswer
                    {
                        PublicId   = new Guid("00000000-0000-0000-8888-000000001008"),
                        QuestionId = questionnaire.Questions.First(q => q.PublicId == QaNutriQuestionFoodDiaryId).Id,
                        FileUrl    = "https://storage.qa.formup.test/qa-fixtures/food-diary-week1.pdf",
                    },
                ],
            };

            db.QuestionnaireResponses.Add(response);
            await db.SaveChangesAsync();
            logger.LogInformation(
                "QA nutritionist QuestionnaireResponse created: externalId={ExternalId} submittedAt={SubmittedAt}",
                QaNutriQuestionnaireResponseExternalId, submittedAt);
        }
        else
        {
            logger.LogInformation("QA nutritionist QuestionnaireResponse already present: externalId={ExternalId}", QaNutriQuestionnaireResponseExternalId);
        }

        // 3. Link the response to the seeded nutrition plan — REPLACES #715's
        //    trainer-owned link. Unlike EnsureQuestionnaireFixtureAsync's
        //    "only set if null" guard, this write is unconditional whenever the
        //    current value isn't already THIS response's id, so a pre-#720
        //    database (still pointing at the old trainer-owned response) gets
        //    corrected on the next seed run instead of staying stale forever.
        var nutritionPlan = await mongo.NutritionPlans
            .Find(p => p.ExternalId == QaNutritionPlanExternalId)
            .FirstOrDefaultAsync();

        if (nutritionPlan is null)
        {
            logger.LogWarning(
                "QA NutritionPlan not found while linking nutritionist QuestionnaireResponse: externalId={ExternalId}",
                QaNutritionPlanExternalId);
        }
        else if (nutritionPlan.QuestionnaireResponseId == QaNutriQuestionnaireResponseExternalId)
        {
            logger.LogInformation(
                "QA NutritionPlan already linked to the nutritionist QuestionnaireResponse: externalId={ExternalId}",
                QaNutritionPlanExternalId);
        }
        else
        {
            var filter = Builders<NutritionPlan>.Filter.Eq(p => p.ExternalId, QaNutritionPlanExternalId)
                       & Builders<NutritionPlan>.Filter.Eq(p => p.Version, nutritionPlan.Version);
            var update = Builders<NutritionPlan>.Update
                .Set(p => p.QuestionnaireResponseId, QaNutriQuestionnaireResponseExternalId)
                .Set(p => p.DateUpdated, DateTime.UtcNow)
                .Set(p => p.Version, nutritionPlan.Version + 1);

            await mongo.NutritionPlans.UpdateOneAsync(filter, update);
            logger.LogInformation(
                "QA NutritionPlan (re)linked to nutritionist QuestionnaireResponse, replacing any prior trainer-owned link: externalId={ExternalId} responseId={ResponseId}",
                QaNutritionPlanExternalId, QaNutriQuestionnaireResponseExternalId);
        }
    }

    private static async Task EnsureAvatarAsync(IServiceProvider sp, ILogger logger)
    {
        var blobStorage = sp.GetRequiredService<IBlobStorageService>();

        if (await blobStorage.ObjectExistsAsync(QaAvatarBlobKey, CancellationToken.None))
        {
            logger.LogInformation("QA avatar blob already present at {Key}, skipping.", QaAvatarBlobKey);
            return;
        }

        var bytes = LoadEmbeddedAsset("qa-avatar.png");
        await blobStorage.UploadAsync(QaAvatarBlobKey, bytes, "image/png", CancellationToken.None);
        logger.LogInformation("QA avatar blob uploaded to {Key} ({Bytes} bytes).", QaAvatarBlobKey, bytes.Length);
    }

    private static async Task EnsureFoodImageAsync(IServiceProvider sp, ILogger logger)
    {
        var blobStorage = sp.GetRequiredService<IBlobStorageService>();

        if (await blobStorage.ObjectExistsAsync(QaFoodImageBlobKey, CancellationToken.None))
        {
            logger.LogInformation("QA food image blob already present at {Key}, skipping.", QaFoodImageBlobKey);
            return;
        }

        var bytes = LoadEmbeddedAsset("qa-food.png");
        await blobStorage.UploadAsync(QaFoodImageBlobKey, bytes, "image/png", CancellationToken.None);
        logger.LogInformation("QA food image blob uploaded to {Key} ({Bytes} bytes).", QaFoodImageBlobKey, bytes.Length);
    }

    private static byte[] LoadEmbeddedAsset(string fileName)
    {
        var asm = typeof(QaSeedRunner).Assembly;
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.Ordinal));
        if (resourceName is null)
            throw new InvalidOperationException(
                $"Embedded asset {fileName} not found. Did the .csproj <EmbeddedResource> entry land?");
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Could not open embedded asset stream for {fileName}.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// #879 — seeds a completed <see cref="SessionExecution"/> for
    /// <see cref="QaDualPlacementSessionId"/> that marks ONLY the standalone occurrence
    /// (<see cref="QaDualPlacementStandaloneInstanceId"/>) complete, leaving the nested
    /// occurrence (<see cref="QaDualPlacementNestedInstanceId"/>) incomplete. Both share the
    /// same catalog exercise (<see cref="QaDualPlacementExerciseId"/>) but carry distinct
    /// instance ids (#857 phase 3b), so <c>GetFullTrainingPlanEndpoint</c>'s per-instance
    /// completion lookup (<c>CompletedExerciseInstanceIds</c>) must report the two placements
    /// differently — this fixture makes that distinction observable via
    /// GET /client/training/plans/{planId} instead of requiring an interactive drive.
    /// Gated to the Rich seed path only; never created for the Minimal kind.
    /// </summary>
    private static async Task EnsureDualPlacementCompletionAsync(IMongoContext mongo, ILogger logger)
    {
        var existing = await mongo.SessionExecutions
            .Find(l => l.ExternalId == QaDualPlacementSessionExecutionId)
            .FirstOrDefaultAsync();

        if (existing is not null)
        {
            logger.LogInformation(
                "QA DualPlacement SessionExecution already present: externalId={ExternalId}", QaDualPlacementSessionExecutionId);
            return;
        }

        var completedAt = DateTime.UtcNow.Date.AddDays(-1).AddHours(9);
        var execution = new SessionExecution
        {
            ExternalId  = QaDualPlacementSessionExecutionId,
            // ClientId = ApplicationUser.Id — mirrors EnsureMainPlanExecutionAsync's convention.
            ClientId    = ClientUserId,
            PlanId      = QaTrainingPlanExternalId,
            SessionId   = QaDualPlacementSessionId,
            Date        = SessionExecution.ToCompletionDateUtc(completedAt),
            Status      = SessionExecutionStatus.Partial,
            DateCreated = completedAt,
            DateUpdated = completedAt,
            CompletedExerciseInstanceIds = [QaDualPlacementStandaloneInstanceId],
        };

        await mongo.SessionExecutions.InsertOneAsync(execution);
        logger.LogInformation(
            "QA DualPlacement SessionExecution created: externalId={ExternalId} planId={PlanId} sessionId={SessionId} completedInstanceId={CompletedInstanceId}",
            QaDualPlacementSessionExecutionId, QaTrainingPlanExternalId, QaDualPlacementSessionId, QaDualPlacementStandaloneInstanceId);
    }

    /// <summary>
    /// #946 — seeds two <see cref="PhotoDiaryRequest"/> rows on the nutritionist↔client link
    /// (created by <see cref="EnsureNutritionistQuestionnaireFixtureAsync"/>, which MUST run
    /// first) so a navigable requestId exists for the index/detail (list, get-by-id) and the
    /// finalize (photo upload) screens: one scoped to the seeded QA nutrition plan, one
    /// deliberately plan-less — a legitimate backend state per
    /// <see cref="PhotoDiaryRequest.PlanId"/>'s own doc comment, not a synthetic edge case. Both
    /// rows are Status=Accepted so they surface on the client app's active-request list (mobile
    /// filters {Accepted, InProgress}) and are eligible for FinalizePlanPhotoEndpoint (Accepted or
    /// InProgress). This does NOT make the accept screen (either PhotoDiaryMode — bulk or
    /// workflow) or the dismiss screen drivable: both AcceptRequestEndpoint and
    /// DismissRequestEndpoint reject any request whose Status isn't Pending, so an
    /// already-Accepted row 409s on both. Driving those two screens needs a third, Pending-status
    /// fixture row — not attempted here.
    /// Re-resolves the nutritionist↔client link by (clientProfile.Id, nutriProfile.Id) — its
    /// Id is DB-generated and only ever held as a local inside
    /// EnsureNutritionistQuestionnaireFixtureAsync, never published as a Qa* constant.
    /// Gated to the Rich seed path only; never created for the Minimal kind.
    /// </summary>
    private static async Task EnsurePhotoDiaryRequestsFixtureAsync(ApplicationDbContext db, ILogger logger)
    {
        var clientProfile = await db.ClientProfiles.FirstOrDefaultAsync(cp => cp.UserId == ClientUserId)
            ?? throw new InvalidOperationException("QA ClientProfile must be seeded before the photo diary requests fixture.");
        var nutriProfile = await db.ProfessionalProfiles.FirstOrDefaultAsync(pp => pp.UserId == NutriUserId)
            ?? throw new InvalidOperationException("QA nutri ProfessionalProfile must be seeded before the photo diary requests fixture.");

        var nutriLink = await db.ClientProfessionalLinks
            .FirstOrDefaultAsync(l => l.ClientProfileId == clientProfile.Id && l.ProfessionalProfileId == nutriProfile.Id)
            ?? throw new InvalidOperationException(
                "QA nutritionist<->client link must be seeded before the photo diary requests fixture — " +
                "EnsureNutritionistQuestionnaireFixtureAsync must run first.");

        var requestIds = new[] { QaPhotoDiaryRequestWithPlanId, QaPhotoDiaryRequestPlanlessId };

        var existingIds = (await db.PhotoDiaryRequests
            .Where(r => requestIds.Contains(r.Id))
            .Select(r => r.Id)
            .ToListAsync())
            .ToHashSet();

        if (existingIds.Count == requestIds.Length)
        {
            logger.LogInformation("QA PhotoDiaryRequest fixtures already present ({Count}), skipping.", existingIds.Count);
            return;
        }

        var acceptedAt = DateTimeOffset.UtcNow.AddDays(-1);

        var requests = new List<PhotoDiaryRequest>
        {
            new()
            {
                Id             = QaPhotoDiaryRequestWithPlanId,
                ProfessionalId = NutriUserId,
                LinkId         = nutriLink.Id,
                PlanId         = QaNutritionPlanExternalId,
                DurationDays   = 7,
                Mode           = PhotoDiaryMode.Bulk,
                Status         = PhotoDiaryStatus.Accepted,
                AcceptedAt     = acceptedAt,
                CreatedAt      = acceptedAt,
                UpdatedAt      = acceptedAt,
            },
            new()
            {
                Id             = QaPhotoDiaryRequestPlanlessId,
                ProfessionalId = NutriUserId,
                LinkId         = nutriLink.Id,
                PlanId         = null,
                DurationDays   = 7,
                Mode           = PhotoDiaryMode.Workflow,
                Status         = PhotoDiaryStatus.Accepted,
                AcceptedAt     = acceptedAt,
                CreatedAt      = acceptedAt,
                UpdatedAt      = acceptedAt,
            },
        };

        var toInsert = requests.Where(r => !existingIds.Contains(r.Id)).ToList();
        db.PhotoDiaryRequests.AddRange(toInsert);
        await db.SaveChangesAsync();

        logger.LogInformation(
            "QA PhotoDiaryRequest fixtures created: {Count} inserted, linkId={LinkId}", toInsert.Count, nutriLink.Id);
    }

    /// <summary>
    /// #1095 — Conversation between qa.trainer and qa.client with three messages: an opening
    /// message from the client (read), a coach reply containing a YouTube URL (read), and a
    /// second client message left unread — so the trainer's UnreadMessages chip and the inbox
    /// thread's YouTube-preview rendering both have a fixture to exercise. Messages are dated
    /// via the two-pass insert-then-<c>ExecuteUpdateAsync</c> trick
    /// (<c>GetMessagesEndpointTests.SeedMessagesAsync</c>) — <c>ApplyTimestamps</c> unconditionally
    /// overwrites <c>DateCreated</c> with <c>UtcNow</c> on insert, so the intended timestamps are
    /// stamped back on afterward via a change-tracker-bypassing update.
    /// </summary>
    private static async Task EnsureInboxConversationFixtureAsync(ApplicationDbContext db, ILogger logger)
    {
        var existing = await db.Conversations.AnyAsync(c => c.PublicId == QaInboxConversationId);

        if (existing)
        {
            logger.LogInformation("QA inbox conversation fixture already present, skipping.");
            return;
        }

        var conversation = new Conversation
        {
            PublicId = QaInboxConversationId,
            ProfessionalUserId = TrainerUserId,
            ClientUserId = ClientUserId,
        };
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();

        var now = DateTime.UtcNow;
        var messages = new List<(Guid Id, Guid SenderId, string Text, bool IsRead, DateTime SentAt)>
        {
            (QaInboxMessage1Id, ClientUserId, "Hi coach, quick question about my program!", true, now.AddHours(-3)),
            (QaInboxMessage2Id, TrainerUserId,
                "Sure, check out this form breakdown: https://www.youtube.com/watch?v=dQw4w9WgXcQ",
                true, now.AddHours(-2)),
            (QaInboxMessage3Id, ClientUserId, "Thanks! One more thing...", false, now.AddHours(-1)),
        };

        // Pass 1: insert (ApplyTimestamps overwrites DateCreated with UtcNow on Added rows).
        foreach (var (id, senderId, text, isRead, sentAt) in messages)
        {
            db.ChatMessages.Add(new ChatMessage
            {
                PublicId = id,
                ConversationId = conversation.Id,
                SenderUserId = senderId,
                Text = text,
                IsRead = isRead,
                DateCreated = sentAt,
                DateUpdated = sentAt,
            });
        }

        await db.SaveChangesAsync();

        // Pass 2: force the intended DateCreated via ExecuteUpdateAsync (bypasses the change
        // tracker, so pass 1's ApplyTimestamps overwrite does not survive).
        foreach (var (id, _, _, _, sentAt) in messages)
        {
            await db.ChatMessages
                .Where(m => m.PublicId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.DateCreated, sentAt));
        }

        var last = messages[^1];
        conversation.LastMessageText = last.Text.Length > 300 ? last.Text[..300] : last.Text;
        conversation.LastMessageAt = last.SentAt;
        conversation.LastMessageSenderId = last.SenderId;
        await db.SaveChangesAsync();

        logger.LogInformation(
            "QA inbox conversation fixture created: conversationId={ConversationId}", conversation.PublicId);
    }

    /// <summary>
    /// #1101 — Conversation between qa.trainer and the former client (ended link): a client
    /// message, a coach reply, then an unread client message. Mirrors the real end state — the
    /// conversation is untouched and <c>IsFormer</c> stays false. Messages are dated with the same
    /// insert-then-<c>ExecuteUpdateAsync</c> pass as <see cref="EnsureInboxConversationFixtureAsync"/>.
    /// </summary>
    private static async Task EnsureEndedLinkConversationFixtureAsync(ApplicationDbContext db, ILogger logger)
    {
        if (await db.Conversations.AnyAsync(c => c.PublicId == QaEndedLinkConversationId))
        {
            logger.LogInformation("QA ended-link conversation fixture already present, skipping.");
            return;
        }

        var conversation = new Conversation
        {
            PublicId = QaEndedLinkConversationId,
            ProfessionalUserId = TrainerUserId,
            ClientUserId = ClientFormerUserId,
        };
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();

        var now = DateTime.UtcNow;
        var messages = new List<(Guid Id, Guid SenderId, string Text, bool IsRead, DateTime SentAt)>
        {
            (QaEndedLinkMessage1Id, TrainerUserId, "Thanks for working with me — good luck!", true, now.AddHours(-5)),
            (QaEndedLinkMessage2Id, ClientFormerUserId, "One last question about my old plan.", false, now.AddHours(-4)),
        };

        foreach (var (id, senderId, text, isRead, sentAt) in messages)
        {
            db.ChatMessages.Add(new ChatMessage
            {
                PublicId = id,
                ConversationId = conversation.Id,
                SenderUserId = senderId,
                Text = text,
                IsRead = isRead,
                DateCreated = sentAt,
                DateUpdated = sentAt,
            });
        }

        await db.SaveChangesAsync();

        foreach (var (id, _, _, _, sentAt) in messages)
        {
            await db.ChatMessages
                .Where(m => m.PublicId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.DateCreated, sentAt));
        }

        var last = messages[^1];
        conversation.LastMessageText = last.Text;
        conversation.LastMessageAt = last.SentAt;
        conversation.LastMessageSenderId = last.SenderId;
        await db.SaveChangesAsync();

        logger.LogInformation(
            "QA ended-link conversation fixture created: conversationId={ConversationId}", conversation.PublicId);
    }

    /// <summary>
    /// #1095 — Two check-in responses on the qa.trainer&lt;-&gt;qa.client link: one submitted but not
    /// yet reviewed by the trainer (exercises the NewCheckIns filter chip), and one past due and
    /// unanswered (exercises MissingCheckIns). Their week starts are distinct Mondays.
    /// </summary>
    private static async Task EnsureInboxWeeklyCheckInsFixtureAsync(ApplicationDbContext db, ILogger logger)
    {
        var checkInIds = new[] { QaWeeklyCheckInRespondedUnreviewedId, QaWeeklyCheckInExpiredId };

        var existingIds = (await db.QuestionnaireResponses
            .Where(r => checkInIds.Contains(r.PublicId))
            .Select(r => r.PublicId)
            .ToListAsync())
            .ToHashSet();

        if (existingIds.Count == checkInIds.Length)
        {
            logger.LogInformation("QA inbox check-in fixtures already present ({Count}), skipping.", existingIds.Count);
            return;
        }

        var target = await FindCheckInTargetAsync(db, ClientUserId, TrainerUserId);
        var now = DateTime.UtcNow;
        var thisMonday = MondayOfWeek(now);

        var checkIns = new List<QuestionnaireResponse>
        {
            new()
            {
                PublicId = QaWeeklyCheckInRespondedUnreviewedId,
                ClientId = ClientUserId,
                ProfessionalId = TrainerUserId,
                QuestionnaireId = target.QuestionnaireId,
                LinkId = target.LinkId,
                Kind = QuestionnaireResponseKind.CheckIn,
                Status = QuestionnaireResponseStatus.Submitted,
                CheckInWeekStart = DateOnly.FromDateTime(thisMonday.AddDays(-7)),
                CheckInDueAtUtc = now.AddDays(-6),
                SubmittedAt = now.AddDays(-7),
            },
            new()
            {
                PublicId = QaWeeklyCheckInExpiredId,
                ClientId = ClientUserId,
                ProfessionalId = TrainerUserId,
                QuestionnaireId = target.QuestionnaireId,
                LinkId = target.LinkId,
                Kind = QuestionnaireResponseKind.CheckIn,
                Status = QuestionnaireResponseStatus.Pending,
                CheckInWeekStart = DateOnly.FromDateTime(thisMonday.AddDays(-14)),
                CheckInDueAtUtc = now.AddDays(-13),
            },
        };

        var toInsert = checkIns.Where(c => !existingIds.Contains(c.PublicId)).ToList();
        db.QuestionnaireResponses.AddRange(toInsert);
        await db.SaveChangesAsync();

        logger.LogInformation("QA inbox check-in fixtures created: {Count} inserted.", toInsert.Count);
    }

    /// <summary>
    /// #1303 — check-in history for the qa.trainer scope: the newest slot (next Monday) is waiting,
    /// last week and weeks 4 and 6 back are answered and already reviewed, week 5 back is missed.
    /// Together with the two #1095 rows (weeks 1 and 2 back) the history shows answered and missed
    /// weeks, and the inbox NewCheckIns chip still sees only the #1095 unreviewed row.
    /// </summary>
    private static async Task EnsureOverviewCheckInsAsync(
        ApplicationDbContext db,
        ILogger logger,
        Guid? clientUserId = null,
        Guid? professionalUserId = null,
        Guid[]? ids = null)
    {
        var checkInClientId = clientUserId ?? ClientUserId;
        var checkInProfessionalId = professionalUserId ?? TrainerUserId;

        // Order: waiting, answered last week, answered week 4, missed week 5, answered week 6.
        var checkInIds = ids ??
        [
            QaCheckInWaitingId, QaCheckInAnsweredLastWeekId, QaCheckInAnsweredWeek4Id,
            QaCheckInMissedWeek5Id, QaCheckInAnsweredWeek6Id,
        ];

        var existingIds = (await db.QuestionnaireResponses
            .Where(r => checkInIds.Contains(r.PublicId))
            .Select(r => r.PublicId)
            .ToListAsync())
            .ToHashSet();

        if (existingIds.Count == checkInIds.Length)
        {
            logger.LogInformation("QA overview check-ins already present ({Count}), skipping.", existingIds.Count);
            return;
        }

        var target = await FindCheckInTargetAsync(db, checkInClientId, checkInProfessionalId);
        var now = DateTime.UtcNow;
        var anchor = MondayOfWeek(now).AddDays(7);

        QuestionnaireResponse Build(Guid id, int weeksBack, QuestionnaireResponseStatus status)
        {
            var weekStart = anchor.AddDays(-7 * weeksBack);
            var sentAt = weekStart.AddDays(-1).AddHours(18);
            var isWaiting = weeksBack == 0;

            return new QuestionnaireResponse
            {
                PublicId = id,
                ClientId = checkInClientId,
                ProfessionalId = checkInProfessionalId,
                QuestionnaireId = target.QuestionnaireId,
                LinkId = target.LinkId,
                Kind = QuestionnaireResponseKind.CheckIn,
                Status = status,
                CheckInWeekStart = DateOnly.FromDateTime(weekStart),
                CheckInDueAtUtc = isWaiting ? now.AddDays(2) : sentAt.AddDays(2),
                SubmittedAt = status == QuestionnaireResponseStatus.Submitted ? sentAt.AddHours(3) : null,
                ReviewedByCoachAtUtc = status == QuestionnaireResponseStatus.Submitted ? sentAt.AddHours(3).AddMinutes(30) : null,
            };
        }

        var checkIns = new List<QuestionnaireResponse>
        {
            Build(checkInIds[0], 0, QuestionnaireResponseStatus.Pending),
            Build(checkInIds[1], 1, QuestionnaireResponseStatus.Submitted),
            Build(checkInIds[2], 4, QuestionnaireResponseStatus.Submitted),
            Build(checkInIds[3], 5, QuestionnaireResponseStatus.Pending),
            Build(checkInIds[4], 6, QuestionnaireResponseStatus.Submitted),
        };

        var toInsert = checkIns.Where(c => !existingIds.Contains(c.PublicId)).ToList();
        db.QuestionnaireResponses.AddRange(toInsert);
        await db.SaveChangesAsync();

        logger.LogInformation("QA overview check-ins created: {Count} inserted.", toInsert.Count);
    }

    /// <summary>The link and the coach's check-in form a seeded check-in response points at.</summary>
    private static async Task<(long LinkId, long QuestionnaireId)> FindCheckInTargetAsync(
        ApplicationDbContext db, Guid clientUserId, Guid professionalUserId)
    {
        await DefaultCheckInForm.EnsureAsync(db, professionalUserId, "en", CancellationToken.None);

        var linkId = await db.ClientProfessionalLinks
            .Where(l => l.ClientProfile.UserId == clientUserId && l.ProfessionalProfile.UserId == professionalUserId)
            .Select(l => l.Id)
            .FirstAsync();

        var questionnaireId = await db.Questionnaires
            .Where(q => q.ProfessionalId == professionalUserId && q.Type == QuestionnaireType.CheckIn)
            .OrderBy(q => q.Id)
            .Select(q => q.Id)
            .FirstAsync();

        return (linkId, questionnaireId);
    }

    /// <summary>Returns the UTC-midnight Monday of the ISO week containing <paramref name="now"/>.</summary>
    private static DateTime MondayOfWeek(DateTime now) =>
        now.Date.AddDays(-(((int)now.DayOfWeek == 0 ? 7 : (int)now.DayOfWeek) - 1));

    /// <summary>
    /// Stable id of a planned meal in a QA nutrition plan; <paramref name="mealIndex"/> is 1-3
    /// (breakfast, lunch, dinner). <paramref name="prefix"/> is the first four GUID groups, so
    /// each plan gets its own id space.
    /// </summary>
    private static Guid PlannedMealId(int weekNumber, int dayOfWeek, int mealIndex, string prefix = DefaultMealIdPrefix) =>
        new($"{prefix}-{weekNumber:D2}{dayOfWeek:D2}0000000{mealIndex}");

    /// <summary>
    /// Stable id of an item (food or recipe) of a planned QA meal; <paramref name="itemIndex"/> is
    /// 1 for the seeded food and 2 for the seeded recipe. Shares the meal id prefix, so it never
    /// collides with a meal id.
    /// </summary>
    private static Guid PlannedItemId(int weekNumber, int dayOfWeek, int mealIndex, int itemIndex, string prefix = DefaultMealIdPrefix) =>
        new($"{prefix}-{weekNumber:D2}{dayOfWeek:D2}{mealIndex:D2}{itemIndex:D2}0000");

    /// <summary>Meal id prefix of the qa.nutri plan for qa.client.</summary>
    private const string DefaultMealIdPrefix = "00000000-0000-0000-1111";

    /// <summary>Meal id prefix of qa.coach's plan for QA Duo.</summary>
    private const string DuoMealIdPrefix = "44444444-4444-4444-1111";

    /// <summary>
    /// #1303 — fills in qa.client's identity fields (date of birth, height, weight) and the
    /// onboarding answers with their derived nutrition targets, using only codes the web
    /// <c>clients.values</c> translations know. Existing values are never overwritten.
    /// </summary>
    private static async Task EnsureClientOverviewProfileAsync(
        ApplicationDbContext db,
        ClientProfile clientProfile,
        ILogger logger,
        decimal heightCm = 182m,
        decimal weightKg = 80.5m,
        decimal targetWeightKg = 76m)
    {
        var now = DateTime.UtcNow;

        if (clientProfile.DateOfBirth is null)
        {
            clientProfile.DateOfBirth = now.Date.AddYears(-32).AddDays(-45);
            clientProfile.HeightCm    = heightCm;
            clientProfile.WeightKg    = weightKg;
            clientProfile.Goals       = "Lose fat and get stronger.";
            await db.SaveChangesAsync();
        }

        if (await db.ClientOnboardingData.AnyAsync(o => o.ClientProfileId == clientProfile.Id))
        {
            logger.LogInformation("QA client onboarding data already present.");
            return;
        }

        db.ClientOnboardingData.Add(new ClientOnboardingData
        {
            ClientProfileId          = clientProfile.Id,
            DateOfBirth              = clientProfile.DateOfBirth!.Value,
            Sex                      = BiologicalSex.Male,
            HeightCm                 = heightCm,
            WeightKg                 = weightKg,
            TargetWeightKg           = targetWeightKg,
            BodyType                 = BodyType.Mesomorph,
            PrimaryGoal              = PrimaryGoal.LoseFat,
            TimeHorizon              = TimeHorizon.SixMonths,
            JobType                  = JobType.Sedentary,
            SleepHours               = 6,
            StressLevel              = 4,
            CurrentTrainingFrequency = CurrentTrainingFrequency.Occasional,
            DesiredTrainingFrequency = DesiredTrainingFrequency.FourPerWeek,
            FitnessRating            = 5,
            GymAccess                = GymAccess.Yes,
            PreferredActivities      = "strength,cardio",
            Injuries                 = "knees,back",
            MealsPerDay              = MealsPerDay.FourToFive,
            DietaryStyle             = DietaryStyle.Standard,
            Allergies                = "lactose,nuts",
            DietRating               = 3,
            PlanExperience           = PlanExperience.TriedFailed,
            PastBlockers             = "time,motivation",
            PrimaryMotivation        = PrimaryMotivation.Health,
            DateCreated              = now,
            NutritionTargets = new ClientNutritionTargets
            {
                DerivedActivityLevel = ActivityLevel.ModeratelyActive,
                DerivedNutritionGoal = NutritionGoal.Cut,
                Bmr                  = 1788m,
                Tdee                 = 2771m,
                AdjustedKcal         = 2217m,
                ProteinGrams         = 161m,
                CarbsGrams           = 238m,
                FatGrams             = 69m,
                MealDistribution     = "{\"breakfast\":25,\"snack1\":10,\"lunch\":30,\"snack2\":10,\"dinner\":25}",
                DateCreated          = now,
            },
        });
        await db.SaveChangesAsync();

        logger.LogInformation("QA client onboarding data created for clientProfileId={ClientProfileId}", clientProfile.Id);
    }

    /// <summary>
    /// #1303 — eight weekly body measurements for qa.client, the newest dated yesterday, with
    /// weight, body fat and waist trending down toward the 76 kg target.
    /// </summary>
    private static async Task EnsureBodyMeasurementsAsync(
        ApplicationDbContext db,
        ClientProfile clientProfile,
        ILogger logger,
        Guid[]? measurementIds = null,
        decimal[]? weightSeries = null)
    {
        var ids = measurementIds ?? QaBodyMeasurementIds;

        var existingIds = (await db.BodyMeasurements
            .Where(m => ids.Contains(m.PublicId))
            .Select(m => m.PublicId)
            .ToListAsync())
            .ToHashSet();

        if (existingIds.Count == ids.Length)
        {
            logger.LogInformation("QA body measurements already present ({Count}), skipping.", existingIds.Count);
            return;
        }

        var now = DateTime.UtcNow;
        // Oldest first: index 0 is seven weeks before the newest row.
        decimal[] weightKg = weightSeries ?? [84.0m, 83.4m, 82.9m, 82.1m, 81.8m, 81.2m, 80.9m, 80.5m];
        decimal[] bodyFat  = [22.0m, 21.7m, 21.5m, 21.2m, 20.9m, 20.6m, 20.3m, 20.0m];
        decimal[] waistCm  = [94.0m, 93.2m, 92.6m, 91.8m, 91.1m, 90.4m, 89.9m, 89.5m];

        var rows = Enumerable.Range(0, ids.Length)
            .Where(index => !existingIds.Contains(ids[index]))
            .Select(index => new BodyMeasurement
            {
                PublicId          = ids[index],
                ClientProfileId   = clientProfile.Id,
                MeasuredAt        = now.Date.AddDays(-1 - 7 * (ids.Length - 1 - index)).AddHours(7),
                WeightKg          = weightKg[index],
                BodyFatPercentage = bodyFat[index],
                WaistCm           = waistCm[index],
                DateCreated       = now,
            })
            .ToList();

        db.BodyMeasurements.AddRange(rows);
        await db.SaveChangesAsync();

        logger.LogInformation("QA body measurements created: {Count} inserted.", rows.Count);
    }

    /// <summary>
    /// #1303 — 14 eaten-meal logs on the six days before today against the QA nutrition plan
    /// (3 planned meals per day, 18 planned), so the nutritionist's timeline shows "N of 3".
    /// Today stays empty so the streak does not depend on the time of day.
    /// </summary>
    private static async Task EnsureMealLogsAsync(
        IMongoContext mongo,
        ILogger logger,
        Guid? clientUserId = null,
        Guid? planExternalId = null,
        string mealIdPrefix = DefaultMealIdPrefix,
        int[]? loggedPerDay = null)
    {
        var logClientId = clientUserId ?? ClientUserId;
        var logPlanId = planExternalId ?? QaNutritionPlanExternalId;
        var now = DateTime.UtcNow;
        var planStart = MondayOfWeek(now).AddDays(-7);
        int[] mealsLoggedPerDay = loggedPerDay ?? [3, 3, 2, 2, 2, 2];
        int[] mealHours = [8, 12, 18];

        var existingLogs = await mongo.MealLogs
            .Find(l => l.ClientId == logClientId && l.PlanId == logPlanId)
            .ToListAsync();
        var existingKeys = existingLogs.Select(l => (l.MealId, l.LogDate.Date)).ToHashSet();

        var logs = new List<MealLog>();

        for (var daysAgo = 1; daysAgo <= mealsLoggedPerDay.Length; daysAgo++)
        {
            var day = now.Date.AddDays(-daysAgo);
            var daysSinceStart = (int)(day - planStart).TotalDays;
            var weekNumber = daysSinceStart / 7 + 1;
            var dayOfWeek = daysSinceStart % 7 + 1;

            for (var meal = 1; meal <= mealsLoggedPerDay[daysAgo - 1]; meal++)
            {
                var mealId = PlannedMealId(weekNumber, dayOfWeek, meal, mealIdPrefix);

                if (existingKeys.Contains((mealId, day)))
                {
                    continue;
                }

                logs.Add(new MealLog
                {
                    ClientId = logClientId,
                    PlanId   = logPlanId,
                    MealId   = mealId,
                    LogDate  = day,
                    EatenAt  = day.AddHours(mealHours[meal - 1]).AddMinutes(10),
                });
            }
        }

        if (logs.Count == 0)
        {
            logger.LogInformation("QA meal logs already present, skipping.");
            return;
        }

        await mongo.MealLogs.InsertManyAsync(logs);
        logger.LogInformation("QA meal logs created: {Count} inserted.", logs.Count);
    }

    /// <summary>
    /// #1303 — one personal record (QA Squat 85 kg x 8) pointing at the completed main-plan
    /// execution, which already logs that exact set. The unique (workoutLogId, exercise, set)
    /// index makes a second insert fail, hence the ExternalId check first.
    /// </summary>
    private static async Task EnsurePersonalRecordAsync(IMongoContext mongo, ILogger logger)
    {
        var existing = await mongo.PersonalRecords
            .Find(r => r.ExternalId == QaPersonalRecordExternalId)
            .FirstOrDefaultAsync();

        if (existing is not null)
        {
            logger.LogInformation("QA personal record already present.");
            return;
        }

        var now = DateTime.UtcNow;

        await mongo.PersonalRecords.InsertOneAsync(new PersonalRecord
        {
            ExternalId         = QaPersonalRecordExternalId,
            ClientId           = ClientUserId,
            ExerciseExternalId = StandardExercise1Id,
            ExerciseName       = "QA Squat",
            WeightKg           = 85m,
            Reps               = 8,
            AchievedAt         = now.Date.AddDays(-3).AddHours(10).AddMinutes(10),
            WorkoutLogId       = QaMainPlanCompletedExecutionId,
            SetNumber          = 1,
            DateCreated        = now,
        });

        logger.LogInformation("QA personal record created: externalId={ExternalId}", QaPersonalRecordExternalId);
    }

    /// <summary>
    /// #1303 — one completed execution of the main plan's Monday session (<see cref="QaSessionId"/>)
    /// dated this Monday, so "sessions this week" and training compliance both count it (compliance
    /// only matches an execution dated on the day its session is scheduled). On a Thursday reset the
    /// main execution (3 days ago) is already this exact row, so the skip filter keeps the insert a no-op.
    /// </summary>
    private static async Task EnsureMondaySessionExecutionAsync(IMongoContext mongo, ILogger logger)
    {
        var now = DateTime.UtcNow;
        var monday = MondayOfWeek(now);

        var sameSessionAndDay = Builders<SessionExecution>.Filter.Eq(e => e.ClientId, ClientUserId)
            & Builders<SessionExecution>.Filter.Eq(e => e.SessionId, QaSessionId)
            & Builders<SessionExecution>.Filter.Eq(e => e.Date, monday);
        var sameId = Builders<SessionExecution>.Filter.Eq(e => e.ExternalId, QaMondaySessionExecutionId);

        var existing = await mongo.SessionExecutions
            .Find(sameId | sameSessionAndDay)
            .FirstOrDefaultAsync();

        if (existing is not null)
        {
            logger.LogInformation("QA Monday SessionExecution already present: externalId={ExternalId}", QaMondaySessionExecutionId);
            return;
        }

        var completedAt = monday.AddHours(18) < now ? monday.AddHours(18) : now;

        await mongo.SessionExecutions.InsertOneAsync(new SessionExecution
        {
            ExternalId  = QaMondaySessionExecutionId,
            ClientId    = ClientUserId,
            PlanId      = QaTrainingPlanExternalId,
            SessionId   = QaSessionId,
            Date        = SessionExecution.ToCompletionDateUtc(monday),
            Status      = SessionExecutionStatus.Completed,
            DateCreated = completedAt,
            DateUpdated = completedAt,
        });

        logger.LogInformation("QA Monday SessionExecution created: externalId={ExternalId}", QaMondaySessionExecutionId);
    }

    /// <summary>
    /// #1311 — qa.coach's one-week training plan for QA Duo: three sessions this week. Session A
    /// sits on the seed-time weekday (so its completed execution can be dated today); B and C sit
    /// two and four weekdays later, wrapping inside the Monday-Sunday week.
    /// </summary>
    private static async Task EnsureDuoTrainingPlanAsync(IMongoContext mongo, ILogger logger)
    {
        var existing = await mongo.TrainingPlans
            .Find(p => p.ExternalId == QaDuoTrainingPlanExternalId)
            .FirstOrDefaultAsync();

        if (existing is not null)
        {
            logger.LogInformation("QA Duo TrainingPlan already present: externalId={ExternalId}", QaDuoTrainingPlanExternalId);
            return;
        }

        var now = DateTime.UtcNow;
        var todayDayOfWeek = (int)now.DayOfWeek == 0 ? 7 : (int)now.DayOfWeek;
        int WeekdayOffsetBy(int days) => (todayDayOfWeek - 1 + days) % 7 + 1;

        SessionExercise Exercise(Guid instanceId, Guid catalogId, string name, int order, params ExerciseSet[] sets) => new()
        {
            ExerciseId         = instanceId,
            ExerciseExternalId = catalogId,
            ExerciseName       = name,
            Order              = order,
            MovementType       = MovementType.Reps,
            Sets               = [.. sets],
        };

        var sessionsByDay = new Dictionary<int, List<TrainingSession>>
        {
            [WeekdayOffsetBy(0)] =
            [
                new TrainingSession
                {
                    SessionId = QaDuoSessionAId,
                    Name      = "QA Duo Strength A",
                    Order     = 1,
                    Workouts  = [],
                    StandaloneExercises =
                    [
                        Exercise(QaDuoSquatInstanceId, StandardExercise1Id, "QA Squat", 1,
                            new ExerciseSet { SetNumber = 1, Type = SetType.Normal, Reps = 8, WeightKg = 70m },
                            new ExerciseSet { SetNumber = 2, Type = SetType.Normal, Reps = 8, WeightKg = 70m }),
                        Exercise(QaDuoDeadliftInstanceId, StandardExercise2Id, "QA Deadlift", 2,
                            new ExerciseSet { SetNumber = 1, Type = SetType.Normal, Reps = 5, WeightKg = 90m }),
                    ],
                },
            ],
            [WeekdayOffsetBy(2)] =
            [
                new TrainingSession
                {
                    SessionId = QaDuoSessionBId,
                    Name      = "QA Duo Core B",
                    Order     = 1,
                    Workouts  = [],
                    StandaloneExercises = [Exercise(QaDuoPlankInstanceId, QaStandaloneOnlyExerciseId, "QA Plank", 1)],
                },
            ],
            [WeekdayOffsetBy(4)] =
            [
                new TrainingSession
                {
                    SessionId = QaDuoSessionCId,
                    Name      = "QA Duo Pull C",
                    Order     = 1,
                    Workouts  = [],
                    StandaloneExercises = [Exercise(QaDuoPullUpInstanceId, AmrapExercise1Id, "QA Pull-up", 1)],
                },
            ],
        };

        await mongo.TrainingPlans.InsertOneAsync(new TrainingPlan
        {
            ExternalId  = QaDuoTrainingPlanExternalId,
            ClientId    = ClientDuoUserId,
            TrainerId   = CoachUserId,
            Name        = "QA Duo Training Plan",
            Status      = TrainingPlanStatus.Active,
            StartDate   = MondayOfWeek(now),
            DateCreated = now,
            Version     = 1,
            Weeks =
            [
                new TrainingWeek
                {
                    WeekNumber    = 1,
                    Status        = WeekStatus.Published,
                    DatePublished = now,
                    Days          = BuildTrainingDays(sessionsByDay),
                },
            ],
        });

        logger.LogInformation("QA Duo TrainingPlan created: externalId={ExternalId}", QaDuoTrainingPlanExternalId);
    }

    /// <summary>
    /// #1311 — instant at or before now on today's date: 06:00 UTC, or now when the seed runs
    /// earlier. Keeps the Duo execution and PR dated today without ever being in the future.
    /// </summary>
    private static DateTime TodayCompletionInstant(DateTime now) =>
        now.Date.AddHours(6) < now ? now.Date.AddHours(6) : now;

    /// <summary>
    /// #1311 — one completed execution of Duo session A dated today. The session sits on today's
    /// weekday of the current plan week, so "sessions this week" reads 1 of 3 on any reset day.
    /// </summary>
    private static async Task EnsureDuoSessionExecutionAsync(IMongoContext mongo, ILogger logger)
    {
        var existing = await mongo.SessionExecutions
            .Find(e => e.ExternalId == QaDuoSessionExecutionId)
            .FirstOrDefaultAsync();

        if (existing is not null)
        {
            logger.LogInformation("QA Duo SessionExecution already present: externalId={ExternalId}", QaDuoSessionExecutionId);
            return;
        }

        var now = DateTime.UtcNow;
        var completedAt = TodayCompletionInstant(now);

        await mongo.SessionExecutions.InsertOneAsync(new SessionExecution
        {
            ExternalId  = QaDuoSessionExecutionId,
            ClientId    = ClientDuoUserId,
            PlanId      = QaDuoTrainingPlanExternalId,
            SessionId   = QaDuoSessionAId,
            Date        = SessionExecution.ToCompletionDateUtc(now.Date),
            Status      = SessionExecutionStatus.Completed,
            DateCreated = completedAt,
            DateUpdated = completedAt,
            CompletedExerciseInstanceIds = [QaDuoSquatInstanceId, QaDuoDeadliftInstanceId],
        });

        logger.LogInformation("QA Duo SessionExecution created: externalId={ExternalId}", QaDuoSessionExecutionId);
    }

    /// <summary>
    /// #1311 — one personal record (QA Squat 72.5 kg x 8) on the Duo execution, dated today so
    /// "PRs this month" never reads 0 on the first of the month. The unique (workoutLogId,
    /// exercise, set) index makes a second insert fail, hence the ExternalId check first.
    /// </summary>
    private static async Task EnsureDuoPersonalRecordAsync(IMongoContext mongo, ILogger logger)
    {
        var existing = await mongo.PersonalRecords
            .Find(r => r.ExternalId == QaDuoPersonalRecordExternalId)
            .FirstOrDefaultAsync();

        if (existing is not null)
        {
            logger.LogInformation("QA Duo personal record already present.");
            return;
        }

        var now = DateTime.UtcNow;

        await mongo.PersonalRecords.InsertOneAsync(new PersonalRecord
        {
            ExternalId         = QaDuoPersonalRecordExternalId,
            ClientId           = ClientDuoUserId,
            ExerciseExternalId = StandardExercise1Id,
            ExerciseName       = "QA Squat",
            WeightKg           = 72.5m,
            Reps               = 8,
            AchievedAt         = TodayCompletionInstant(now),
            WorkoutLogId       = QaDuoSessionExecutionId,
            SetNumber          = 1,
            DateCreated        = now,
        });

        logger.LogInformation("QA Duo personal record created: externalId={ExternalId}", QaDuoPersonalRecordExternalId);
    }
}
