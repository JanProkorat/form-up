using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Endpoints.Billing;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Users;

/// <summary>
/// Integration tests for <c>POST /users/me/coach-account/disable</c> and the date fields it feeds.
/// </summary>
[Collection(TestCollection.Shard3)]
public class DisableCoachAccountIntegrationTests(FitnessApiFactory factory) : IDisposable
{
    /// <summary>Leaves the shared fake provider in its defaults for the next test class.</summary>
    public void Dispose() => BillingTestData.ResetProvider(factory);

    private record DisableResult(DateTime ActiveUntil, List<string> RolesRemoved);

    private record RolesResult(DateTime? CoachAccountActiveUntil, DateTime? ActiveUntilIfDisabled);

    private record ProfileResult(DateTime? CoachAccountActiveUntil);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static async Task SeedSubscriptionAsync(
        FitnessApiFactory factory,
        long profileId,
        SubscriptionStatus status,
        DateTimeOffset? periodEndsAt,
        DateTimeOffset? trialEndsAt = null,
        bool paysSecondRoleAddOn = false)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var plan = new SubscriptionPlan
        {
            Code = $"plan-{Guid.NewGuid():N}",
            NameCs = "Plan",
            NameEn = "Plan",
            NameDe = "Plan",
            Currency = "CZK",
        };
        var price = SubscriptionPlanPriceBuilder.OpenPriceFor(plan);
        db.SubscriptionPlans.Add(plan);
        await db.SaveChangesAsync(Ct);

