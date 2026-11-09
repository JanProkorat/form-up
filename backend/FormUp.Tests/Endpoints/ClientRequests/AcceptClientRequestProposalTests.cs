using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static FormUp.Tests.Endpoints.ClientPayments.ClientPaymentFixtures;
using static FormUp.Tests.Endpoints.PaymentSettings.PaymentSettingsFixtures;

namespace FormUp.Tests.Endpoints.ClientRequests;

/// <summary>
/// A client accepts a coach's package proposal (POST /client/requests/{id}/accept-proposal).
/// </summary>
[Collection(TestCollection.Name)]
public class AcceptClientRequestProposalTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Proposal(Actor Coach, Actor Client, Guid RequestId, Guid PackageId, Guid DiscountId, Guid FormId);

    private async Task<Actor> CoachWithPaymentsAsync()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await SubscribeAsync(factory, coach, canUsePayments: true);

        return coach;
    }

    private static async Task<Guid> SendRequestAsync(Actor coach, Actor client)
    {
        var response = await client.Http.PostAsJsonAsync("/client/requests", new { ProfessionalPublicId = coach.PublicId }, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        return (await response.Content.ReadFromJsonAsync<RequestCreated>(Ct))!.PublicId;
    }

    private async Task<Proposal> ProposedAsync(Actor? existingClient = null)
    {
        var coach = await CoachWithPaymentsAsync();
        var client = existingClient ?? await TestActors.Client(factory).CreateAsync(Ct);
        var packageId = await CreatePackageAsync(coach, 2500m);
        var discountId = await CreateDiscountAsync(coach, 30m);
        var formId = await CreateFormAsync(coach.UserId);
        var requestId = await SendRequestAsync(coach, client);

        var proposed = await coach.Http.PostAsJsonAsync(
            $"/trainer/client-requests/{requestId}/propose-package",
            new { PackagePublicId = packageId, DiscountPublicId = discountId, QuestionnairePublicId = formId },
            Ct);
        proposed.StatusCode.Should().Be(HttpStatusCode.NoContent);

        return new Proposal(coach, client, requestId, packageId, discountId, formId);
    }

    private static Task<HttpResponseMessage> AcceptAsync(Actor client, Guid requestId) =>
        client.Http.PostAsJsonAsync($"/client/requests/{requestId}/accept-proposal", new { }, Ct);

    private async Task<Guid> CreateFormAsync(Guid professionalUserId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var form = new Questionnaire
        {
            PublicId = Guid.NewGuid(),
            ProfessionalId = professionalUserId,
            Title = $"Form {Guid.NewGuid():N}",
            Type = QuestionnaireType.Onboarding,
            IsActive = true,
        };
        db.Questionnaires.Add(form);
        await db.SaveChangesAsync(Ct);

        return form.PublicId;
    }

    private async Task<ClientRequest> StoredRequestAsync(Guid requestId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.ClientRequests.AsNoTracking().SingleAsync(r => r.PublicId == requestId, Ct);
    }

    private async Task<bool> LinkActiveAsync(Actor coach, Actor client)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.ClientProfessionalLinks.AnyAsync(
            l => l.ClientProfileId == client.ProfileId && l.ProfessionalProfileId == coach.ProfileId && l.ConnectionState != ConnectionState.Ended, Ct);
    }

    private async Task<List<ClientPaymentTerms>> TermsOfAsync(Actor coach, Actor client)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.ClientPaymentTerms
            .AsNoTracking()
            .Include(t => t.CoachPackage)
            .Include(t => t.CoachDiscount)
            .Where(t => t.ClientPaymentAssignment.ProfessionalProfileId == coach.ProfileId
                && t.ClientPaymentAssignment.ClientProfileId == client.ProfileId)
            .ToListAsync(Ct);
    }

    [Fact]
    public async Task Accept_OpenProposal_StartsTheLinkStoresTheTermsAssignsTheFormAndNotifiesTheCoach()
    {
        var proposal = await ProposedAsync();

        var response = await AcceptAsync(proposal.Client, proposal.RequestId);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await LinkActiveAsync(proposal.Coach, proposal.Client)).Should().BeTrue();
        (await StoredRequestAsync(proposal.RequestId)).Status.Should().Be(ClientRequestStatus.Accepted);

        var terms = (await TermsOfAsync(proposal.Coach, proposal.Client)).Should().ContainSingle().Subject;
        terms.CoachPackage.PublicId.Should().Be(proposal.PackageId);
        terms.CoachDiscount!.PublicId.Should().Be(proposal.DiscountId);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var formResponse = await db.QuestionnaireResponses.SingleAsync(
            r => r.ClientId == proposal.Client.UserId && r.ProfessionalId == proposal.Coach.UserId, Ct);
        formResponse.Status.Should().Be(QuestionnaireResponseStatus.Pending);

        var notification = await db.Notifications.SingleAsync(
            n => n.RecipientUserId == proposal.Coach.UserId && n.Type == NotificationType.ClientRequestProposalAccepted, Ct);
        JsonDocument.Parse(notification.Data!).RootElement.GetProperty("requestId").GetString()
            .Should().Be(proposal.RequestId.ToString());

        factory.Services.GetRequiredService<FakeRealtimeNotifier>().Calls.Should().Contain(
            call => call.UserId == proposal.Coach.UserId && call.EventType == "clientrequestproposalaccepted");

        var accepted = await db.ChatMessages.SingleAsync(
            m => m.EventSourceId == proposal.RequestId && m.EventType == ChatEventType.Accepted, Ct);
        accepted.SenderUserId.Should().Be(proposal.Client.UserId);
    }

    [Fact]
    public async Task Accept_PackageArchivedSincePropose_StartsTheLinkWithoutTermsAndAuditsTheSkip()
    {
        var proposal = await ProposedAsync();
        (await proposal.Coach.Http.PostAsync($"/payments/packages/{proposal.PackageId}/archive", Empty(), Ct))
            .IsSuccessStatusCode.Should().BeTrue();

        var response = await AcceptAsync(proposal.Client, proposal.RequestId);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await LinkActiveAsync(proposal.Coach, proposal.Client)).Should().BeTrue();
        (await TermsOfAsync(proposal.Coach, proposal.Client)).Should().BeEmpty();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.AuditLogs.AnyAsync(
            a => a.EntityId == proposal.RequestId && a.EntityType == nameof(ClientRequest) && a.Action == "InvitePackageTermsSkipped", Ct))
            .Should().BeTrue();
    }

    [Fact]
    public async Task Accept_ProposalWithdrawnBeforeTheAnswer_Returns409AndStartsNoLink()
    {
        var proposal = await ProposedAsync();
        (await proposal.Coach.Http.PostAsJsonAsync(
            $"/trainer/client-requests/{proposal.RequestId}/withdraw-proposal", new { }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var response = await AcceptAsync(proposal.Client, proposal.RequestId);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ProblemCodeAsync(response)).Should().Be("CLIENT_REQUEST_INVALID_STATE");
        (await LinkActiveAsync(proposal.Coach, proposal.Client)).Should().BeFalse();
    }

    [Fact]
    public async Task Accept_PendingRequestWithoutProposal_Returns409()
    {
        var coach = await CoachWithPaymentsAsync();
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var requestId = await SendRequestAsync(coach, client);

        var response = await AcceptAsync(client, requestId);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ProblemCodeAsync(response)).Should().Be("CLIENT_REQUEST_INVALID_STATE");
    }

    [Fact]
    public async Task Accept_UnknownRequestOrAnotherClientsRequest_Returns404()
    {
        var proposal = await ProposedAsync();
        var otherClient = await TestActors.Client(factory).CreateAsync(Ct);

        var unknown = await AcceptAsync(otherClient, Guid.NewGuid());
        var foreign = await AcceptAsync(otherClient, proposal.RequestId);

        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ProblemCodeAsync(unknown)).Should().Be("CLIENT_REQUEST_NOT_FOUND");
        foreign.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ProblemCodeAsync(foreign)).Should().Be("CLIENT_REQUEST_NOT_FOUND");
        (await StoredRequestAsync(proposal.RequestId)).Status.Should().Be(ClientRequestStatus.PackageProposed);
    }

    [Fact]
    public async Task Accept_ProfessionSlotTakenByAnotherCoach_Returns400AndKeepsTheProposal()
    {
        var proposal = await ProposedAsync();
        var otherCoach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await TestActors.Link(factory, otherCoach, proposal.Client).CreateAsync(Ct);

        var response = await AcceptAsync(proposal.Client, proposal.RequestId);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodesAsync(response)).Should().Contain("PROFESSION_ALREADY_OCCUPIED");
        (await StoredRequestAsync(proposal.RequestId)).Status.Should().Be(ClientRequestStatus.PackageProposed);
        (await LinkActiveAsync(proposal.Coach, proposal.Client)).Should().BeFalse();
    }

    [Fact]
    public async Task Accept_CoachRoleRemovedSincePropose_Returns403CoachRoleRemoved()
    {
        var proposal = await ProposedAsync();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var profile = await db.ProfessionalProfiles.SingleAsync(p => p.Id == proposal.Coach.ProfileId, Ct);
            profile.TrainerRoleRemovedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(Ct);
        }

        var response = await AcceptAsync(proposal.Client, proposal.RequestId);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCodesAsync(response)).Should().Contain("COACH_ROLE_REMOVED");
        (await LinkActiveAsync(proposal.Coach, proposal.Client)).Should().BeFalse();
    }

    [Fact]
    public async Task Accept_OpenProposal_CancelsTheClientsOtherOpenRequestsOfTheSameRole()
    {
        var proposal = await ProposedAsync();
        var otherCoach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var otherRequestId = await SendRequestAsync(otherCoach, proposal.Client);

        (await AcceptAsync(proposal.Client, proposal.RequestId)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await StoredRequestAsync(otherRequestId)).Status.Should().Be(ClientRequestStatus.Cancelled);
    }

    private record RequestCreated(Guid PublicId);
}
