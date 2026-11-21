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

namespace FormUp.Tests.Endpoints.Professionals;

/// <summary>
/// Search and public-profile discovery flags: Online, AcceptNewClients, the online filter, and the
/// invite-gated bypass of a hidden (ShowInSearch=false) profile.
/// </summary>
[Collection(TestCollection.Shard4)]
public class ProfessionalDiscoveryFlagsTests(FitnessApiFactory factory)
{
    private record SearchItem(Guid PublicId, bool Online, bool AcceptNewClients);

    private record SearchResult(List<SearchItem> Items);

    private record ProfileResult(Guid PublicId, bool Online, bool AcceptNewClients);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Actor> CoachAsync(string lastName, bool online = false, bool accepting = true, bool showInSearch = true)
    {
        var coach = await TestActors.Trainer(factory).WithName("Disc", lastName).CreateAsync(Ct);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var profile = await db.ProfessionalProfiles.FirstAsync(p => p.Id == coach.ProfileId, Ct);
        profile.Online = online;
        profile.AcceptNewClients = accepting;
        profile.ShowInSearch = showInSearch;
        await db.SaveChangesAsync(Ct);
        return coach;
    }

    private async Task SeedInviteAsync(Actor coach, string email, PendingInviteStatus status, DateTime sentAt)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.PendingInvites.Add(new PendingInvite
        {
            ProfessionalProfileId = coach.ProfileId,
            Email = email,
            SentAt = sentAt,
            Status = status,
        });
        await db.SaveChangesAsync(Ct);
    }

    private static async Task<List<SearchItem>> SearchAsync(Actor client, string query)
    {
        var response = await client.Http.GetAsync($"/professionals/search?{query}", Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<SearchResult>(cancellationToken: Ct))!.Items;
    }

    [Fact]
    public async Task Search_IncludesWaitlistedCoach_AndReturnsOnlineAndAcceptNewClients()
    {
        var lastName = $"Wait{Guid.NewGuid():N}";
        var coach = await CoachAsync(lastName, online: true, accepting: false);
        var client = await TestActors.Client(factory).CreateAsync(Ct);

        var items = await SearchAsync(client, $"search={lastName}");

        var item = items.Should().ContainSingle(i => i.PublicId == coach.PublicId).Subject;
        item.AcceptNewClients.Should().BeFalse();
        item.Online.Should().BeTrue();
    }

    [Fact]
    public async Task Search_OnlineFilter_NarrowsToMatchingFlag()
    {
        var lastName = $"Onl{Guid.NewGuid():N}";
        var online = await CoachAsync(lastName, online: true);
        var offline = await CoachAsync(lastName, online: false);
        var client = await TestActors.Client(factory).CreateAsync(Ct);

        (await SearchAsync(client, $"search={lastName}&online=true")).Select(i => i.PublicId)
            .Should().Equal(online.PublicId);
        (await SearchAsync(client, $"search={lastName}&online=false")).Select(i => i.PublicId)
            .Should().Equal(offline.PublicId);
        (await SearchAsync(client, $"search={lastName}")).Select(i => i.PublicId)
            .Should().BeEquivalentTo([online.PublicId, offline.PublicId]);
    }

    [Fact]
    public async Task GetPublicProfile_ReturnsOnlineAndAcceptNewClients()
    {
        var coach = await CoachAsync($"Prof{Guid.NewGuid():N}", online: true, accepting: false);
        var client = await TestActors.Client(factory).CreateAsync(Ct);

        var response = await client.Http.GetAsync($"/professionals/{coach.PublicId}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ProfileResult>(cancellationToken: Ct);
        body!.Online.Should().BeTrue();
        body.AcceptNewClients.Should().BeFalse();
    }

    [Fact]
    public async Task GetPublicProfile_HiddenCoach_WithLivePendingInviteToCaller_Returns200()
    {
        var coach = await CoachAsync($"Hid{Guid.NewGuid():N}", showInSearch: false);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await SeedInviteAsync(coach, client.Email.ToUpperInvariant(), PendingInviteStatus.Pending, DateTime.UtcNow);

        var response = await client.Http.GetAsync($"/professionals/{coach.PublicId}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetPublicProfile_HiddenCoach_WithoutInvite_Returns404()
    {
        var coach = await CoachAsync($"Hid{Guid.NewGuid():N}", showInSearch: false);
        var client = await TestActors.Client(factory).CreateAsync(Ct);

        (await client.Http.GetAsync($"/professionals/{coach.PublicId}", Ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(PendingInviteStatus.Declined)]
    [InlineData(PendingInviteStatus.Cancelled)]
    [InlineData(PendingInviteStatus.Accepted)]
    public async Task GetPublicProfile_HiddenCoach_WithAnsweredInvite_Returns404(PendingInviteStatus status)
    {
        var coach = await CoachAsync($"Hid{Guid.NewGuid():N}", showInSearch: false);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await SeedInviteAsync(coach, client.Email, status, DateTime.UtcNow);

        (await client.Http.GetAsync($"/professionals/{coach.PublicId}", Ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetPublicProfile_HiddenCoach_WithExpiredInvite_Returns404()
    {
        var coach = await CoachAsync($"Hid{Guid.NewGuid():N}", showInSearch: false);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await SeedInviteAsync(coach, client.Email, PendingInviteStatus.Pending, DateTime.UtcNow.AddDays(-8));

        (await client.Http.GetAsync($"/professionals/{coach.PublicId}", Ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetPublicProfile_HiddenCoach_WithInviteToAnotherEmail_Returns404()
    {
        var coach = await CoachAsync($"Hid{Guid.NewGuid():N}", showInSearch: false);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await SeedInviteAsync(coach, "someone-else@actor-fixture.com", PendingInviteStatus.Pending, DateTime.UtcNow);

        (await client.Http.GetAsync($"/professionals/{coach.PublicId}", Ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
