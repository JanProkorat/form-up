using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.PhotoDiaryRequests.ListClientRequests;
using FormUp.Application.Features.Trainers.PendingInvites.Create;
using FormUp.Application.Features.Trainers.PendingInvites.GetAll;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Trainers;

/// <summary>
/// An invite to a coaching account's email looks like any other invite to the inviter (listed,
/// counted, 409 on repeat, deletable) but creates no token, notification or conversation, and the
/// coach-role invitee can neither see nor act on it.
/// </summary>
[Collection(TestCollection.Shard2)]
public class SilentInviteIntegrationTests(FitnessApiFactory factory)
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static readonly JsonSerializerOptions EnumJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private async Task<(Actor Inviter, Actor Coach)> SetupAsync()
    {
        var inviter = await TestActors.Trainer(factory).CreateAsync(Ct);
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Client).CreateAsync(Ct);
        return (inviter, coach);
    }

    private async Task<CreatePendingInviteResponse> InviteAsync(Actor inviter, string email)
    {
        var response = await inviter.Http.PostAsJsonAsync(
            "/trainer/pending-invites", new { Email = email, Message = "Join me" }, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<CreatePendingInviteResponse>(Ct))!;
    }

    private async Task AssertNoSideEffectsAsync(Actor inviter, Actor coach)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        (await db.InvitationTokens.AnyAsync(t => t.ProfessionalProfileId == inviter.ProfileId, Ct))
            .Should().BeFalse("no token is issued for a silent invite");
        (await db.Notifications.AnyAsync(n => n.RecipientUserId == coach.UserId, Ct))
            .Should().BeFalse("the coach account is never notified");
        (await db.Conversations.AnyAsync(c => c.ProfessionalUserId == inviter.UserId, Ct))
            .Should().BeFalse("no conversation is seeded");
    }

    [Fact]
    public async Task CreateInvite_ForCoachEmail_ReturnsNormalSuccessAndStoresListedRow()
    {
        var (inviter, coach) = await SetupAsync();

        var created = await InviteAsync(inviter, coach.Email);

        created.Email.Should().Be(coach.Email);

        var list = await inviter.Http.GetFromJsonAsync<GetPendingInvitesResponse>("/trainer/pending-invites", EnumJson, Ct);
        list!.Invites.Should().ContainSingle(i => i.PublicId == created.PublicId && i.Status == PendingInviteStatus.Pending);

        await AssertNoSideEffectsAsync(inviter, coach);
    }

    [Fact]
    public async Task CreateInvite_RepeatForCoachEmail_Returns409Duplicate()
    {
        var (inviter, coach) = await SetupAsync();
        await InviteAsync(inviter, coach.Email);

        var second = await inviter.Http.PostAsJsonAsync("/trainer/pending-invites", new { Email = coach.Email }, Ct);

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await second.Content.ReadFromJsonAsync<ProblemDto>(Ct);
        problem!.ErrorCode.Should().Be("DUPLICATE_PENDING_INVITE");
    }

    [Fact]
    public async Task CreateInvite_SilentInviteCountsTowardOutstandingCap()
    {
        var (inviter, coach) = await SetupAsync();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            for (var i = 0; i < 199; i++)
            {
                db.PendingInvites.Add(new PendingInvite
                {
                    ProfessionalProfileId = inviter.ProfileId,
                    Email = $"filler-{i}-{Guid.NewGuid():N}@cap-test.com",
                    SentAt = DateTime.UtcNow,
                });
            }

            await db.SaveChangesAsync(Ct);
        }

        await InviteAsync(inviter, coach.Email);

        var overCap = await inviter.Http.PostAsJsonAsync(
            "/trainer/pending-invites", new { Email = $"{Guid.NewGuid():N}@cap-test.com" }, Ct);

        overCap.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        var problem = await overCap.Content.ReadFromJsonAsync<ProblemDto>(Ct);
        problem!.ErrorCode.Should().Be("TOO_MANY_PENDING_INVITES");
    }

    [Fact]
    public async Task DeleteInvite_SilentRow_Returns204WithoutNotifyingOrSeeding()
    {
        var (inviter, coach) = await SetupAsync();
        var created = await InviteAsync(inviter, coach.Email);

        var delete = await inviter.Http.DeleteAsync($"/trainer/pending-invites/{created.PublicId}", Ct);

        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await AssertNoSideEffectsAsync(inviter, coach);

        var list = await inviter.Http.GetFromJsonAsync<GetPendingInvitesResponse>("/trainer/pending-invites", EnumJson, Ct);
        list!.Invites.Should().NotContain(i => i.PublicId == created.PublicId);
    }

    [Fact]
    public async Task InviteeSide_CoachRoleCaller_SeesNothingAndCannotAct()
    {
        var (inviter, coach) = await SetupAsync();
        var created = await InviteAsync(inviter, coach.Email);

        var pending = await coach.Http.GetAsync("/client/invites/pending", Ct);
        var accept = await coach.Http.PostAsJsonAsync($"/client/invites/{created.PublicId}/accept", new { }, Ct);
        var decline = await coach.Http.PostAsJsonAsync($"/client/invites/{created.PublicId}/decline", new { }, Ct);
        var questionnaires = await coach.Http.GetAsync("/client/questionnaires/pending", Ct);

        pending.StatusCode.Should().Be(HttpStatusCode.NoContent);
        accept.StatusCode.Should().Be(HttpStatusCode.NotFound);
        decline.StatusCode.Should().Be(HttpStatusCode.NotFound);
        questionnaires.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.PendingInvites.SingleAsync(i => i.PublicId == created.PublicId, Ct)).Status
            .Should().Be(PendingInviteStatus.Pending, "a coach-role caller must not consume the silent row");
        (await db.ClientProfessionalLinks.AnyAsync(l => l.ProfessionalProfileId == inviter.ProfileId, Ct))
            .Should().BeFalse("no link may be created from a silent invite");
        await AssertNoSideEffectsAsync(inviter, coach);
    }

    [Fact]
    public async Task PhotoDiaryList_CoachRoleCaller_SkipsSilentInviteRows_ButRealInviteeSeesTheirs()
    {
        var (inviter, coach) = await SetupAsync();
        var realClient = await TestActors.Client(factory).CreateAsync(Ct);
        var silentInvite = await InviteAsync(inviter, coach.Email);
        var realInvite = await InviteAsync(inviter, realClient.Email);
        var silentInviteId = await InternalIdAsync(silentInvite);
        var realInviteId = await InternalIdAsync(realInvite);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.PhotoDiaryRequests.Add(new PhotoDiaryRequest
            {
                Id = Guid.NewGuid(),
                ProfessionalId = inviter.UserId,
                PendingInviteId = silentInviteId,
            });
            db.PhotoDiaryRequests.Add(new PhotoDiaryRequest
            {
                Id = Guid.NewGuid(),
                ProfessionalId = inviter.UserId,
                PendingInviteId = realInviteId,
            });
            await db.SaveChangesAsync(Ct);
        }

        var coachResponse = await coach.Http.GetAsync("/client/photo-diary-requests", Ct);
        var clientResponse = await realClient.Http.GetAsync("/client/photo-diary-requests", Ct);

        coachResponse.EnsureSuccessStatusCode();
        (await coachResponse.Content.ReadFromJsonAsync<ListClientRequestsResponse>(EnumJson, Ct))!.Items
            .Should().BeEmpty("a coach-role caller must not see requests hung on a silent invite");
        (await clientResponse.Content.ReadFromJsonAsync<ListClientRequestsResponse>(EnumJson, Ct))!.Items
            .Should().ContainSingle(r => r.PendingInviteId == realInviteId);
    }

    [Fact]
    public async Task ConversationContext_CoachRoleInvitee_GetsNoInviteBanner_ButRealInviteeDoes()
    {
        var (inviter, coach) = await SetupAsync();
        var realClient = await TestActors.Client(factory).CreateAsync(Ct);
        await InviteAsync(inviter, coach.Email);
        await InviteAsync(inviter, realClient.Email);

        Guid coachConversationId;
        Guid clientConversationId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var coachConversation = new Conversation { ProfessionalUserId = inviter.UserId, ClientUserId = coach.UserId };
            db.Conversations.Add(coachConversation);
            await db.SaveChangesAsync(Ct);
            coachConversationId = coachConversation.PublicId;

            // The real invitee is verified, so the invite already seeded its thread (#1100 C3).
            clientConversationId = await db.Conversations
                .Where(c => c.ProfessionalUserId == inviter.UserId && c.ClientUserId == realClient.UserId)
                .Select(c => c.PublicId)
                .SingleAsync(Ct);
        }

        var coachContext = await coach.Http.GetAsync($"/conversations/{coachConversationId}/context", Ct);
        var clientContext = await realClient.Http.GetAsync($"/conversations/{clientConversationId}/context", Ct);

        coachContext.StatusCode.Should().Be(HttpStatusCode.NoContent);
        clientContext.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PhotoDiaryRequest_AgainstSilentInvite_DoesNotNotifyTheCoachAccount_ButNotifiesARealInvitee()
    {
        var (inviter, coach) = await SetupAsync();
        var realClient = await TestActors.Client(factory).CreateAsync(Ct);
        var silentInvite = await InviteAsync(inviter, coach.Email);
        var realInvite = await InviteAsync(inviter, realClient.Email);
        var silentInviteId = await InternalIdAsync(silentInvite);
        var realInviteId = await InternalIdAsync(realInvite);
        var notifier = factory.Services.GetRequiredService<FakeRealtimeNotifier>();

        var toCoach = await inviter.Http.PostAsJsonAsync(
            "/trainer/photo-diary-requests", new { PendingInviteId = silentInviteId, Mode = "Workflow" }, Ct);
        var toClient = await inviter.Http.PostAsJsonAsync(
            "/trainer/photo-diary-requests", new { PendingInviteId = realInviteId, Mode = "Workflow" }, Ct);

        toCoach.StatusCode.Should().Be(HttpStatusCode.OK);
        toClient.StatusCode.Should().Be(HttpStatusCode.OK);
        notifier.Calls.Should().NotContain(c => c.UserId == coach.UserId && c.EventType == "photodiaryrequested");
        notifier.Calls.Should().Contain(c => c.UserId == realClient.UserId && c.EventType == "photodiaryrequested");
    }

    /// <summary>
    /// Resolves the internal long id of a created invite; the photo-diary API still takes it.
    /// </summary>
    private async Task<long> InternalIdAsync(CreatePendingInviteResponse invite)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.PendingInvites
            .Where(i => i.PublicId == invite.PublicId)
            .Select(i => i.Id)
            .SingleAsync(Ct);
    }

    private sealed class ProblemDto
    {
        public string? ErrorCode { get; set; }
    }
}
