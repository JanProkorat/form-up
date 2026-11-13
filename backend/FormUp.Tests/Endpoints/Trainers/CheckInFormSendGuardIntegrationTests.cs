using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Trainers;

/// <summary>
/// A Check-in form can never be sent through the onboarding paths: assign, replace, pending invite
/// and accepting a client request all refuse it with CHECKIN_FORM_NOT_SENDABLE before any write.
/// </summary>
[Collection(TestCollection.Name)]
public class CheckInFormSendGuardIntegrationTests(FitnessApiFactory factory)
{
    private const string ErrorCode = "CHECKIN_FORM_NOT_SENDABLE";

    private async Task<Guid> CreateQuestionnaireAsync(Guid professionalId, QuestionnaireType type)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var questionnaire = new Questionnaire
        {
            PublicId = Guid.NewGuid(),
            ProfessionalId = professionalId,
            Title = $"Form {Guid.NewGuid():N}",
            Type = type,
            IsActive = true,
        };
        db.Questionnaires.Add(questionnaire);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return questionnaire.PublicId;
    }

    private async Task<(Actor Trainer, Actor Client, long LinkId)> CreateLinkedPairAsync()
    {
        var trainer = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, trainer, "studio");
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        var linkId = await TestActors.Link(factory, trainer, client).CreateAsync(TestContext.Current.CancellationToken);

        return (trainer, client, linkId);
    }

    [Fact]
    public async Task Assign_CheckInForm_Returns400AndLeavesLinkUntouched()
    {
        var (trainer, client, linkId) = await CreateLinkedPairAsync();
        var formId = await CreateQuestionnaireAsync(trainer.UserId, QuestionnaireType.CheckIn);

        var response = await trainer.Http.PostAsJsonAsync(
            $"/trainer/clients/{client.PublicId}/assign-questionnaire",
            new { QuestionnairePublicId = formId },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain(ErrorCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.ClientProfessionalLinks.AsNoTracking().SingleAsync(l => l.Id == linkId, TestContext.Current.CancellationToken))
            .QuestionnaireId.Should().BeNull();
        (await db.QuestionnaireResponses.AnyAsync(r => r.LinkId == linkId, TestContext.Current.CancellationToken))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Assign_OnboardingForm_Returns204()
    {
        var (trainer, client, _) = await CreateLinkedPairAsync();
        var formId = await CreateQuestionnaireAsync(trainer.UserId, QuestionnaireType.Onboarding);

        var response = await trainer.Http.PostAsJsonAsync(
            $"/trainer/clients/{client.PublicId}/assign-questionnaire",
            new { QuestionnairePublicId = formId },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.QuestionnaireResponses.AsNoTracking().SingleAsync(
                r => r.ClientId == client.UserId && r.ProfessionalId == trainer.UserId, TestContext.Current.CancellationToken))
            .Kind.Should().Be(QuestionnaireResponseKind.Questionnaire);
    }

    [Fact]
    public async Task Replace_OnboardingForm_CancelsThePendingResponseAndCreatesAQuestionnaireKindOne()
    {
        var (trainer, client, linkId) = await CreateLinkedPairAsync();
        var firstId = await CreateQuestionnaireAsync(trainer.UserId, QuestionnaireType.Onboarding);
        var secondId = await CreateQuestionnaireAsync(trainer.UserId, QuestionnaireType.Onboarding);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var first = await db.Questionnaires.SingleAsync(q => q.PublicId == firstId, TestContext.Current.CancellationToken);
            db.QuestionnaireResponses.Add(new QuestionnaireResponse
            {
                QuestionnaireId = first.Id,
                ClientId = client.UserId,
                ProfessionalId = trainer.UserId,
                LinkId = linkId,
                Status = QuestionnaireResponseStatus.Pending,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var response = await trainer.Http.PostAsJsonAsync(
            $"/trainer/clients/{client.PublicId}/replace-questionnaire",
            new { QuestionnairePublicId = secondId },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        using var assertScope = factory.Services.CreateScope();
        var assertDb = assertScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var replacement = await assertDb.QuestionnaireResponses.AsNoTracking()
            .SingleAsync(r => r.LinkId == linkId && r.Status == QuestionnaireResponseStatus.Pending, TestContext.Current.CancellationToken);
        replacement.Kind.Should().Be(QuestionnaireResponseKind.Questionnaire);
    }

    [Fact]
    public async Task Replace_CheckInForm_Returns400AndKeepsPendingResponse()
    {
        var (trainer, client, linkId) = await CreateLinkedPairAsync();
        var onboardingId = await CreateQuestionnaireAsync(trainer.UserId, QuestionnaireType.Onboarding);
        var checkInId = await CreateQuestionnaireAsync(trainer.UserId, QuestionnaireType.CheckIn);

        long pendingResponseId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var onboarding = await db.Questionnaires.SingleAsync(q => q.PublicId == onboardingId, TestContext.Current.CancellationToken);
            var pending = new QuestionnaireResponse
            {
                QuestionnaireId = onboarding.Id,
                ClientId = client.UserId,
                ProfessionalId = trainer.UserId,
                LinkId = linkId,
                Status = QuestionnaireResponseStatus.Pending,
            };
            db.QuestionnaireResponses.Add(pending);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            pendingResponseId = pending.Id;
        }

        var response = await trainer.Http.PostAsJsonAsync(
            $"/trainer/clients/{client.PublicId}/replace-questionnaire",
            new { QuestionnairePublicId = checkInId },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain(ErrorCode);

        using var assertScope = factory.Services.CreateScope();
        var assertDb = assertScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await assertDb.QuestionnaireResponses.AsNoTracking().SingleAsync(r => r.Id == pendingResponseId, TestContext.Current.CancellationToken))
            .Status.Should().Be(QuestionnaireResponseStatus.Pending);
        (await assertDb.QuestionnaireResponses.CountAsync(r => r.LinkId == linkId, TestContext.Current.CancellationToken))
            .Should().Be(1);
    }

    [Fact]
    public async Task CreatePendingInvite_CheckInForm_Returns400AndCreatesNoInvite()
    {
        var trainer = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);
        var formId = await CreateQuestionnaireAsync(trainer.UserId, QuestionnaireType.CheckIn);
        var inviteeEmail = $"{Guid.NewGuid():N}@checkin-guard.com";

        var response = await trainer.Http.PostAsJsonAsync(
            "/trainer/pending-invites",
            new { Email = inviteeEmail, QuestionnairePublicId = formId },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain(ErrorCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.PendingInvites.AnyAsync(p => p.Email == inviteeEmail, TestContext.Current.CancellationToken))
            .Should().BeFalse();
    }

    [Fact]
    public async Task AcceptClientRequest_CheckInForm_Returns400AndKeepsRequestPending()
    {
        var trainer = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        var formId = await CreateQuestionnaireAsync(trainer.UserId, QuestionnaireType.CheckIn);

        var requestPublicId = Guid.NewGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.ClientRequests.Add(new ClientRequest
            {
                PublicId = requestPublicId,
                ClientProfileId = client.ProfileId,
                ProfessionalProfileId = trainer.ProfileId,
                Status = ClientRequestStatus.Pending,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var response = await trainer.Http.PostAsJsonAsync(
            $"/trainer/client-requests/{requestPublicId}/accept",
            new { QuestionnairePublicId = formId },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain(ErrorCode);

        using var assertScope = factory.Services.CreateScope();
        var assertDb = assertScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await assertDb.ClientRequests.AsNoTracking().SingleAsync(r => r.PublicId == requestPublicId, TestContext.Current.CancellationToken))
            .Status.Should().Be(ClientRequestStatus.Pending);
        (await assertDb.ClientProfessionalLinks.AnyAsync(
                l => l.ClientProfileId == client.ProfileId && l.ProfessionalProfileId == trainer.ProfileId,
                TestContext.Current.CancellationToken))
            .Should().BeFalse();
    }
}
