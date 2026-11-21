using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Endpoints.Messaging;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Domain.Services;

/// <summary>
/// <see cref="CooperationStatusResolver"/> against a real database: status mapping, expiry at a
/// pinned clock, and ids that match no row.
/// </summary>
[Collection(TestCollection.Shard2)]
public class CooperationStatusResolverTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Dictionary<Guid, CooperationSourceState>> ResolveAsync(DateTime now, params Guid[] ids)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await CooperationStatusResolver.ResolveAsync(db, ids, now, Ct);
    }

    [Fact]
    public async Task EmptyIds_ReturnEmpty()
    {
        (await ResolveAsync(DateTime.UtcNow)).Should().BeEmpty();
    }

    [Fact]
    public async Task UnknownId_IsAbsent()
    {
        (await ResolveAsync(DateTime.UtcNow, Guid.NewGuid())).Should().BeEmpty();
    }

    [Fact]
    public async Task EveryStoredStatus_MapsToCardStatus()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var seeded = new List<(Func<Guid> Id, CooperationCardStatus Expected)>();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var inviteMap = new[]
            {
                (PendingInviteStatus.Pending, CooperationCardStatus.Pending),
                (PendingInviteStatus.Accepted, CooperationCardStatus.Accepted),
                (PendingInviteStatus.Declined, CooperationCardStatus.Declined),
                (PendingInviteStatus.Cancelled, CooperationCardStatus.Cancelled),
            };
            foreach (var (stored, expected) in inviteMap)
            {
                var invite = new Application.Domain.Entities.PendingInvite
                {
                    ProfessionalProfileId = coach.ProfileId, Email = client.Email, Status = stored,
                };
                db.PendingInvites.Add(invite);
                seeded.Add((() => invite.PublicId, expected));
            }

            var requestMap = new[]
            {
                (ClientRequestStatus.Pending, CooperationCardStatus.Pending),
                (ClientRequestStatus.Accepted, CooperationCardStatus.Accepted),
                (ClientRequestStatus.Rejected, CooperationCardStatus.Declined),
                (ClientRequestStatus.Cancelled, CooperationCardStatus.Cancelled),
            };
            foreach (var (stored, expected) in requestMap)
            {
                var request = new Application.Domain.Entities.ClientRequest
                {
                    ClientProfileId = client.ProfileId, ProfessionalProfileId = coach.ProfileId, Status = stored,
                };
                db.ClientRequests.Add(request);
                seeded.Add((() => request.PublicId, expected));
            }

            await db.SaveChangesAsync(Ct);
        }

        var all = seeded.ToDictionary(x => x.Id(), x => x.Expected);
        var result = await ResolveAsync(DateTime.UtcNow, [.. all.Keys]);

        result.Should().HaveCount(all.Count);
        foreach (var (id, expected) in all)
        {
            result[id].Status.Should().Be(expected);
        }
    }

    [Fact]
    public async Task PendingInvite_ExpiresAtPinnedClock_AndCarriesScope()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var sentAt = DateTime.UtcNow;
        var inviteId = await ConversationListSeed.SeedInviteAsync(
            factory, coach, client, scope: LinkCapabilityScope.TrainingOnly, sentAt: sentAt, appendEvent: false);

        var fresh = await ResolveAsync(sentAt.AddDays(6), inviteId);
        var stale = await ResolveAsync(sentAt.AddDays(8), inviteId);

        fresh[inviteId].Status.Should().Be(CooperationCardStatus.Pending);
        stale[inviteId].Status.Should().Be(CooperationCardStatus.Expired);
        stale[inviteId].RequestedScope.Should().Be(LinkCapabilityScope.TrainingOnly);
    }

    [Fact]
    public async Task RejectedRequest_ReadsDeclined_WithoutScope()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var requestId = await ConversationListSeed.SeedRequestAsync(factory, coach, client, ClientRequestStatus.Rejected);

        var result = await ResolveAsync(DateTime.UtcNow, requestId);

        result[requestId].Status.Should().Be(CooperationCardStatus.Declined);
        result[requestId].RequestedScope.Should().BeNull();
    }
}
