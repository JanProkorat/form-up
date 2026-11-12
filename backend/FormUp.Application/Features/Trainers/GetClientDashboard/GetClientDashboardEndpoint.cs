using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using Microsoft.EntityFrameworkCore;
using MongoDB.Driver;

namespace FormUp.Application.Features.Trainers.GetClientDashboard;

/// <summary>
/// Endpoint for retrieving a client's dashboard summary.
/// The requesting trainer must have an active link to the client.
/// </summary>
/// <param name="db">Database context.</param>
/// <param name="audit">Audit logging service.</param>
/// <param name="complianceService">Service for calculating compliance metrics.</param>
/// <param name="mongo">MongoDB context for reading active plan goal/macros.</param>
/// <param name="timeProvider">Clock abstraction — lets tests pin "now" deterministically.</param>
/// <param name="coachDiscipline">Narrows the link flags to the disciplines the caller holds a role for (active or removed but not yet swept).</param>
/// <param name="entitlementService">Tells whether the client is over the cap of the caller's plan in force.</param>
public class GetClientDashboardEndpoint(
    IApplicationDbContext db,
    IAuditService audit,
    IComplianceService complianceService,
    IMongoContext mongo,
    TimeProvider timeProvider,
    ICoachDisciplineAccess coachDiscipline,
    EntitlementService entitlementService)
    : Endpoint<GetClientDashboardRequest, GetClientDashboardResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/trainer/clients/{clientId}");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Get client dashboard";
            s.Description = "Returns a summary dashboard for a specific client managed by the authenticated trainer. "
                + "sessionsCompletedThisWeek / sessionsPlannedThisWeek count the client's training sessions in the current "
                + "plan week (week follows the client's local date) and are null without training-plan access or an active "
                + "plan whose current week is covered by a published week (an unpublished current week uses the latest earlier published one).";
            s.Response<GetClientDashboardResponse>(StatusCodes.Status200OK, "Client dashboard");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
            s.Response(StatusCodes.Status403Forbidden, "Active link grants neither nutrition nor training visibility");
            s.Response(StatusCodes.Status404NotFound, "Client unknown, or the caller has no active link to it");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(GetClientDashboardRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        // Find the trainer's profile
        var professionalProfile = await db.ProfessionalProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(tp => tp.UserId == Guid.Parse(userId), ct);

        if (professionalProfile is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Find the client profile by PublicId
        var clientProfile = await db.ClientProfiles
            .AsNoTracking()
            .Include(cp => cp.User)
            .Include(cp => cp.OnboardingData)
            .ThenInclude(od => od!.NutritionTargets)
            .FirstOrDefaultAsync(cp => cp.PublicId == req.ClientId, ct);

        if (clientProfile is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Verify an active trainer-client link exists
        var link = await db.ClientProfessionalLinks
            .AsNoTracking()
            .FirstOrDefaultAsync(ctl =>
                ctl.ProfessionalProfileId == professionalProfile.Id &&
                ctl.ClientProfileId == clientProfile.Id &&
                ctl.ConnectionState != ConnectionState.Ended, ct);

        if (link is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // A discipline is visible only when the link grants it AND the caller still holds that coach
        // role actively — a removed role stays in the JWT. Neither visible: deny outright (matches the
        // LinkCapabilities.GrantsNothing deny semantics from #903).
        var effective = await coachDiscipline.GetEffectiveAsync(
            Guid.Parse(userId), LinkCapabilities.FromLink(link), ct);

        if (effective.GrantsNothing)
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        // Count body measurements and progress photos
        var totalMeasurements = await db.BodyMeasurements
            .AsNoTracking()
            .CountAsync(bm => bm.ClientProfileId == clientProfile.Id, ct);

        var callerUserId = Guid.Parse(userId);
        var totalProgressPhotos = await db.PlanPhotos
            .AsNoTracking()
            .CountAsync(pp => pp.ClientProfileId == clientProfile.Id
                && pp.Category == PlanPhotoCategory.Body
                && db.PlanPhotoViewers.Any(v => v.PlanPhotoId == pp.Id && v.ProfessionalUserId == callerUserId), ct);

        // Get the latest body measurement
        var latestMeasurement = await db.BodyMeasurements
            .AsNoTracking()
            .Where(bm => bm.ClientProfileId == clientProfile.Id)
            .OrderByDescending(bm => bm.MeasuredAt)
            .Select(bm => new LatestMeasurementDto
            {
                MeasuredAt = bm.MeasuredAt,
                WeightKg = bm.WeightKg,
                BodyFatPercentage = bm.BodyFatPercentage
            })
            .FirstOrDefaultAsync(ct);

        // Calculate compliance data (last 7 days). Substitute the caller-visible value
        // into the existing wire fields rather than dropping them — a single-flag caller
        // gets their own domain's figure (CompliancePercent is the COMBINED weighted
        // figure per IComplianceService; returning it unfiltered to a single-flag caller
        // would leak the other domain's adherence by inference).
        decimal? compliancePercent = null;
        var currentStreak = 0;
        decimal? nutritionCompliancePercent = null;
        decimal? trainingCompliancePercent = null;
        int? nutritionStreak = null;
        int? trainingStreak = null;

        // Shared derivation — this endpoint was the only route deriving discipline from the link
        // rather than from global roles, and it is now one of several. Keeping the rule in
        // LinkCapabilities stops the copies drifting apart.
        var discipline = effective.Discipline;

        try
        {
            var complianceFrom = DateTime.UtcNow.Date.AddDays(-7);
            var complianceTo = DateTime.UtcNow.Date.AddDays(1).AddTicks(-1);
            var compliance = await complianceService.CalculateComplianceAsync(
                clientProfile.UserId, complianceFrom, complianceTo, ct, planAuthorUserId: professionalProfile.UserId);
            compliancePercent = discipline switch
            {
                ComplianceDiscipline.NutritionOnly => compliance.NutritionCompliancePercent,
                ComplianceDiscipline.TrainingOnly => compliance.TrainingCompliancePercent,
                _ => compliance.CompliancePercent
            };
            currentStreak = await complianceService.CalculateStreakAsync(
                clientProfile.UserId, discipline, ct, planAuthorUserId: professionalProfile.UserId);

            // Per-discipline figures are gated on the link capability, never on the value, so a
            // single-capability caller cannot infer the other discipline (#916). A single-capability
            // link already walked its own discipline above, so that streak is reused, not recomputed.
            if (effective.CanViewNutritionPlans)
            {
                nutritionCompliancePercent = compliance.NutritionCompliancePercent;
                nutritionStreak = discipline == ComplianceDiscipline.NutritionOnly
                    ? currentStreak
                    : await complianceService.CalculateStreakAsync(
                        clientProfile.UserId, ComplianceDiscipline.NutritionOnly, ct, planAuthorUserId: professionalProfile.UserId);
            }

            if (effective.CanViewTrainingPlans)
            {
                trainingCompliancePercent = compliance.TrainingCompliancePercent;
                trainingStreak = discipline == ComplianceDiscipline.TrainingOnly
                    ? currentStreak
                    : await complianceService.CalculateStreakAsync(
                        clientProfile.UserId, ComplianceDiscipline.TrainingOnly, ct, planAuthorUserId: professionalProfile.UserId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Compliance data is optional — may fail if no active nutrition plan exists
            Logger.LogWarning(ex, "Compliance computation failed for client {ClientPublicId}; returning null compliance", clientProfile.PublicId);
            nutritionCompliancePercent = null;
            trainingCompliancePercent = null;
            nutritionStreak = null;
            trainingStreak = null;
        }

        // Audit: trainer accessing client health data
        await audit.LogAsync(
            Guid.Parse(userId),
            "Read",
            nameof(ClientProfile),
            clientProfile.PublicId,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            ct: ct);

        // Determine questionnaire status
        var questionnaireStatus = "none";
        string? questionnaireTitle = null;
        Guid? questionnaireResponsePublicId = null;

        // A link with an activation time only counts answers from the current collaboration, so a
        // returning client who has not re-onboarded shows no response. A legacy link (no activation
        // time) keeps the coach-wide lookup.
        var activatedAtUtc = link.ActivatedAtUtc;

        var qResponse = await db.QuestionnaireResponses
            .AsNoTracking()
            .Include(r => r.Questionnaire)
            .Where(r => r.ClientId == clientProfile.UserId
                     && r.ProfessionalId == Guid.Parse(userId)
                     && r.Status != Domain.Enums.QuestionnaireResponseStatus.Cancelled
                     && (activatedAtUtc == null
                         || (r.LinkId == link.Id
                             && (r.Status == Domain.Enums.QuestionnaireResponseStatus.Submitted
                                 ? r.SubmittedAt >= activatedAtUtc
                                 : r.DateCreated >= activatedAtUtc))))
            .OrderByDescending(r => r.DateCreated)
            .FirstOrDefaultAsync(ct);

        if (qResponse is not null)
        {
            questionnaireStatus = qResponse.Status == Domain.Enums.QuestionnaireResponseStatus.Submitted
                ? "submitted"
                : "pending";
            questionnaireTitle = qResponse.Questionnaire.Title;
            questionnaireResponsePublicId = qResponse.PublicId;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(now);

        // Query the Active NutritionPlan whose date window contains today to source
        // goal + targetWeightKg plan-first. Fallback to OnboardingData only when the plan
        // value is null. Key: plan.ClientId == clientProfile.UserId — ApplicationUser.Id is
        // the canonical clientId for Mongo documents (#840). A client may hold several
        // sequential, non-overlapping Active plans (#780), so pick the one whose window
        // contains today rather than the most recent.
        //
        // Gated on CanViewNutritionPlans: a training-only caller must not trigger this
        // query at all, otherwise the plan's Goal/TargetWeightKg would win via the ??
        // fallback below and disclose the existence/values of a plan the caller has no
        // visibility into (#921).
        //
        // hasActiveNutritionPlan is resolved SEPARATELY from activePlan via the strict,
        // window-only predicate shared with GetClientsEndpoint — activePlan keeps
        // PlanWindowResolver.ResolveCurrentPlan's legacy single-candidate fallback (needed for
        // the Goal/TargetWeightKg fields below), which would otherwise let an unranged plan
        // read as Active here but Paused on the clients list (#1094).
        NutritionPlan? activePlan = null;
        var hasActiveNutritionPlan = false;
        if (effective.CanViewNutritionPlans)
        {
            var planFilter = Builders<NutritionPlan>.Filter.And(
                Builders<NutritionPlan>.Filter.Eq(p => p.ClientId, clientProfile.UserId),
                Builders<NutritionPlan>.Filter.Eq(p => p.NutritionistId, professionalProfile.UserId),
                Builders<NutritionPlan>.Filter.Eq(p => p.Status, NutritionPlanStatus.Active));

            using var planCursor = await mongo.NutritionPlans.FindAsync(planFilter, cancellationToken: ct);
            var activePlans = await planCursor.ToListAsync(ct);
            activePlan = PlanWindowResolver.ResolveCurrentPlan(activePlans, p => p.StartDate, p => p.Weeks.Count, now);
            hasActiveNutritionPlan = PlanWindowResolver.ResolveCurrentPlanStrict(
                activePlans, p => p.StartDate, p => p.Weeks.Count, today) is not null;
        }

        // Active training plan lookup, gated the same way as the nutrition lookup above. It feeds
        // the status derivation that must agree with GetClientsEndpoint (#1094) and the
        // sessions-this-week counts. Resolved via the same strict, window-only predicate as the
        // nutrition plan above — no legacy fallback needed since nothing else reads it.
        var hasActiveTrainingPlan = false;
        int? sessionsCompletedThisWeek = null;
        int? sessionsPlannedThisWeek = null;
        if (effective.CanViewTrainingPlans)
        {
            var trainingPlanFilter = Builders<TrainingPlan>.Filter.And(
                Builders<TrainingPlan>.Filter.Eq(p => p.ClientId, clientProfile.UserId),
                Builders<TrainingPlan>.Filter.Eq(p => p.TrainerId, professionalProfile.UserId),
                Builders<TrainingPlan>.Filter.Eq(p => p.Status, TrainingPlanStatus.Active));

            using var trainingPlanCursor = await mongo.TrainingPlans.FindAsync(trainingPlanFilter, cancellationToken: ct);
            var activeTrainingPlans = await trainingPlanCursor.ToListAsync(ct);
            hasActiveTrainingPlan = PlanWindowResolver.ResolveCurrentPlanStrict(
                activeTrainingPlans, p => p.StartDate, p => p.Weeks.Count, today) is not null;

            if (activeTrainingPlans.Count > 0)
            {
                // The week follows the client's local date, which can differ from the UTC date used by
                // hasActiveTrainingPlan above by up to the client's UTC offset (about 13h for Auckland).
                var clientTimeZone = await db.ResolveClientTimeZoneAsync(clientProfile.UserId, ct);
                var week = WeeklySessionCompletion.ResolveWeek(activeTrainingPlans, now, clientTimeZone);

                if (week is not null)
                {
                    sessionsPlannedThisWeek = week.PlannedSessions.Count;
                    sessionsCompletedThisWeek = await CountCompletedSessionsAsync(week, clientProfile.UserId, ct);
                }
            }
        }

        var status = ClientStatusClassifier.Classify(
            link.ConnectionState != ConnectionState.Ended,
            effective,
            hasActiveNutritionPlan,
            hasActiveTrainingPlan);

        OnboardingDataDto? onboarding = null;
        if (clientProfile.OnboardingData is { } od)
        {
            // Plan-first: prefer plan's goal and targetWeightKg; fall back to onboarding baseline.
            var effectiveTargetWeightKg = activePlan?.TargetWeightKg ?? od.TargetWeightKg;
            var effectivePrimaryGoal = activePlan?.Goal?.ToString() ?? od.PrimaryGoal.ToString();

            onboarding = new OnboardingDataDto
            {
                Sex = od.Sex.ToString(),
                TargetWeightKg = effectiveTargetWeightKg,
                BodyType = od.BodyType.ToString(),
                PrimaryGoal = effectivePrimaryGoal,
                TimeHorizon = od.TimeHorizon.ToString(),
                JobType = od.JobType.ToString(),
                SleepHours = od.SleepHours,
                StressLevel = od.StressLevel,
                CurrentTrainingFrequency = effective.CanViewTrainingPlans ? od.CurrentTrainingFrequency.ToString() : null,
                DesiredTrainingFrequency = effective.CanViewTrainingPlans ? od.DesiredTrainingFrequency.ToString() : null,
                FitnessRating = od.FitnessRating,
                PreferredActivities = od.PreferredActivities,
                Injuries = od.Injuries,
                MealsPerDay = effective.CanViewNutritionPlans ? od.MealsPerDay.ToString() : null,
                DietaryStyle = effective.CanViewNutritionPlans ? od.DietaryStyle.ToString() : null,
                Allergies = od.Allergies,
                PlanExperience = od.PlanExperience.ToString(),
                PastBlockers = od.PastBlockers,
                PrimaryMotivation = od.PrimaryMotivation.ToString(),
                DateUpdated = od.DateUpdated ?? od.DateCreated,
            };

            if (effective.CanViewNutritionPlans)
            {
                if (od.NutritionTargets is { } targets)
                {
                    var targetsDate = targets.DateUpdated ?? targets.DateCreated;
                    if (targetsDate > onboarding.DateUpdated)
                    {
                        onboarding.DateUpdated = targetsDate;
                    }
                }

                onboarding.DerivedActivityLevel = od.NutritionTargets?.DerivedActivityLevel.ToString();
                onboarding.DerivedNutritionGoal = od.NutritionTargets?.DerivedNutritionGoal.ToString();
                onboarding.Bmr = od.NutritionTargets?.Bmr;
                onboarding.Tdee = od.NutritionTargets?.Tdee;
                onboarding.AdjustedKcal = od.NutritionTargets?.AdjustedKcal;
                onboarding.ProteinGrams = od.NutritionTargets?.ProteinGrams;
                onboarding.CarbsGrams = od.NutritionTargets?.CarbsGrams;
                onboarding.FatGrams = od.NutritionTargets?.FatGrams;
                onboarding.MealDistribution = od.NutritionTargets?.MealDistribution;
                onboarding.FibreGrams = od.NutritionTargets?.FibreGrams;
                onboarding.Dislikes = od.Dislikes;
                onboarding.Habits = od.Habits;
            }
        }

        var isOutsidePlan = (await entitlementService.GetKeepClientsStateAsync(professionalProfile.Id, ct))
            .OverCapClientProfileIds
            .Contains(clientProfile.Id);

        await Send.OkAsync(new GetClientDashboardResponse
        {
            IsOutsidePlan = isOutsidePlan,
            LinkId = link.Id,
            ClientPublicId = clientProfile.PublicId,
            ClientUserId = clientProfile.UserId,
            Email = clientProfile.User.Email!,
            FirstName = clientProfile.User.FirstName,
            LastName = clientProfile.User.LastName,
            DateOfBirth = clientProfile.DateOfBirth,
            HeightCm = clientProfile.HeightCm,
            WeightKg = clientProfile.WeightKg,
            Goals = clientProfile.Goals,
            // DateCreated, not DateUpdated — matches GetClientsEndpoint's "Client since" so a
            // capability-flag toggle (which bumps DateUpdated) cannot move this date (#1094).
            LinkedAt = link.DateCreated,
            IsActive = link.ConnectionState != ConnectionState.Ended,
            Status = status,
            CanViewNutritionPlans = effective.CanViewNutritionPlans,
            CanViewTrainingPlans = effective.CanViewTrainingPlans,
            HasRegistered = clientProfile.User.EmailConfirmed,
            QuestionnaireStatus = questionnaireStatus,
            QuestionnaireTitle = questionnaireTitle,
            QuestionnaireResponsePublicId = questionnaireResponsePublicId,
            QuestionnaireSubmittedAt = qResponse?.SubmittedAt,
            TotalMeasurements = totalMeasurements,
            TotalProgressPhotos = totalProgressPhotos,
            LatestMeasurement = latestMeasurement,
            CompliancePercent = compliancePercent,
            CurrentStreak = currentStreak,
            NutritionCompliancePercent = nutritionCompliancePercent,
            TrainingCompliancePercent = trainingCompliancePercent,
            NutritionStreak = nutritionStreak,
            TrainingStreak = trainingStreak,
            SessionsCompletedThisWeek = sessionsCompletedThisWeek,
            SessionsPlannedThisWeek = sessionsPlannedThisWeek,
            Onboarding = onboarding
        }, ct);
    }

    private async Task<int> CountCompletedSessionsAsync(
        WeeklySessionCompletion.WeekScope week,
        Guid clientUserId,
        CancellationToken ct)
    {
        if (week.PlannedSessions.Count == 0)
        {
            return 0;
        }

        // Keyed on ApplicationUser.Id, never the client's PublicId — Mongo documents join on the former.
        var filter = Builders<SessionExecution>.Filter.And(
            Builders<SessionExecution>.Filter.Eq(e => e.ClientId, clientUserId),
            Builders<SessionExecution>.Filter.Eq(e => e.PlanId, week.Plan.ExternalId),
            Builders<SessionExecution>.Filter.In(e => e.SessionId, week.PlannedSessions.Select(s => (Guid?)s.SessionId)),
            Builders<SessionExecution>.Filter.Gte(e => e.Date, week.WeekStartUtc),
            Builders<SessionExecution>.Filter.Lt(e => e.Date, week.WeekEndUtc));

        using var cursor = await mongo.SessionExecutions.FindAsync(filter, cancellationToken: ct);
        var executions = await cursor.ToListAsync(ct);

        return WeeklySessionCompletion.CountCompleted(week, executions);
    }
}
