using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Features.ClientTraining.GetFullPlan;

/// <summary>
/// Full training plan response for the client mobile view.
/// Contains all published weeks enriched with completion state and muscle group data.
/// </summary>
public class GetFullTrainingPlanResponse
{
    /// <summary>External identifier of the training plan.</summary>
    public Guid PlanId { get; set; }

    /// <summary>Display name of the training plan.</summary>
    public string PlanName { get; set; } = "";

    /// <summary>Plan status (Draft, Active, Completed, Archived).</summary>
    public string Status { get; set; } = "";

    /// <summary>The Monday when Week 1 begins, if set.</summary>
    public DateTime? StartDate { get; set; }

    /// <summary>Current week number (null if plan is upcoming).</summary>
    public int? CurrentWeek { get; set; }

    /// <summary>Total number of weeks in the plan (including draft).</summary>
    public int TotalWeeks { get; set; }

    /// <summary>Number of published weeks.</summary>
    public int PublishedWeekCount { get; set; }

    /// <summary>Linked questionnaire response public ID, if any.</summary>
    public Guid? QuestionnaireResponseId { get; set; }

    /// <summary>When this plan was marked as completed, if applicable.</summary>
    public DateTime? DateCompleted { get; set; }

    /// <summary>
    /// Last day of the plan's last published week, set only when the plan's coach no longer has a plan that covers
    /// this client: after it the plan is read-only. Null otherwise.
    /// </summary>
    public DateOnly? RunsUntil { get; set; }

    /// <summary>Published weeks with sessions and completion state.</summary>
    public List<WeekDto> Weeks { get; set; } = [];
}

/// <summary>
/// A published week within the training plan.
/// </summary>
public class WeekDto
{
    /// <summary>1-based week number.</summary>
    public int WeekNumber { get; set; }

    /// <summary>Week publish status (Draft, Published).</summary>
    public string Status { get; set; } = "";

    /// <summary>When this week was published.</summary>
    public DateTime? DatePublished { get; set; }

    /// <summary>
    /// Start date (Monday) of this week, computed from plan.StartDate + (weekNumber-1)*7 days.
    /// Null when no anchor date is available.
    /// </summary>
    public DateTime? WeekStartDate { get; set; }

    /// <summary>
    /// End date (Sunday) of this week. Null when no anchor date is available.
    /// </summary>
    public DateTime? WeekEndDate { get; set; }

    /// <summary>Optional day-level notes keyed by day of week (1=Monday … 7=Sunday).</summary>
    public Dictionary<int, string> DayNotes { get; set; } = new();

    /// <summary>Sessions in this week.</summary>
    public List<SessionDto> Sessions { get; set; } = [];
}

/// <summary>
/// A single training session within a week.
/// </summary>
public class SessionDto
{
    /// <summary>Unique session identifier.</summary>
    public Guid SessionId { get; set; }

    /// <summary>Day of the week (1 = Monday, 7 = Sunday).</summary>
    public int DayOfWeek { get; set; }

    /// <summary>Display name of the session (e.g. "Push Day").</summary>
    public string Name { get; set; } = "";

    /// <summary>Display order within the day.</summary>
    public int Order { get; set; }

    /// <summary>Optional coach notes for this session.</summary>
    public string? Notes { get; set; }

    /// <summary>Number of exercises where all planned sets are completed.</summary>
    public int CompletedExerciseCount { get; set; }

    /// <summary>Total number of exercises in this session.</summary>
    public int TotalExerciseCount { get; set; }

    /// <summary>
    /// Estimated session duration in minutes.
    /// Currently null — a reliable heuristic requires product-defined set-duration
    /// assumptions that haven't been finalised. Will be added as an additive change.
    /// </summary>
    public int? EstimatedDurationMinutes { get; set; }

    /// <summary>
    /// Workouts in this session, ordered by their Order field. Every document is built directly
    /// from <see cref="Workouts"/> and <see cref="StandaloneExercises"/> — there is no legacy
    /// flat-exercises shape and no read-time backfill (#857).
    /// </summary>
    public List<WorkoutDto> Workouts { get; set; } = [];