        db.CoachSubscriptions.Add(new CoachSubscription
        {
            ProfessionalProfileId = profileId,
            SubscriptionPlanId = plan.Id,
            SubscriptionPlanPriceId = price.Id,
            Status = status,
            CurrentPeriodEndsAt = periodEndsAt,
            TrialEndsAt = trialEndsAt,
            PaysSecondRoleAddOn = paysSecondRoleAddOn,
        });
        await db.SaveChangesAsync(Ct);
    }

    private async Task<ProfessionalProfile> ReadProfileAsync(Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.ProfessionalProfiles.AsNoTracking().FirstAsync(p => p.UserId == userId, Ct);
    }

    private async Task<bool> ReadCancelAtPeriodEndAsync(long profileId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.CoachSubscriptions.AsNoTracking()
            .Where(cs => cs.ProfessionalProfileId == profileId)
            .Select(cs => cs.CancelAtPeriodEnd)
            .FirstAsync(Ct);
    }

    private async Task<int> CountRoleRemovedNotificationsAsync(Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Notifications.AsNoTracking()
            .CountAsync(n => n.RecipientUserId == userId && n.Type == NotificationType.CoachRoleRemoved, Ct);
    }

    [Fact]
    public async Task Disable_PaidSubscription_MarksNotRenewing_AndKeepsRolesUntilPeriodEnd()
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var periodEnd = DateTimeOffset.UtcNow.AddDays(20);
        await SeedSubscriptionAsync(factory, coach.ProfileId, SubscriptionStatus.Active, periodEnd);

        var response = await coach.Http.PostAsync("/users/me/coach-account/disable", null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<DisableResult>(cancellationToken: Ct);
        body!.ActiveUntil.Should().BeCloseTo(periodEnd.UtcDateTime, TimeSpan.FromSeconds(1));
        body.RolesRemoved.Should().BeEmpty();

        var profile = await ReadProfileAsync(coach.UserId);
        profile.CoachAccountActiveUntil.Should().BeCloseTo(periodEnd.UtcDateTime, TimeSpan.FromSeconds(1));
        profile.TrainerRoleRemovedAt.Should().BeNull();
        profile.NutritionistRoleRemovedAt.Should().BeNull();
        (await ReadCancelAtPeriodEndAsync(coach.ProfileId)).Should().BeTrue();
    }

    [Fact]
    public async Task Disable_TrialingSubscription_UsesTrialEnd()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var trialEnd = DateTimeOffset.UtcNow.AddDays(5);
        await SeedSubscriptionAsync(
            factory, coach.ProfileId, SubscriptionStatus.Trialing, DateTimeOffset.UtcNow.AddDays(40), trialEnd);

        var response = await coach.Http.PostAsync("/users/me/coach-account/disable", null, Ct);

        var body = await response.Content.ReadFromJsonAsync<DisableResult>(cancellationToken: Ct);
        body!.ActiveUntil.Should().BeCloseTo(trialEnd.UtcDateTime, TimeSpan.FromSeconds(1));
        body.RolesRemoved.Should().BeEmpty();
    }

    [Fact]
    public async Task Disable_NoSubscription_RemovesBothRolesAtOnce_AndNotifiesEachClientOnce()
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).CreateAsync(Ct);

        var response = await coach.Http.PostAsync("/users/me/coach-account/disable", null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<DisableResult>(cancellationToken: Ct);
        body!.RolesRemoved.Should().BeEquivalentTo([AppRoles.Trainer, AppRoles.Nutritionist]);
        body.ActiveUntil.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));

        var profile = await ReadProfileAsync(coach.UserId);
        profile.TrainerRoleRemovedAt.Should().NotBeNull();
        profile.NutritionistRoleRemovedAt.Should().NotBeNull();
        (await CountRoleRemovedNotificationsAsync(client.UserId)).Should().Be(1);
    }

    [Fact]
    public async Task Disable_ActiveRowWithoutPeriodEnd_RemovesRolesAtOnce()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await SeedSubscriptionAsync(factory, coach.ProfileId, SubscriptionStatus.Active, periodEndsAt: null);

        var response = await coach.Http.PostAsync("/users/me/coach-account/disable", null, Ct);

        var body = await response.Content.ReadFromJsonAsync<DisableResult>(cancellationToken: Ct);
        body!.RolesRemoved.Should().Equal(AppRoles.Trainer);
        (await ReadProfileAsync(coach.UserId)).TrainerRoleRemovedAt.Should().NotBeNull();
        (await ReadCancelAtPeriodEndAsync(coach.ProfileId)).Should().BeTrue();
    }

    [Fact]
    public async Task Disable_RepeatedWhilePending_ReturnsSameDate()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await SeedSubscriptionAsync(factory, coach.ProfileId, SubscriptionStatus.Active, DateTimeOffset.UtcNow.AddDays(10));

        var first = await (await coach.Http.PostAsync("/users/me/coach-account/disable", null, Ct))
            .Content.ReadFromJsonAsync<DisableResult>(cancellationToken: Ct);
        var secondResponse = await coach.Http.PostAsync("/users/me/coach-account/disable", null, Ct);
        var second = await secondResponse.Content.ReadFromJsonAsync<DisableResult>(cancellationToken: Ct);

        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        second!.ActiveUntil.Should().Be(first!.ActiveUntil);
        second.RolesRemoved.Should().BeEmpty();
    }

    [Fact]
    public async Task Disable_AllRolesAlreadyRemoved_Returns400NoActiveCoachRole()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        (await coach.Http.PostAsync("/users/me/coach-account/disable", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await coach.Http.PostAsync("/users/me/coach-account/disable", null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.NoActiveCoachRole);
    }

    [Fact]
    public async Task Disable_ClientOnlyUser_Returns403()
    {
        var client = await TestActors.Client(factory).CreateAsync(Ct);

        var response = await client.Http.PostAsync("/users/me/coach-account/disable", null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Disable_OnlyCoachRoleDelete_StillReturnsOnlyCoachRole()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await SeedSubscriptionAsync(factory, coach.ProfileId, SubscriptionStatus.Active, DateTimeOffset.UtcNow.AddDays(10));
        (await coach.Http.PostAsync("/users/me/coach-account/disable", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await coach.Http.DeleteAsync("/users/me/roles/Trainer", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.OnlyCoachRole);
    }

    [Fact]
    public async Task GetRoles_ReturnsStoredAndComputedDates()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var periodEnd = DateTimeOffset.UtcNow.AddDays(12);
        await SeedSubscriptionAsync(factory, coach.ProfileId, SubscriptionStatus.Active, periodEnd);

        var before = await coach.Http.GetFromJsonAsync<RolesResult>("/users/me/roles", Ct);
        before!.CoachAccountActiveUntil.Should().BeNull();
        before.ActiveUntilIfDisabled.Should().BeCloseTo(periodEnd.UtcDateTime, TimeSpan.FromSeconds(1));

        await coach.Http.PostAsync("/users/me/coach-account/disable", null, Ct);

        var after = await coach.Http.GetFromJsonAsync<RolesResult>("/users/me/roles", Ct);
        after!.CoachAccountActiveUntil.Should().BeCloseTo(periodEnd.UtcDateTime, TimeSpan.FromSeconds(1));
        after.ActiveUntilIfDisabled.Should().Be(after.CoachAccountActiveUntil);
    }

    [Fact]
    public async Task Disable_GiftedCoachWithFreeUntil_UsesFreeUntil_AndRolesShowTheSameDateBeforehand()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var freeUntil = DateTimeOffset.UtcNow.AddDays(45);
        await SeedSubscriptionAsync(factory, coach.ProfileId, SubscriptionStatus.Active, periodEndsAt: null);
        await BillingTestData.UpdateRowAsync(factory, coach.ProfileId, row =>
        {
            row.GivenFreeAt = DateTimeOffset.UtcNow;
            row.FreeUntil = freeUntil;
        });

        var before = await coach.Http.GetFromJsonAsync<RolesResult>("/users/me/roles", Ct);
        var response = await coach.Http.PostAsync("/users/me/coach-account/disable", null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<DisableResult>(cancellationToken: Ct);
        body!.ActiveUntil.Should().BeCloseTo(freeUntil.UtcDateTime, TimeSpan.FromSeconds(1));
        before!.ActiveUntilIfDisabled.Should().BeCloseTo(freeUntil.UtcDateTime, TimeSpan.FromSeconds(1));
        body.ActiveUntil.Should().Be(before.ActiveUntilIfDisabled!.Value);
        (await ReadProfileAsync(coach.UserId)).CoachAccountActiveUntil
            .Should().BeCloseTo(freeUntil.UtcDateTime, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Disable_LiveStripeSubscription_CancelsAtTheProviderOnce_AndClearsTheScheduledChange()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory);
        var subscriptionId = await BillingTestData.SubscribeLiveAsync(factory, coach, plan, paysSecondRoleAddOn: true);
        await BillingTestData.SetPendingPlanAsync(factory, coach.ProfileId, plan);
        await BillingTestData.UpdateRowAsync(factory, coach.ProfileId, row => row.SecondRoleAddOnEndsAt = DateTimeOffset.UtcNow.AddDays(3));

        var response = await coach.Http.PostAsync("/users/me/coach-account/disable", null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.CancelledAtPeriodEnd.Should().ContainSingle().Which.Should().Be(subscriptionId);
        var row = await BillingTestData.ReadRowAsync(factory, coach.ProfileId);
        row.CancelAtPeriodEnd.Should().BeTrue();
        row.PendingSubscriptionPlanPriceId.Should().BeNull();
        row.SecondRoleAddOnEndsAt.Should().BeNull();
    }

    [Fact]
    public async Task Disable_LiveStripeSubscription_WhenTheProviderFails_Returns502AndSavesNothing()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory);
        await BillingTestData.SubscribeLiveAsync(factory, coach, plan);
        fake.Fail = true;

        var response = await coach.Http.PostAsync("/users/me/coach-account/disable", null, Ct);

        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.BillingProviderError);
        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await ReadProfileAsync(coach.UserId)).CoachAccountActiveUntil.Should().BeNull();
        (await ReadCancelAtPeriodEndAsync(coach.ProfileId)).Should().BeFalse();
    }

    [Fact]
    public async Task Disable_LiveStripeSubscription_WhenBillingIsNotConfigured_Returns503AndSavesNothing()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory);
        await BillingTestData.SubscribeLiveAsync(factory, coach, plan);
        fake.IsConfigured = false;

        var response = await coach.Http.PostAsync("/users/me/coach-account/disable", null, Ct);

        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.BillingNotConfigured);
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        fake.CancelledAtPeriodEnd.Should().BeEmpty();
        (await ReadCancelAtPeriodEndAsync(coach.ProfileId)).Should().BeFalse();
    }

    [Fact]
    public async Task Disable_SubscriptionWithoutALiveProviderSubscription_SkipsTheProvider_EvenWhenNotConfigured()
    {
        var fake = BillingTestData.ResetProvider(factory);
        fake.IsConfigured = false;
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await SeedSubscriptionAsync(factory, coach.ProfileId, SubscriptionStatus.Active, DateTimeOffset.UtcNow.AddDays(5));

        var response = await coach.Http.PostAsync("/users/me/coach-account/disable", null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.CancelledAtPeriodEnd.Should().BeEmpty();
        (await ReadCancelAtPeriodEndAsync(coach.ProfileId)).Should().BeTrue();
    }

    [Fact]
    public async Task Disable_WhenTheRowAlreadyCancelsAtPeriodEnd_DoesNotCallTheProvider()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory);
        await BillingTestData.SubscribeLiveAsync(factory, coach, plan, cancelAtPeriodEnd: true);

        var response = await coach.Http.PostAsync("/users/me/coach-account/disable", null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.CancelledAtPeriodEnd.Should().BeEmpty();
    }

    [Fact]
    public async Task GetProfile_ReturnsPendingCoachAccountDate()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var periodEnd = DateTimeOffset.UtcNow.AddDays(9);
        await SeedSubscriptionAsync(factory, coach.ProfileId, SubscriptionStatus.Active, periodEnd);

        (await coach.Http.GetFromJsonAsync<ProfileResult>("/users/me", Ct))!
            .CoachAccountActiveUntil.Should().BeNull();

        await coach.Http.PostAsync("/users/me/coach-account/disable", null, Ct);

        (await coach.Http.GetFromJsonAsync<ProfileResult>("/users/me", Ct))!
            .CoachAccountActiveUntil.Should().BeCloseTo(periodEnd.UtcDateTime, TimeSpan.FromSeconds(1));
    }
}
