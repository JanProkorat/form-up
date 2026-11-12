using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Features.Trainers.GetClientDashboard;

/// <summary>
/// Response model containing a client's dashboard summary for a trainer.
/// </summary>
public class GetClientDashboardResponse
{
    /// <summary>
    /// Internal integer primary key of the ClientProfessionalLink row.
    /// Used to populate <c>linkId</c> on the photo-diary-request create form.
    /// </summary>
    public long LinkId { get; set; }

    /// <summary>
    /// The client profile's public ID.
    /// </summary>
    public Guid ClientPublicId { get; set; }

    /// <summary>
    /// The client's <c>ApplicationUser.Id</c> (= <c>ClientProfile.UserId</c>).
    /// This is the identifier used by the weekly-check-in and schedule-override
    /// endpoints — it differs from <see cref="ClientPublicId"/>.
    /// </summary>
    public Guid ClientUserId { get; set; }

    /// <summary>
    /// The client's email address.
    /// </summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// The client's first name.
    /// </summary>
    public string FirstName { get; set; } = string.Empty;

    /// <summary>
    /// The client's last name.
    /// </summary>
    public string LastName { get; set; } = string.Empty;

    /// <summary>
    /// The client's date of birth.
    /// </summary>
    public DateTime? DateOfBirth { get; set; }

    /// <summary>
    /// The client's height in centimeters.
    /// </summary>
    public decimal? HeightCm { get; set; }

    /// <summary>
    /// The client's current weight in kilograms.
    /// </summary>
    public decimal? WeightKg { get; set; }

    /// <summary>
    /// The client's fitness or health goals.
    /// </summary>
    public string? Goals { get; set; }

    /// <summary>
    /// Date when the trainer-client relationship was established.
    /// </summary>
    public DateTime LinkedAt { get; set; }

    /// <summary>
    /// Whether the trainer-client relationship is currently active.
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// True when the client is outside the plan in force (over its client cap): visible to the
    /// coach but not workable. Same source as the CLIENT_OVER_PLAN_LIMIT gate.
    /// </summary>
    public bool IsOutsidePlan { get; set; }

    /// <summary>
    /// Derived Active/Paused/Archived status — computed by
    /// <see cref="Domain.Services.ClientStatusClassifier.Classify"/>, the same derivation
    /// <c>GetClientsEndpoint</c> uses, so this value always agrees with the caller's clients-list
    /// status pill for the same client (#1094).
    /// </summary>
    public ClientListStatus Status { get; set; }

    /// <summary>
    /// Whether this professional is permitted to view the client's nutrition plans.
    /// Mirrors <c>ClientProfessionalLink.CanViewNutritionPlans</c>.
    /// </summary>
    public bool CanViewNutritionPlans { get; set; }

    /// <summary>
    /// Whether this professional is permitted to view the client's training plans.
    /// Mirrors <c>ClientProfessionalLink.CanViewTrainingPlans</c>.
    /// </summary>
    public bool CanViewTrainingPlans { get; set; }

    /// <summary>
    /// Whether the client has registered an account (email confirmed).
    /// </summary>
    public bool HasRegistered { get; set; }

    /// <summary>
    /// Status of the client's questionnaire: "none", "pending", or "submitted".
    /// </summary>
    public string QuestionnaireStatus { get; set; } = "none";

    /// <summary>
    /// Title of the pending or submitted questionnaire, if any.
    /// </summary>
    public string? QuestionnaireTitle { get; set; }

    /// <summary>
    /// PublicId of the questionnaire response, if any.
    /// </summary>
    public Guid? QuestionnaireResponsePublicId { get; set; }

    /// <summary>
    /// When the questionnaire was submitted, if submitted.
    /// </summary>
    public DateTime? QuestionnaireSubmittedAt { get; set; }

    /// <summary>
    /// Total number of body measurements recorded for the client.
    /// </summary>
    public int TotalMeasurements { get; set; }

    /// <summary>
    /// Total number of progress photos uploaded for the client.
    /// </summary>
    public int TotalProgressPhotos { get; set; }

    /// <summary>
    /// The most recent body measurement, or null if none exist.
    /// </summary>
    public LatestMeasurementDto? LatestMeasurement { get; set; }

    /// <summary>
    /// Compliance percentage for the last 7 days (0-100), or null if no active nutrition plan exists.
    /// </summary>
    public decimal? CompliancePercent { get; set; }

    /// <summary>
    /// Current streak of consecutive compliant days.
    /// </summary>
    public int CurrentStreak { get; set; }

    /// <summary>
    /// Meal-only compliance percentage for the last 7 days (0-100). Null when the link lacks nutrition
    /// access or the compliance computation failed; 0 (not null) when there is no nutrition plan.
    /// </summary>
    public decimal? NutritionCompliancePercent { get; set; }

    /// <summary>
    /// Training-only compliance percentage for the last 7 days (0-100). Null when the link lacks
    /// training access or the compliance computation failed.
    /// </summary>
    public decimal? TrainingCompliancePercent { get; set; }

    /// <summary>
    /// Current streak of consecutive days with planned meals on which at least one meal was logged;
    /// days with nothing planned are skipped. Null when the link lacks nutrition access or the
    /// compliance computation failed.
    /// </summary>
    public int? NutritionStreak { get; set; }