    /// <summary>
    /// Read-only flat union of every exercise in this session — standalone exercises plus every
    /// workout's nested exercises — ordered by the ONE shared <c>Order</c> sequence workouts and
    /// standalone exercises occupy within a session (see <c>UpdateTrainingPlanValidator</c>'s
    /// cross-list duplicate-Order check). Computed on read only; there is no corresponding member
    /// on the write side, so this field is ignored (not rejected) if present in a PUT body (#874).
    /// </summary>
    public List<ExerciseDto> AllExercises { get; set; } = [];

    /// <summary>
    /// Standalone exercises programmed directly on this session — not grouped under any
    /// <see cref="WorkoutDto"/> (#857 phase 3a). Also included (in shared-Order position) in the
    /// flat <see cref="AllExercises"/> view above and counted in <see cref="TotalExerciseCount"/> /
    /// <see cref="CompletedExerciseCount"/>.
    /// </summary>
    public List<ExerciseDto> StandaloneExercises { get; set; } = [];

    /// <summary>
    /// Current lock state of this session.
    /// Possible values: "Stable" (no active lock), "Editing" (trainer holds an editing lock),
    /// "Live" (client has an in-progress workout lock).
    /// Populated via a batch <c>GetStateAsync</c> call on the session lock service.
    /// </summary>
    public string LockState { get; set; } = "Stable";

    /// <summary>
    /// Who currently holds the lock, if any.
    /// Possible values: "Coach", "Client", or null when the session is Stable.
    /// </summary>
    public string? LockHolder { get; set; }

    /// <summary>
    /// True when at least one exercise in this session has HasModifications == true.
    /// Always false when no workout log exists for this session.
    /// </summary>
    public bool HasModifications { get; set; }
}

/// <summary>
/// An ordered section within a session (e.g. "Hlavní", "Warm-up", "Cool-down").
/// </summary>
public class WorkoutDto
{
    /// <summary>Stable identifier for this workout.</summary>
    public Guid WorkoutId { get; set; }

    /// <summary>Display order within the session (0-based).</summary>
    public int Order { get; set; }

    /// <summary>Display name (e.g. "Hlavní", "Warm-up").</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Workout format for this workout (e.g. "Emom", "Amrap", "Tabata", "ForTime").
    /// Null means the workout uses the default Standard format.
    /// </summary>
    public string? Format { get; set; }

    /// <summary>
    /// Full format-configuration object for the workout (rounds, intervals, work/rest timings).
    /// Null when Format is null or Standard.
    /// Mirrors <see cref="FormUp.Application.Domain.Documents.WodConfig"/> on TrainingWorkout.
    /// </summary>
    public WodConfig? FormatConfig { get; set; }

    /// <summary>
    /// Optional coach note for this workout.
    /// Mirrors the Notes property on TrainingWorkout.
    /// </summary>
    public string? Notes { get; set; }

    /// <summary>True when this workout is considered complete:
    /// for workouts with exercises → every exercise has IsCompleted=true;
    /// for workouts without exercises → the workout's id is in the
    /// TrainingCompletion.CompletedSectionIds set for the owning session.</summary>
    public bool IsCompleted { get; set; }

    /// <summary>Exercises within this workout.</summary>
    public List<ExerciseDto> Exercises { get; set; } = [];
}

/// <summary>
/// An exercise within a session, enriched with muscle groups and completion state.
/// </summary>
public class ExerciseDto
{
    /// <summary>
    /// Instance identifier for this specific exercise entry within its session — the id the
    /// client-facing mark-complete/incomplete routes require (#857 phase 3b). Distinguishes two
    /// occurrences of the same catalog exercise (<see cref="ExerciseExternalId"/>) programmed
    /// twice in one workout, or once standalone and once nested in a workout of the same
    /// session. Mirrors <see cref="FormUp.Application.Domain.Documents.SessionExercise.ExerciseId"/>.
    /// </summary>
    public Guid ExerciseId { get; set; }

    /// <summary>Reference to the exercise document's ExternalId — used for exercise metadata lookups.</summary>
    public Guid ExerciseExternalId { get; set; }

    /// <summary>Snapshot exercise name.</summary>
    public string ExerciseName { get; set; } = "";

    /// <summary>Display order within the session.</summary>
    public int Order { get; set; }

    /// <summary>Optional coach notes for this exercise.</summary>
    public string? Notes { get; set; }

    /// <summary>Rest time between sets in seconds.</summary>
    public int? RestSeconds { get; set; }

