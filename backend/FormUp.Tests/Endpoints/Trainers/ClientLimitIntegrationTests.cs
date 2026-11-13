using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Trainers;

/// <summary>
/// The active-client limit of a coach's plan: refused at invite creation and at every point a link becomes active,
/// pending invites never count, and a client who already has an active link takes no new slot.
/// </summary>
[Collection(TestCollection.Name)]
public class ClientLimitIntegrationTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A coach on a plan that allows one active client, with one active client linked already.</summary>
    private async Task<(Actor Coach, Actor LinkedClient)> CoachAtTheLimitAsync()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeAsync(
            factory, coach, canUseWeeklyCheckIns: false, canUsePerClientCheckInConfig: false, maxActiveClients: 1);
        var linkedClient = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, linkedClient).CreateAsync(Ct);

        return (coach, linkedClient);
    }

    private static async Task AssertClientLimitAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.ClientLimitReached);
    }

    private static Task<HttpResponseMessage> InviteAsync(Actor coach, string email) =>
        coach.Http.PostAsJsonAsync("/trainer/pending-invites", new { Email = email }, Ct);

    private static async Task<Guid> InviteOkAsync(Actor coach, string email)
    {
        var response = await InviteAsync(coach, email);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return (await response.Content.ReadFromJsonAsync<InviteCreated>(Ct))!.PublicId;
    }

    private async Task<bool> LinkExistsAsync(Actor coach, Actor client)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.ClientProfessionalLinks.AnyAsync(
            l => l.ClientProfileId == client.ProfileId && l.ProfessionalProfileId == coach.ProfileId && l.ConnectionState != ConnectionState.Ended, Ct);
    }

    private async Task<Guid> AddClientRequestAsync(Actor coach, Actor client, ClientRequestStatus status)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var request = new ClientRequest
        {
            PublicId = Guid.NewGuid(),
            ClientProfileId = client.ProfileId,
            ProfessionalProfileId = coach.ProfileId,
            Status = status,
        };
        db.ClientRequests.Add(request);
        await db.SaveChangesAsync(Ct);

        return request.PublicId;
    }

    [Fact]
    public async Task CreateInvite_CoachAtTheLimit_Returns403ClientLimitReached()
    {
        var (coach, _) = await CoachAtTheLimitAsync();

        var response = await InviteAsync(coach, $"{Guid.NewGuid():N}@limit-fixture.com");

        await AssertClientLimitAsync(response);
    }

    [Fact]
    public async Task CreateInvite_PendingInvitesDoNotCountTowardTheLimit()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeAsync(
            factory, coach, canUseWeeklyCheckIns: false, canUsePerClientCheckInConfig: false, maxActiveClients: 1);

        await InviteOkAsync(coach, $"{Guid.NewGuid():N}@limit-fixture.com");
        await InviteOkAsync(coach, $"{Guid.NewGuid():N}@limit-fixture.com");
        await InviteOkAsync(coach, $"{Guid.NewGuid():N}@limit-fixture.com");
    }

    [Fact]
    public async Task InviteAndAccept_ClientWhoIsAlreadyLinked_TakesNoNewSlot()
    {
        var (coach, linkedClient) = await CoachAtTheLimitAsync();

        var inviteId = await InviteOkAsync(coach, linkedClient.Email);
        var accept = await linkedClient.Http.PostAsJsonAsync($"/client/invites/{inviteId}/accept", new { }, Ct);

        accept.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await LinkExistsAsync(coach, linkedClient)).Should().BeTrue();
    }

    [Fact]
    public async Task AcceptInvite_CoachFilledTheLimitAfterInviting_Returns403AndCreatesNoLink()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeAsync(
            factory, coach, canUseWeeklyCheckIns: false, canUsePerClientCheckInConfig: false, maxActiveClients: 1);
        var invitedClient = await TestActors.Client(factory).CreateAsync(Ct);
        var inviteId = await InviteOkAsync(coach, invitedClient.Email);
        var otherClient = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, otherClient).CreateAsync(Ct);

        var response = await invitedClient.Http.PostAsJsonAsync($"/client/invites/{inviteId}/accept", new { }, Ct);

        await AssertClientLimitAsync(response);
        (await LinkExistsAsync(coach, invitedClient)).Should().BeFalse();
    }

    [Fact]
    public async Task AcceptClientRequest_CoachAtTheLimit_Returns403AndCreatesNoLink()
    {
        var (coach, _) = await CoachAtTheLimitAsync();
        var requester = await TestActors.Client(factory).CreateAsync(Ct);
        var requestId = await AddClientRequestAsync(coach, requester, ClientRequestStatus.Pending);

        var response = await coach.Http.PostAsJsonAsync($"/trainer/client-requests/{requestId}/accept", new { }, Ct);

        await AssertClientLimitAsync(response);
        (await LinkExistsAsync(coach, requester)).Should().BeFalse();
    }

    [Fact]
    public async Task AcceptInvitationToken_CoachAtTheLimit_Returns403AndCreatesNoLink()
    {
        var (coach, _) = await CoachAtTheLimitAsync();
        var invitedClient = await TestActors.Client(factory).CreateAsync(Ct);
        var token = Guid.NewGuid().ToString("N");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.InvitationTokens.Add(new InvitationToken
            {
                ProfessionalProfileId = coach.ProfileId,
                Email = invitedClient.Email,
                Token = token,
                ExpiresAt = DateTime.UtcNow.AddDays(7),
            });
            await db.SaveChangesAsync(Ct);
        }

        var response = await invitedClient.Http.PostAsJsonAsync("/auth/invite/accept", new { Token = token }, Ct);

        await AssertClientLimitAsync(response);
        (await LinkExistsAsync(coach, invitedClient)).Should().BeFalse();
    }

    [Fact]
    public async Task AcceptProposal_CoachAtTheLimit_Returns403AndCreatesNoLink()
    {
        var (coach, _) = await CoachAtTheLimitAsync();
        var requester = await TestActors.Client(factory).CreateAsync(Ct);
        var requestId = await AddClientRequestAsync(coach, requester, ClientRequestStatus.PackageProposed);

        var response = await requester.Http.PostAsJsonAsync($"/client/requests/{requestId}/accept-proposal", new { }, Ct);

        await AssertClientLimitAsync(response);
        (await LinkExistsAsync(coach, requester)).Should().BeFalse();
    }

    private record InviteCreated(Guid PublicId);
}
