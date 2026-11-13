using System.Net;
using System.Net.Http.Json;
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
/// <c>POST /client/requests</c> with an optional package of the coach.
/// </summary>
[Collection(TestCollection.Name)]
public class SendClientRequestPackageTests(FitnessApiFactory factory)
{
    private const string UnavailableCode = "REQUEST_PACKAGE_UNAVAILABLE";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Actor> CoachWithPaymentsAsync()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await SubscribeAsync(factory, coach, canUsePayments: true);

        return coach;
    }

    private async Task<bool> RequestExistsAsync(Actor client)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.ClientRequests.AnyAsync(r => r.ClientProfileId == client.ProfileId, Ct);
    }

    private async Task AssertRefusedAsync(Actor coach, Actor client, Guid packageId)
    {
        var response = await client.Http.PostAsJsonAsync(
            "/client/requests", new { ProfessionalPublicId = coach.PublicId, PackagePublicId = packageId }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(UnavailableCode);
        (await RequestExistsAsync(client)).Should().BeFalse();
    }

    [Fact]
    public async Task SendRequest_WithProfilePackage_StoresThePackage()
    {
        var coach = await CoachWithPaymentsAsync();
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var packageId = await CreatePackageAsync(coach);

        var response = await client.Http.PostAsJsonAsync(
            "/client/requests", new { ProfessionalPublicId = coach.PublicId, PackagePublicId = packageId }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stored = await db.ClientRequests
            .Include(r => r.CoachPackage)
            .SingleAsync(r => r.ClientProfileId == client.ProfileId, Ct);
        stored.CoachPackage!.PublicId.Should().Be(packageId);
        stored.CoachDiscountId.Should().BeNull();
    }

    [Fact]
    public async Task SendRequest_WithoutPackage_StoresNoPackage()
    {
        var coach = await CoachWithPaymentsAsync();
        var client = await TestActors.Client(factory).CreateAsync(Ct);

        var response = await client.Http.PostAsJsonAsync(
            "/client/requests", new { ProfessionalPublicId = coach.PublicId }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.ClientRequests.SingleAsync(r => r.ClientProfileId == client.ProfileId, Ct))
            .CoachPackageId.Should().BeNull();
    }

    [Fact]
    public async Task SendRequest_WithPackageOfAnotherCoach_Returns400AndStoresNothing()
    {
        var coach = await CoachWithPaymentsAsync();
        var otherCoach = await CoachWithPaymentsAsync();
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var foreignPackageId = await CreatePackageAsync(otherCoach);

        await AssertRefusedAsync(coach, client, foreignPackageId);
    }

    [Fact]
    public async Task SendRequest_WithUnknownPackage_Returns400WithTheSameCode()
    {
        var coach = await CoachWithPaymentsAsync();
        var client = await TestActors.Client(factory).CreateAsync(Ct);

        await AssertRefusedAsync(coach, client, Guid.NewGuid());
    }

    [Fact]
    public async Task SendRequest_WithArchivedPackage_Returns400()
    {
        var coach = await CoachWithPaymentsAsync();
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var packageId = await CreatePackageAsync(coach);
        (await coach.Http.PostAsync($"/payments/packages/{packageId}/archive", Empty(), Ct))
            .IsSuccessStatusCode.Should().BeTrue();

        await AssertRefusedAsync(coach, client, packageId);
    }

    [Fact]
    public async Task SendRequest_WithPackageHiddenFromProfile_Returns400()
    {
        var coach = await CoachWithPaymentsAsync();
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var packageId = await CreatePackageAsync(coach);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var package = await db.CoachPackages.SingleAsync(p => p.PublicId == packageId, Ct);
            package.ShowOnProfile = false;
            await db.SaveChangesAsync(Ct);
        }

        await AssertRefusedAsync(coach, client, packageId);
    }

    [Fact]
    public async Task SendRequest_PackageInAnotherCurrencyThanTheProfile_Returns400()
    {
        var coach = await CoachWithPaymentsAsync();
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var packageId = await CreatePackageAsync(coach, currency: "EUR");

        await AssertRefusedAsync(coach, client, packageId);
    }

    [Fact]
    public async Task SendRequest_CoachWithoutPayments_Returns400()
    {
        var coach = await CoachWithPaymentsAsync();
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var packageId = await CreatePackageAsync(coach);
        await CoachSubscriptionBuilder.MoveToPlanWithoutPaymentsAsync(factory, coach);

        await AssertRefusedAsync(coach, client, packageId);
    }
}