    /// <summary>
    /// Movement type — drives which set field the prescription uses
    /// (reps / duration / distance / reps-for-time). Required for the
    /// client to render the correct summary string. Serialised as the
    /// enum's string name (e.g. "Reps", "Time", "Distance",
    /// "RepsForTime"); the client casts to its `MovementType` enum.
    /// Defaults to "Reps" when the underlying exercise carries no
    /// explicit value.
    /// </summary>
    public string MovementType { get; set; } = "Reps";

    /// <summary>
    /// Target muscle groups fetched from the Exercise document.
    /// Empty list when the exercise no longer exists in the database.
    /// </summary>
    public List<MuscleGroup> MuscleGroups { get; set; } = [];

    /// <summary>
    /// True when this specific exercise instance (<see cref="ExerciseId"/>) was marked complete
    /// directly (checkbox/instance-keyed completion), OR every planned set has a completed
    /// workout log entry. The instance-keyed check is what lets a set-less exercise ever report
    /// complete, and what lets two placements of the same catalog exercise
    /// (<see cref="ExerciseExternalId"/>) in one session be completed independently (#877).
    /// </summary>
    public bool IsCompleted { get; set; }

    /// <summary>
    /// True when at least one set under this exercise has IsModified == true.
    /// Always false when no workout log exists for this exercise.
    /// </summary>
    public bool HasModifications { get; set; }

    /// <summary>Planned sets with per-set completion timestamps and actual-vs-planned delta.</summary>
    public List<SetDto> Sets { get; set; } = [];
}

/// <summary>
/// A planned set with its completion state and actual-vs-planned delta derived from workout logs.
/// </summary>
public class SetDto
{
    /// <summary>1-based set number within the exercise.</summary>
    public int SetNumber { get; set; }

    /// <summary>Set type (Normal, Warmup, Dropset, Superset).</summary>
    public string Type { get; set; } = "";

    /// <summary>Target number of repetitions (from the plan prescription).</summary>
    public int? Reps { get; set; }

    /// <summary>Target weight in kilograms (from the plan prescription).</summary>
    public decimal? WeightKg { get; set; }

    /// <summary>Target duration in seconds (from the plan prescription).</summary>
    public int? DurationSeconds { get; set; }

    /// <summary>Target distance in meters (from the plan prescription).</summary>
    public decimal? DistanceMeters { get; set; }

    /// <summary>Rest time after this set in seconds.</summary>
    public int? RestSeconds { get; set; }

    /// <summary>When this set was completed, or null if not yet done.</summary>
    public DateTime? CompletedAt { get; set; }

    // ── Actual logged values (from WorkoutLog) ──────────────────────────────────
    // Null when no log exists for this set (i.e. set not yet performed).

    /// <summary>Actual repetitions logged. Null when not yet performed.</summary>
    public int? ActualReps { get; set; }

    /// <summary>Actual weight (kg) logged. Null when not yet performed.</summary>
    public decimal? ActualWeightKg { get; set; }

    /// <summary>Actual RPE logged. Null when not yet performed.</summary>
    public decimal? ActualRpe { get; set; }

    /// <summary>Actual duration (seconds) logged. Null when not yet performed.</summary>
    public int? ActualDurationSeconds { get; set; }

    /// <summary>Actual distance (meters) logged. Null when not yet performed.</summary>
    public decimal? ActualDistanceMeters { get; set; }

    // ── Snapshot-planned values (frozen on WorkoutLog at log time) ──────────────
    // Null on legacy logs that pre-date snapshot storage — treat as planned == actual.

    /// <summary>Snapshot-planned repetitions at log time. Null for legacy logs.</summary>
    public int? PlannedReps { get; set; }

    /// <summary>Snapshot-planned weight (kg) at log time. Null for legacy logs.</summary>
    public decimal? PlannedWeightKg { get; set; }

    /// <summary>Snapshot-planned RPE at log time. Null for legacy logs.</summary>
    public decimal? PlannedRpe { get; set; }

    /// <summary>Snapshot-planned duration (seconds) at log time. Null for legacy logs.</summary>
    public int? PlannedDurationSeconds { get; set; }

    /// <summary>Snapshot-planned distance (meters) at log time. Null for legacy logs.</summary>
    public decimal? PlannedDistanceMeters { get; set; }

    /// <summary>
    /// Backend-computed flag: true when any actual field differs from its snapshot-planned
    /// counterpart. Always false for legacy sets (no snapshot → treated as planned == actual).
    /// </summary>
    public bool IsModified { get; set; }
}
