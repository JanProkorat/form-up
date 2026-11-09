using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static FormUp.Tests.Endpoints.ClientPayments.ClientPaymentFixtures;
using static FormUp.Tests.Endpoints.PaymentSettings.PaymentSettingsFixtures;

namespace FormUp.Tests.Endpoints.Trainers;

/// <summary>
/// Integration tests for the package and discount an invite can carry (POST /trainer/pending-invites).
/// </summary>
[Collection(TestCollection.Name)]
public class CreatePendingInvitePackageIntegrationTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Actor> CoachWithPaymentsAsync()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await SubscribeAsync(factory, coach, canUsePayments: true);

        return coach;
    }

    private static string NewEmail() => $"{Guid.NewGuid():N}@invite-fixture.com";

    private static Task<HttpResponseMessage> InviteAsync(Actor coach, string email, Guid? packageId, Guid? discountId = null) =>
        coach.Http.PostAsJsonAsync("/trainer/pending-invites", new { Email = email, PackageId = packageId, DiscountId = discountId }, Ct);

    private async Task<bool> InviteStoredAsync(string email)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.PendingInvites.AnyAsync(pi => pi.Email == email, Ct);
    }

    [Fact]
    public async Task Create_WithPackageAndDiscount_StoresThemAndNamesThePackageInTheEmail()
    {
        var coach = await CoachWithPaymentsAsync();
        var packageId = await CreatePackageAsync(coach, 1500m, "Coaching 12", "EveryNWeeks", intervalWeeks: 4);
        var discountId = await CreateDiscountAsync(coach, 20m);
        var email = NewEmail();

        var response = await InviteAsync(coach, email, packageId, discountId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var invite = await db.PendingInvites
                .Include(pi => pi.CoachPackage)
                .Include(pi => pi.CoachDiscount)
                .SingleAsync(pi => pi.Email == email, Ct);
            invite.CoachPackage!.PublicId.Should().Be(packageId);
            invite.CoachDiscount!.PublicId.Should().Be(discountId);
        }

        var emailService = factory.Services.GetRequiredService<FakeEmailService>();
        await FakeEmailService.WaitForAsync(() => emailService.SentInvitations.Any(i => i.Email == email));
        var sent = emailService.SentInvitations.Single(i => i.Email == email);
        sent.CoachRole.Should().Be(InvitationCoachRole.Trainer, "a trainer-only coach's grant names the trainer role in the email");
        var package = sent.Package;
        package.Should().NotBeNull();
        package!.Name.Should().Be("Coaching 12");
        package.Billing.Should().Be(PackageBillingInterval.EveryNWeeks);
        package.IntervalWeeks.Should().Be(4);
    }

    [Fact]
    public async Task Create_WithoutPaymentsEntitlement_Returns403AndStoresNothing()
    {
        var coach = await CoachWithPaymentsAsync();
        var packageId = await CreatePackageAsync(coach);
        await SetSubscriptionStatusAsync(coach, SubscriptionStatus.Canceled);
        var email = NewEmail();

        var response = await InviteAsync(coach, email, packageId);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ProblemCodeAsync(response)).Should().Be("PAYMENTS_NOT_ENTITLED");
        (await InviteStoredAsync(email)).Should().BeFalse();
    }

    [Fact]
    public async Task Create_UnknownOrForeignPackageOrDiscount_Returns404()
    {
        var coach = await CoachWithPaymentsAsync();
        var other = await CoachWithPaymentsAsync();
        var ownPackage = await CreatePackageAsync(coach);
        var foreignPackage = await CreatePackageAsync(other);
        var foreignDiscount = await CreateDiscountAsync(other);

        var cases = new[]
        {
            await InviteAsync(coach, NewEmail(), Guid.NewGuid()),
            await InviteAsync(coach, NewEmail(), foreignPackage),
            await InviteAsync(coach, NewEmail(), ownPackage, Guid.NewGuid()),
            await InviteAsync(coach, NewEmail(), ownPackage, foreignDiscount)
        };

        cases.Should().OnlyContain(response => response.StatusCode == HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Create_ArchivedPackageArchivedDiscountOrDiscountNotCoveringThePackage_Returns409WithItsCode()
    {
        var coach = await CoachWithPaymentsAsync();
        var packageId = await CreatePackageAsync(coach, name: "Online coaching");
        var archivedPackage = await CreatePackageAsync(coach, name: "Old");
        var otherPackage = await CreatePackageAsync(coach, name: "Nutrition");
        var archivedDiscount = await CreateDiscountAsync(coach);
        var narrowDiscount = await CreateDiscountAsync(coach, packageIds: [otherPackage]);
        await coach.Http.PostAsync($"/payments/packages/{archivedPackage}/archive", Empty(), Ct);
        await coach.Http.PostAsync($"/payments/discounts/{archivedDiscount}/archive", Empty(), Ct);

        var email = NewEmail();
        var cases = new (HttpResponseMessage Response, string Code)[]
        {
            (await InviteAsync(coach, email, archivedPackage), "PACKAGE_ARCHIVED"),
            (await InviteAsync(coach, email, packageId, archivedDiscount), "DISCOUNT_ARCHIVED"),
            (await InviteAsync(coach, email, packageId, narrowDiscount), "DISCOUNT_PACKAGE_INVALID")
        };

        foreach (var (response, code) in cases)
        {
            response.StatusCode.Should().Be(HttpStatusCode.Conflict, code);
            (await ProblemCodeAsync(response)).Should().Be(code);
        }

        (await InviteStoredAsync(email)).Should().BeFalse("a rejected invite is neither saved nor emailed");
    }

    [Fact]
    public async Task Create_FixedDiscountInAnotherCurrencyThanThePackage_Returns400CurrencyMismatch()
    {
        var coach = await CoachWithPaymentsAsync();
        var euroPackage = await CreatePackageAsync(coach, 100m, currency: "EUR");
        var crownDiscount = await CreateDiscountAsync(coach, 20m, type: "FixedAmount", currency: "CZK");

        var response = await InviteAsync(coach, NewEmail(), euroPackage, crownDiscount);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ProblemCodeAsync(response)).Should().Be("CURRENCY_MISMATCH");
    }

    private async Task SetSubscriptionStatusAsync(Actor coach, SubscriptionStatus status)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var subscription = await db.CoachSubscriptions.SingleAsync(cs => cs.ProfessionalProfileId == coach.ProfileId, Ct);
        subscription.Status = status;
        await db.SaveChangesAsync(Ct);
    }
}
