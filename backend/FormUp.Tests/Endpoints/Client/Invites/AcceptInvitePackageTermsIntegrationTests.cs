using System.Net;
using System.Net.Http.Json;
using System.Text;
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
/// Integration tests for the package an invite carries being given to the client when they accept, through the
/// in-app accept and the emailed-token accept.
/// </summary>
[Collection(TestCollection.Name)]
public class AcceptInvitePackageTermsIntegrationTests(FitnessApiFactory factory)
{
    private record CreatedInvite(Guid PublicId);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Actor> CoachWithPaymentsAsync()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await SubscribeAsync(factory, coach, canUsePayments: true);

        return coach;
    }

    private static async Task<Guid> SendInviteAsync(Actor coach, Actor client, Guid packageId, Guid? discountId = null)
    {
        var response = await coach.Http.PostAsJsonAsync(
            "/trainer/pending-invites",
            new { Email = client.Email, PackageId = packageId, DiscountId = discountId },
            Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return (await response.Content.ReadFromJsonAsync<CreatedInvite>(Ct))!.PublicId;
    }

    private static Task<HttpResponseMessage> AcceptInAppAsync(Actor client, Guid inviteId) =>
        client.Http.PostAsync($"/client/invites/{inviteId}/accept", new StringContent("{}", Encoding.UTF8, "application/json"), Ct);

    private async Task<List<ClientPaymentTerms>> TermsOfAsync(Actor coach, Actor client)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.ClientPaymentTerms
            .AsNoTracking()
            .Include(t => t.CoachPackage)
            .Where(t => t.ClientPaymentAssignment.ProfessionalProfileId == coach.ProfileId
                && t.ClientPaymentAssignment.ClientProfileId == client.ProfileId)
            .ToListAsync(Ct);
    }

    private async Task<List<DateOnly>> PeriodStartsOfAsync(Actor coach, Actor client)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.ClientPayments
            .AsNoTracking()
            .Where(p => p.ClientPaymentAssignment.ProfessionalProfileId == coach.ProfileId
                && p.ClientPaymentAssignment.ClientProfileId == client.ProfileId
                && p.PeriodStart != null)
            .OrderBy(p => p.PeriodStart)
            .Select(p => p.PeriodStart!.Value)
            .ToListAsync(Ct);
    }

    private async Task<bool> LinkExistsAsync(Actor coach, Actor client)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.ClientProfessionalLinks.AnyAsync(
            l => l.ClientProfileId == client.ProfileId && l.ProfessionalProfileId == coach.ProfileId, Ct);
    }

    private async Task<bool> SkipAuditedAsync(Guid inviteId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.AuditLogs.AnyAsync(a => a.EntityId == inviteId && a.Action == "InvitePackageTermsSkipped", Ct);
    }

    [Fact]
    public async Task AcceptInApp_WithUsablePackageAndDiscount_StoresTermsAndStartsBillingToday()
    {
        var coach = await CoachWithPaymentsAsync();
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var packageId = await CreatePackageAsync(coach, 2500m);
        var discountId = await CreateDiscountAsync(coach, 30m);
        var inviteId = await SendInviteAsync(coach, client, packageId, discountId);

        var response = await AcceptInAppAsync(client, inviteId);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var terms = (await TermsOfAsync(coach, client)).Should().ContainSingle().Subject;
        terms.CoachPackage.PublicId.Should().Be(packageId);
        terms.CoachDiscountId.Should().NotBeNull();
        (await PeriodStartsOfAsync(coach, client)).First().Should().Be(Today);
    }

    [Fact]
    public async Task AcceptByToken_WithUsablePackage_Returns200AndMaterializesTheFirstPeriodToday()
    {
        var coach = await CoachWithPaymentsAsync();
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var packageId = await CreatePackageAsync(coach, 900m, "Starter");
        await SendInviteAsync(coach, client, packageId);
        var emailService = factory.Services.GetRequiredService<FakeEmailService>();
        await FakeEmailService.WaitForAsync(() => emailService.SentInvitations.Any(i => i.Email == client.Email));
        var token = emailService.SentInvitations.Single(i => i.Email == client.Email).Token;

        var response = await client.Http.PostAsJsonAsync("/auth/invite/accept", new { Token = token }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await TermsOfAsync(coach, client)).Should().ContainSingle().Which.CoachPackage.PublicId.Should().Be(packageId);
        (await PeriodStartsOfAsync(coach, client)).First().Should().Be(Today);
    }

    [Fact]
    public async Task AcceptInApp_PackageArchivedAfterSending_Returns204LinksTheClientAndSkipsTheTerms()
    {
        var coach = await CoachWithPaymentsAsync();
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var packageId = await CreatePackageAsync(coach);
        var inviteId = await SendInviteAsync(coach, client, packageId);
        await coach.Http.PostAsync($"/payments/packages/{packageId}/archive", Empty(), Ct);

        var response = await AcceptInAppAsync(client, inviteId);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await LinkExistsAsync(coach, client)).Should().BeTrue();
        (await TermsOfAsync(coach, client)).Should().BeEmpty();
        (await SkipAuditedAsync(inviteId)).Should().BeTrue();
    }

    [Fact]
    public async Task AcceptInApp_CoachLostPaymentsAfterSending_Returns204LinksTheClientAndSkipsTheTerms()
    {
        var coach = await CoachWithPaymentsAsync();
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var packageId = await CreatePackageAsync(coach);
        var inviteId = await SendInviteAsync(coach, client, packageId);

        await CoachSubscriptionBuilder.MoveToPlanWithoutPaymentsAsync(factory, coach);

        var response = await AcceptInAppAsync(client, inviteId);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await LinkExistsAsync(coach, client)).Should().BeTrue();
        (await TermsOfAsync(coach, client)).Should().BeEmpty();
        (await SkipAuditedAsync(inviteId)).Should().BeTrue();
    }

    [Fact]
    public async Task AcceptInApp_WhenTheClientIsAlreadyLinked_LeavesTheExistingTermsUntouched()
    {
        var world = await CreateWorldAsync(factory);
        var current = await CreatePackageAsync(world.Coach, 2500m, "Current");
        var offered = await CreatePackageAsync(world.Coach, 100m, "Offered");
        await SetTermsOkAsync(world, current);
        var inviteId = await SendInviteAsync(world.Coach, world.Client, offered);

        var response = await AcceptInAppAsync(world.Client, inviteId);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var terms = (await TermsOfAsync(world.Coach, world.Client)).Should().ContainSingle().Subject;
        terms.CoachPackage.PublicId.Should().Be(current, "an existing link keeps what the coach set on the Payments tab");
    }
}
