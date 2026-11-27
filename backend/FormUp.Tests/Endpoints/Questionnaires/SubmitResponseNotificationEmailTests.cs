using System.Net;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Features.Questionnaires.Shared;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Questionnaires;

/// <summary>
/// The coach's check-in email from POST /client/questionnaire/response/{id}/submit.
/// </summary>
[Collection(TestCollection.Shard2)]
public class SubmitResponseNotificationEmailTests(FitnessApiFactory factory)
{
    private static readonly SeedQuestion[] Form = [new("note", QuestionTypes.ShortText)];

    private async Task<(QuestionnaireAnswerScenario Scenario, Guid CoachId)> SeedAsync(
        QuestionnaireResponseKind kind, bool? coachEmailEnabled = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var scenario = await QuestionnaireAnswerScenario.CreateAsync(factory, Form);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var response = await db.QuestionnaireResponses.FirstAsync(r => r.PublicId == scenario.ResponsePublicId, ct);
        response.Kind = kind;

        if (coachEmailEnabled is { } enabled)
        {
            db.NotificationPreferences.Add(new NotificationPreference
            {
                UserId = response.ProfessionalId,
                Event = NotificationEvent.WeeklyCheckInSubmitted,
                EmailEnabled = enabled,
                PushEnabled = false,
            });
        }

        await db.SaveChangesAsync(ct);

        return (scenario, response.ProfessionalId);
    }

    private async Task<HttpStatusCode> SubmitAsync(QuestionnaireAnswerScenario scenario) =>
        (await scenario.Client.Http.PostAsync(
            $"/client/questionnaire/response/{scenario.ResponsePublicId}/submit",
            null,
            TestContext.Current.CancellationToken)).StatusCode;

    private Task<IReadOnlyList<NotificationEmailWorkItem>> SentToAsync(Guid coachId) =>
        factory.Services.GetRequiredService<FakeEmailService>()
            .NotificationsForAsync(factory.Services.GetRequiredService<IBackgroundEmailQueue>(), coachId);

    [Fact]
    public async Task Submit_CheckInWithDefaultPreference_EmailsTheCoach()
    {
        var (scenario, coachId) = await SeedAsync(QuestionnaireResponseKind.CheckIn);

        (await SubmitAsync(scenario)).Should().Be(HttpStatusCode.OK);

        var sent = await SentToAsync(coachId);
        sent.Should().ContainSingle();
        sent[0].Event.Should().Be(NotificationEvent.WeeklyCheckInSubmitted);
        sent[0].Values.Should().ContainKey(NotificationEmailWorkItem.ActionUrlKey);
        sent[0].Values[NotificationEmailWorkItem.ActionUrlKey].Should().EndWith("/forms");
    }

    [Fact]
    public async Task Submit_CheckInWithEmailSwitchOff_SucceedsAndSendsNoEmail()
    {
        var (scenario, coachId) = await SeedAsync(QuestionnaireResponseKind.CheckIn, coachEmailEnabled: false);

        (await SubmitAsync(scenario)).Should().Be(HttpStatusCode.OK);

        (await SentToAsync(coachId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Submit_CheckInWithEmailSwitchOn_EmailsTheCoach()
    {
        var (scenario, coachId) = await SeedAsync(QuestionnaireResponseKind.CheckIn, coachEmailEnabled: true);

        (await SubmitAsync(scenario)).Should().Be(HttpStatusCode.OK);

        (await SentToAsync(coachId)).Should().ContainSingle();
    }

    [Fact]
    public async Task Submit_OnboardingForm_SendsNoCheckInEmailButKeepsTheInAppNotice()
    {
        var (scenario, coachId) = await SeedAsync(QuestionnaireResponseKind.Onboarding);

        (await SubmitAsync(scenario)).Should().Be(HttpStatusCode.OK);

        (await SentToAsync(coachId)).Should().BeEmpty();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.Notifications.AnyAsync(
            n => n.RecipientUserId == coachId && n.Type == NotificationType.QuestionnaireSubmitted,
            TestContext.Current.CancellationToken)).Should().BeTrue();
    }
}
