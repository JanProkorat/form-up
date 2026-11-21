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
using static FormUp.Tests.Endpoints.ClientPayments.ClientPaymentFixtures;
using static FormUp.Tests.Endpoints.PaymentSettings.PaymentSettingsFixtures;

namespace FormUp.Tests.Endpoints.Client.Invites;

/// <summary>
/// Integration tests for <c>GET /client/invites</c>.
/// </summary>
[Collection(TestCollection.Shard4)]
public class GetClientInvitesEndpointTests(FitnessApiFactory factory)
{
    private record InvitesResult(List<InviteItem> Items);

    private record InviteItem(
        Guid Id, Guid CoachPublicId, string CoachName, string CoachRole, string CoachCity,
        string? OfferedType, string? Message, DateTime SentAt, DateTime ExpiresAt, InvitePackage? Package = null);

    private record InvitePackage(
        string Name, string Billing, int? IntervalWeeks, int? RunsForPayments, string? Description,
        decimal BasePrice, string? DiscountLabel, decimal FinalPrice, string Currency);

    private record PendingResult(Guid Id, InvitePackage? Package);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Guid> SeedInviteAsync(
        Actor coach, string email, PendingInviteStatus status, DateTime sentAt, string? message = null,
        Guid? packageId = null, Guid? discountId = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var invite = new PendingInvite
        {
            ProfessionalProfileId = coach.ProfileId,
            Email = email,
            Message = message,
            SentAt = sentAt,
            Status = status,
            CoachPackageId = packageId is null ? null : (await db.CoachPackages.SingleAsync(p => p.PublicId == packageId, Ct)).Id,
            CoachDiscountId = discountId is null ? null : (await db.CoachDiscounts.SingleAsync(d => d.PublicId == discountId, Ct)).Id,
        };
        db.PendingInvites.Add(invite);
        await db.SaveChangesAsync(Ct);
        return invite.PublicId;
    }

    private static async Task<List<InviteItem>> GetAsync(Actor caller)
    {
        var response = await caller.Http.GetAsync("/client/invites", Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<InvitesResult>(cancellationToken: Ct))!.Items;
    }

    [Fact]
    public async Task GetAll_ReturnsOnlyPendingUnexpiredInvites_NewestFirst_WithCoachDetails()
    {
        var coach = await TestActors.Trainer(factory).WithName("Coach", "Carl").CreateAsync(Ct);
        var otherCoach = await TestActors.Trainer(factory).WithName("Other", "Olga").CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var now = DateTime.UtcNow;

        var older = await SeedInviteAsync(coach, client.Email.ToUpperInvariant(), PendingInviteStatus.Pending, now.AddDays(-2), "Hi");
        var newer = await SeedInviteAsync(otherCoach, client.Email, PendingInviteStatus.Pending, now.AddHours(-1));
        await SeedInviteAsync(coach, client.Email, PendingInviteStatus.Declined, now.AddDays(-1));
        await SeedInviteAsync(coach, client.Email, PendingInviteStatus.Accepted, now.AddDays(-1));
        await SeedInviteAsync(coach, client.Email, PendingInviteStatus.Cancelled, now.AddDays(-1));
        await SeedInviteAsync(coach, client.Email, PendingInviteStatus.Pending, now.AddDays(-8));
        await SeedInviteAsync(coach, "someone-else@actor-fixture.com", PendingInviteStatus.Pending, now);

        var items = await GetAsync(client);

        items.Select(i => i.Id).Should().Equal(newer, older);
        var first = items[1];
        first.CoachPublicId.Should().Be(coach.PublicId);
        first.CoachName.Should().Be("Coach Carl");
        first.CoachRole.Should().Be("Trainer");
        first.Message.Should().Be("Hi");
        first.ExpiresAt.Should().BeCloseTo(first.SentAt.AddDays(7), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task GetAll_CoachRoleLabel_ListsActiveRolesOnly_AndBatchesAcrossCoaches()
    {
        var both = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var trainerOnly = await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var now = DateTime.UtcNow;

        var bothInvite = await SeedInviteAsync(both, client.Email, PendingInviteStatus.Pending, now.AddHours(-3));
        var trainerInvite = await SeedInviteAsync(trainerOnly, client.Email, PendingInviteStatus.Pending, now.AddHours(-2));

        var beforeRemoval = (await GetAsync(client)).Single(i => i.Id == bothInvite);
        beforeRemoval.CoachRole.Should().Be("Trainer & Nutritionist");
        beforeRemoval.OfferedType.Should().Be("Both");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var profile = await db.ProfessionalProfiles.FindAsync([both.ProfileId], Ct);
            profile!.NutritionistRoleRemovedAt = now;
            await db.SaveChangesAsync(Ct);
        }

        var items = await GetAsync(client);

        items.Single(i => i.Id == bothInvite).CoachRole.Should().Be("Trainer", "a removed role is not an active one");
        items.Single(i => i.Id == bothInvite).OfferedType.Should().Be("TrainingOnly");
        items.Single(i => i.Id == trainerInvite).CoachRole.Should().Be("Trainer");
        items.Single(i => i.Id == trainerInvite).OfferedType.Should().Be("TrainingOnly");
    }

    [Fact]
    public async Task GetAll_ForCoachRoleCaller_ReturnsEmptyList()
    {
        var inviter = await TestActors.Trainer(factory).CreateAsync(Ct);

        // Roles(Client) gates the route, so a coach that also holds the Client role is the realistic caller.
        var dual = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Client).CreateAsync(Ct);
        await SeedInviteAsync(inviter, dual.Email, PendingInviteStatus.Pending, DateTime.UtcNow);

        (await GetAsync(dual)).Should().BeEmpty();
    }

    [Fact]
    public async Task GetAll_InviteWithPackage_ShowsThePriceAndDiscount_AndNothingOnceItCannotBeGiven()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await SubscribeAsync(factory, coach, canUsePayments: true);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var packageId = await CreatePackageAsync(coach, 2500m, "Coaching 12", runsFor: 12);
        var discountId = await CreateDiscountAsync(coach, 30m);
        var inviteId = await SeedInviteAsync(
            coach, client.Email, PendingInviteStatus.Pending, DateTime.UtcNow, packageId: packageId, discountId: discountId);

        var package = (await GetAsync(client)).Single(i => i.Id == inviteId).Package;

        package.Should().NotBeNull();
        package!.Name.Should().Be("Coaching 12");
        package.Billing.Should().Be("Monthly");
        package.RunsForPayments.Should().Be(12);
        package.BasePrice.Should().Be(2500m);
        package.FinalPrice.Should().Be(1750m);
        package.DiscountLabel.Should().Contain("30");
        package.Currency.Should().Be("CZK");

        var pending = await client.Http.GetFromJsonAsync<PendingResult>("/client/invites/pending", Ct);
        pending!.Package.Should().BeEquivalentTo(package);

        await coach.Http.PostAsync($"/payments/packages/{packageId}/archive", Empty(), Ct);

        (await GetAsync(client)).Single(i => i.Id == inviteId).Package.Should().BeNull("accepting would no longer give it");
    }
}
