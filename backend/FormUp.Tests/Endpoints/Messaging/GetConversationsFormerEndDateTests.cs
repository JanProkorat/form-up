using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Messaging;

/// <summary>
/// <c>GET /conversations</c> derives the former state and its end date from the pair's links,
/// since the stored flag is never set by production code.
/// </summary>
[Collection(TestCollection.Shard4)]
public class GetConversationsFormerEndDateTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task SetLinkUpdatedAtAsync(Guid linkPublicId, DateTime updatedAt)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.ClientProfessionalLinks
            .Where(l => l.PublicId == linkPublicId)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.DateUpdated, updatedAt), Ct);
    }

    private async Task<(Actor Coach, Actor Client)> PairWithConversationAsync()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await ConversationListSeed.SeedInviteAsync(factory, coach, client);
        return (coach, client);
    }

    [Fact]
    public async Task EndedLink_IsFormer_WithEndDate_ForBothCallers()
    {
        var (coach, client) = await PairWithConversationAsync();
        var link = TestActors.Link(factory, coach, client).Inactive();
        await link.CreateAsync(Ct);
        var endedAt = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        await SetLinkUpdatedAtAsync(link.PublicId, endedAt);

        var asClient = (await ConversationListSeed.GetAsync(client.Http)).Single();
        var asCoach = (await ConversationListSeed.GetAsync(coach.Http)).Single();

        asClient.IsFormer.Should().BeTrue();
        asClient.EndedAt.Should().Be(endedAt);
        asCoach.IsFormer.Should().BeTrue();
        asCoach.EndedAt.Should().Be(endedAt);
    }

    [Fact]
    public async Task ActiveLink_IsNotFormer_AndHasNoEndDate()
    {
        var (coach, client) = await PairWithConversationAsync();
        await TestActors.Link(factory, coach, client).CreateAsync(Ct);

        var row = (await ConversationListSeed.GetAsync(coach.Http)).Single();

        row.IsFormer.Should().BeFalse();
        row.EndedAt.Should().BeNull();
    }

    [Fact]
    public async Task NoLinkAtAll_IsNotFormer()
    {
        var (_, client) = await PairWithConversationAsync();

        var row = (await ConversationListSeed.GetAsync(client.Http)).Single();

        row.IsFormer.Should().BeFalse();
        row.EndedAt.Should().BeNull();
    }

    [Fact]
    public async Task StoredFormerFlag_IsStillHonoured_WithoutEndDate()
    {
        var (coach, client) = await PairWithConversationAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Conversations
                .Where(c => c.ClientUserId == client.UserId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.IsFormer, true), Ct);
        }

        var row = (await ConversationListSeed.GetAsync(client.Http)).Single();

        row.IsFormer.Should().BeTrue();
        row.EndedAt.Should().BeNull();
    }

    [Fact]
    public async Task OtherClientsEndedLink_DoesNotLeakIntoThisPair()
    {
        var (coach, client) = await PairWithConversationAsync();
        var other = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, other).Inactive().CreateAsync(Ct);

        var row = (await ConversationListSeed.GetAsync(coach.Http)).Single();

        row.IsFormer.Should().BeFalse();
        row.EndedAt.Should().BeNull();
    }
}
