using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Features.Client.OnboardingAnswers.Shared;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.Client.OnboardingAnswers.Update;

/// <summary>
/// Lets the client edit a subset of their onboarding answers and tells their linked coaches.
/// </summary>
/// <param name="db">Database context.</param>
/// <param name="audit">Audit logging service.</param>
/// <param name="notifications">Notification service.</param>
internal sealed class UpdateClientOnboardingAnswersEndpoint(
    IApplicationDbContext db,
    IAuditService audit,
    INotificationService notifications)
    : Endpoint<UpdateClientOnboardingAnswersRequest, ClientOnboardingAnswersResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Patch("/client/onboarding/answers");
        Roles(AppRoles.Client);
        Summary(s =>
        {
            s.Summary = "Edit my onboarding answers";
            s.Description =
                "Partially updates the editable onboarding answers; omitted fields stay unchanged " +
                "(so a target weight cannot be cleared). Nutrition targets are not recalculated. " +
                "Linked coaches are notified only when something changed.";
            s.Responses[StatusCodes.Status200OK] = "Current answers (unchanged when nothing differed)";
            s.Responses[StatusCodes.Status400BadRequest] = "Invalid request";
            s.Responses[StatusCodes.Status401Unauthorized] = "Missing or unreadable caller claim";
            s.Responses[StatusCodes.Status404NotFound] = "No client profile";
            s.Responses[StatusCodes.Status409Conflict] = "Onboarding not completed (ONBOARDING_NOT_COMPLETED)";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(UpdateClientOnboardingAnswersRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var userGuid = Guid.Parse(userId);

        var profile = await db.ClientProfiles
            .Include(cp => cp.OnboardingData)
            .FirstOrDefaultAsync(cp => cp.UserId == userGuid, ct);

        if (profile is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (profile.OnboardingData is null)
        {
            await this.SendProblemAsync(
                409, ErrorCodes.OnboardingNotCompleted, "The client has not completed onboarding.", ct);
            return;
        }

        var changedFields = ApplyChanges(profile, req);

        if (changedFields.Count == 0)
        {
            await Send.OkAsync(ClientOnboardingAnswersResponse.FromEntity(profile.OnboardingData), ct);
            return;
        }

        await db.SaveChangesAsync(ct);

        await audit.LogAsync(
            userGuid, "UpdateClientOnboardingAnswers", nameof(ClientOnboardingData), profile.PublicId,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            newValues: $"{{\"changedFields\":[{string.Join(",", changedFields.Select(field => $"\"{field}\""))}]}}",
            ct: ct);

        await NotifyCoachesAsync(profile, userGuid, changedFields, ct);

        await Send.OkAsync(ClientOnboardingAnswersResponse.FromEntity(profile.OnboardingData), ct);
    }

    private static List<string> ApplyChanges(ClientProfile profile, UpdateClientOnboardingAnswersRequest req)
    {
        var data = profile.OnboardingData!;
        var changed = new List<string>();

        if (req.PrimaryGoal is not null
            && Enum.Parse<PrimaryGoal>(req.PrimaryGoal, true) is var goal && goal != data.PrimaryGoal)
        {
            data.PrimaryGoal = goal;
            changed.Add(NotificationTemplates.OnboardingFieldPrimaryGoal);
        }

        if (req.TargetWeightKg is not null && req.TargetWeightKg != data.TargetWeightKg)
        {
            data.TargetWeightKg = req.TargetWeightKg;
            changed.Add(NotificationTemplates.OnboardingFieldTargetWeightKg);
        }

        if (req.DesiredTrainingFrequency is not null
            && Enum.Parse<DesiredTrainingFrequency>(req.DesiredTrainingFrequency, true) is var frequency
            && frequency != data.DesiredTrainingFrequency)
        {
            data.DesiredTrainingFrequency = frequency;
            changed.Add(NotificationTemplates.OnboardingFieldDesiredTrainingFrequency);
        }

        if (req.GymAccess is not null
            && Enum.Parse<GymAccess>(req.GymAccess, true) is var gymAccess && gymAccess != data.GymAccess)
        {
            data.GymAccess = gymAccess;
            changed.Add(NotificationTemplates.OnboardingFieldGymAccess);
        }

        if (req.Injuries is not null && string.Join(",", Normalize(req.Injuries)) is var injuries
            && injuries != data.Injuries)
        {
            data.Injuries = injuries;
            changed.Add(NotificationTemplates.OnboardingFieldInjuries);
        }

        if (req.DietaryStyle is not null
            && Enum.Parse<DietaryStyle>(req.DietaryStyle, true) is var dietaryStyle
            && dietaryStyle != data.DietaryStyle)
        {
            data.DietaryStyle = dietaryStyle;
            changed.Add(NotificationTemplates.OnboardingFieldDietaryStyle);
        }

        if (req.Allergies is not null && string.Join(",", Normalize(req.Allergies)) is var allergies
            && allergies != data.Allergies)
        {
            data.Allergies = allergies;
            changed.Add(NotificationTemplates.OnboardingFieldAllergies);
        }

        if (req.Dislikes is not null && Normalize(req.Dislikes) is var dislikes
            && !dislikes.SequenceEqual(data.Dislikes))
        {
            data.Dislikes = dislikes;
            changed.Add(NotificationTemplates.OnboardingFieldDislikes);
        }

        ApplyBasicsChanges(profile, req, changed);
        ApplyLifestyleChanges(data, req, changed);

        return changed;
    }

    // Date of birth and height live on both the onboarding row and the profile; either copy differing counts as a change.
    private static void ApplyBasicsChanges(
        ClientProfile profile, UpdateClientOnboardingAnswersRequest req, List<string> changed)
    {
        var data = profile.OnboardingData!;

        if (req.DateOfBirth is { } dateOfBirth)
        {
            var stored = dateOfBirth.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

            if (DateOnly.FromDateTime(data.DateOfBirth) != dateOfBirth
                || profile.DateOfBirth is not { } profileDateOfBirth
                || DateOnly.FromDateTime(profileDateOfBirth) != dateOfBirth)
            {
                data.DateOfBirth = stored;
                profile.DateOfBirth = stored;
                changed.Add(NotificationTemplates.OnboardingFieldDateOfBirth);
            }
        }

        if (req.Sex is not null && Enum.Parse<BiologicalSex>(req.Sex, true) is var sex && sex != data.Sex)
        {
            data.Sex = sex;
            changed.Add(NotificationTemplates.OnboardingFieldSex);
        }

        if (req.HeightCm is { } heightCm && (heightCm != data.HeightCm || heightCm != profile.HeightCm))
        {
            data.HeightCm = heightCm;
            profile.HeightCm = heightCm;
            changed.Add(NotificationTemplates.OnboardingFieldHeightCm);
        }
    }

    private static void ApplyLifestyleChanges(
        ClientOnboardingData data, UpdateClientOnboardingAnswersRequest req, List<string> changed)
    {
        if (req.JobType is not null && Enum.Parse<JobType>(req.JobType, true) is var jobType
            && jobType != data.JobType)
        {
            data.JobType = jobType;
            changed.Add(NotificationTemplates.OnboardingFieldJobType);
        }

        if (req.SleepHours is { } sleepHours && sleepHours != data.SleepHours)
        {
            data.SleepHours = sleepHours;
            changed.Add(NotificationTemplates.OnboardingFieldSleepHours);
        }

        if (req.StressLevel is { } stressLevel && stressLevel != data.StressLevel)
        {
            data.StressLevel = stressLevel;
            changed.Add(NotificationTemplates.OnboardingFieldStressLevel);
        }

        if (req.CurrentTrainingFrequency is not null
            && Enum.Parse<CurrentTrainingFrequency>(req.CurrentTrainingFrequency, true) is var currentFrequency
            && currentFrequency != data.CurrentTrainingFrequency)
        {
            data.CurrentTrainingFrequency = currentFrequency;
            changed.Add(NotificationTemplates.OnboardingFieldCurrentTrainingFrequency);
        }

        if (req.PreferredActivities is not null && string.Join(",", Normalize(req.PreferredActivities)) is var activities
            && activities != data.PreferredActivities)
        {
            data.PreferredActivities = activities;
            changed.Add(NotificationTemplates.OnboardingFieldPreferredActivities);
        }

        if (req.MealsPerDay is not null
            && Enum.Parse<MealsPerDay>(req.MealsPerDay, true) is var mealsPerDay && mealsPerDay != data.MealsPerDay)
        {
            data.MealsPerDay = mealsPerDay;
            changed.Add(NotificationTemplates.OnboardingFieldMealsPerDay);
        }

        if (req.Habits is not null && Normalize(req.Habits) is var habits && !habits.SequenceEqual(data.Habits))
        {
            data.Habits = habits;
            changed.Add(NotificationTemplates.OnboardingFieldHabits);
        }
    }

    /// <summary>
    /// One notification per professional with an active link to the client.
    /// </summary>
    private async Task NotifyCoachesAsync(
        ClientProfile profile, Guid clientUserId, List<string> changedFields, CancellationToken ct)
    {
        var recipients = await db.ClientProfessionalLinks
            .AsNoTracking()
            .Where(l => l.ClientProfileId == profile.Id && l.ConnectionState != ConnectionState.Ended)
            .Select(l => new { l.ProfessionalProfile.UserId, l.ProfessionalProfile.User.Language })
            .Distinct()
            .ToListAsync(ct);

        if (recipients.Count == 0)
        {
            return;
        }

        var clientName = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == clientUserId)
            .Select(u => u.FirstName + " " + u.LastName)
            .FirstAsync(ct);

        foreach (var recipient in recipients)
        {
            await notifications.CreateAsync(
                recipient.UserId,
                NotificationType.OnboardingAnswersUpdated,
                new Dictionary<string, string>
                {
                    ["clientName"] = clientName,
                    ["fields"] = NotificationTemplates.LocalizeOnboardingFields(changedFields, recipient.Language),
                    ["changedFields"] = string.Join(",", changedFields),
                    ["clientId"] = profile.PublicId.ToString(),
                },
                ct: ct);
        }
    }

    private static List<string> Normalize(List<string> items) =>
        items.Select(item => item.Trim()).ToList();
}