    /// <summary>
    /// Current streak of consecutive days with planned sessions on which at least one session was
    /// completed; days with nothing planned are skipped. Null when the link lacks training access or
    /// the compliance computation failed.
    /// </summary>
    public int? TrainingStreak { get; set; }

    /// <summary>
    /// Planned sessions the client has fully completed in the current plan week (a session counts
    /// once however many days it was executed). Null when the caller has no training-plan access,
    /// or there is no active plan, current week or published schedule to count against. Never
    /// exceeds <see cref="SessionsPlannedThisWeek"/>.
    /// </summary>
    public int? SessionsCompletedThisWeek { get; set; }

    /// <summary>
    /// Sessions scheduled in the current plan week, counted by the same rule as
    /// <see cref="SessionsCompletedThisWeek"/>. Null in exactly the same cases.
    /// </summary>
    public int? SessionsPlannedThisWeek { get; set; }

    /// <summary>
    /// Client's onboarding questionnaire data, or null if not completed.
    /// </summary>
    public OnboardingDataDto? Onboarding { get; set; }
}

/// <summary>
/// Client onboarding questionnaire data summary.
/// </summary>
public class OnboardingDataDto
{
    /// <summary>Biological sex.</summary>
    public string? Sex { get; set; }
    /// <summary>Target weight in kg.</summary>
    public decimal? TargetWeightKg { get; set; }
    /// <summary>Body type / somatotype.</summary>
    public string? BodyType { get; set; }
    /// <summary>Primary fitness goal.</summary>
    public string? PrimaryGoal { get; set; }
    /// <summary>Desired time horizon.</summary>
    public string? TimeHorizon { get; set; }
    /// <summary>Job/activity type.</summary>
    public string? JobType { get; set; }
    /// <summary>Sleep hours per night.</summary>
    public int? SleepHours { get; set; }
    /// <summary>Stress level (1-5).</summary>
    public int? StressLevel { get; set; }
    /// <summary>Current training frequency.</summary>
    public string? CurrentTrainingFrequency { get; set; }
    /// <summary>Desired training frequency.</summary>
    public string? DesiredTrainingFrequency { get; set; }
    /// <summary>Self-rated fitness (1-10).</summary>
    public int? FitnessRating { get; set; }
    /// <summary>Preferred activities (comma-separated).</summary>
    public string? PreferredActivities { get; set; }
    /// <summary>Injuries/limitations (comma-separated).</summary>
    public string? Injuries { get; set; }
    /// <summary>Meals per day.</summary>
    public string? MealsPerDay { get; set; }
    /// <summary>Dietary style.</summary>
    public string? DietaryStyle { get; set; }
    /// <summary>Allergies (comma-separated).</summary>
    public string? Allergies { get; set; }
    /// <summary>Plan experience.</summary>
    public string? PlanExperience { get; set; }
    /// <summary>Past blockers (comma-separated).</summary>
    public string? PastBlockers { get; set; }
    /// <summary>Primary motivation.</summary>
    public string? PrimaryMotivation { get; set; }
    /// <summary>Derived activity level.</summary>
    public string? DerivedActivityLevel { get; set; }
    /// <summary>Derived nutrition goal.</summary>
    public string? DerivedNutritionGoal { get; set; }
    /// <summary>BMR in kcal/day.</summary>
    public decimal? Bmr { get; set; }
    /// <summary>TDEE in kcal/day.</summary>
    public decimal? Tdee { get; set; }
    /// <summary>Adjusted daily calories.</summary>
    public decimal? AdjustedKcal { get; set; }
    /// <summary>Daily protein target in grams.</summary>
    public decimal? ProteinGrams { get; set; }
    /// <summary>Daily carbs target in grams.</summary>
    public decimal? CarbsGrams { get; set; }
    /// <summary>Daily fat target in grams.</summary>
    public decimal? FatGrams { get; set; }
    /// <summary>Meal distribution percentages as JSON.</summary>
    public string? MealDistribution { get; set; }
    /// <summary>Foods the client dislikes. Null when the caller cannot view nutrition plans.</summary>
    public List<string>? Dislikes { get; set; }
    /// <summary>Eating habits. Null when the caller cannot view nutrition plans.</summary>
    public List<string>? Habits { get; set; }
    /// <summary>Daily fibre target in grams. Null when unset or the caller cannot view nutrition plans.</summary>
    public decimal? FibreGrams { get; set; }
    /// <summary>
    /// Later of the onboarding and nutrition-target update dates (creation date if never updated).
    /// Target dates count only when the caller can view nutrition plans.
    /// </summary>
    public DateTime? DateUpdated { get; set; }
}

/// <summary>
/// Summary of the most recent body measurement for a client.
/// </summary>
public class LatestMeasurementDto
{
    /// <summary>
    /// Date and time when the measurement was taken.
    /// </summary>
    public DateTime MeasuredAt { get; set; }

    /// <summary>
    /// Weight in kilograms.
    /// </summary>
    public decimal? WeightKg { get; set; }

    /// <summary>
    /// Body fat percentage.
    /// </summary>
    public decimal? BodyFatPercentage { get; set; }
}
