using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Billing;

/// <summary>
/// Small seeding helpers shared by the billing endpoint tests.
/// </summary>
internal static class BillingTestData
{
    /// <summary>Reads responses the way the API writes them: camelCase names and string enums.</summary>
    public static readonly System.Text.Json.JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>The fake payment provider the test host uses, reset to its defaults.</summary>
    /// <param name="factory">The test host.</param>
    public static FakeSubscriptionService ResetProvider(FitnessApiFactory factory)
    {
        var fake = factory.Services.GetRequiredService<FakeSubscriptionService>();
        fake.Reset();

        return fake;
    }

    /// <summary>Gives the plan's open price row a Stripe price id and returns it.</summary>
    /// <param name="factory">The test host.</param>
    /// <param name="planCode">Code of the plan.</param>
    public static async Task<string> SetStripePriceAsync(FitnessApiFactory factory, string planCode)
    {
        var priceId = $"price_{Guid.NewGuid():N}";
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        await db.SubscriptionPlanPrices
            .Where(p => p.SubscriptionPlan.Code == planCode && p.ValidTo == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.ExternalPriceId, priceId), TestContext.Current.CancellationToken);

        return priceId;
    }

    /// <summary>A paid plan priced in Stripe; returns its code and Stripe price id.</summary>
    /// <param name="factory">The test host.</param>
    /// <param name="price">Plan price.</param>
    /// <param name="addOn">Second-role add-on price, or null.</param>
    /// <param name="bothRolesIncluded">True when the plan covers both roles at no extra charge.</param>
    public static async Task<(string Code, string PriceId)> StripePlanAsync(
        FitnessApiFactory factory, long price = 29900, long? addOn = null, bool bothRolesIncluded = true)
    {
        var code = await PaidPlanBuilder.CreateAsync(
            factory, priceMinorUnits: price, addOnMinorUnits: addOn, bothRolesIncluded: bothRolesIncluded);

        return (code, await SetStripePriceAsync(factory, code));
    }

    /// <summary>
    /// Subscribes the coach to the plan as a live Stripe subscription (provider ids set) and returns the provider
    /// subscription id.
    /// </summary>
    /// <param name="factory">The test host.</param>
    /// <param name="coach">The coach.</param>
    /// <param name="planCode">Code of the plan the coach is on.</param>
    /// <param name="paysSecondRoleAddOn">True when the coach already pays the add-on.</param>
    /// <param name="givenFreeAt">When an admin gave the plan for free, or null.</param>
    /// <param name="freeUntil">End of the gift, or null.</param>
    /// <param name="cancelAtPeriodEnd">True when a cancellation is already pending.</param>
    /// <param name="currentPeriodEndsAt">When the current paid period ends.</param>
    public static async Task<string> SubscribeLiveAsync(
        FitnessApiFactory factory,
        Actor coach,
        string planCode,
        bool paysSecondRoleAddOn = false,
        DateTimeOffset? givenFreeAt = null,
        DateTimeOffset? freeUntil = null,
        bool cancelAtPeriodEnd = false,
        DateTimeOffset? currentPeriodEndsAt = null)
    {
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(
            factory,
            coach,
            planCode,
            paysSecondRoleAddOn: paysSecondRoleAddOn,
            givenFreeAt: givenFreeAt,
            freeUntil: freeUntil,
            cancelAtPeriodEnd: cancelAtPeriodEnd,
            currentPeriodEndsAt: currentPeriodEndsAt ?? DateTimeOffset.UtcNow.AddDays(10));

        var subscriptionId = $"sub_{Guid.NewGuid():N}";
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        await db.CoachSubscriptions
            .Where(cs => cs.ProfessionalProfileId == coach.ProfileId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(cs => cs.ExternalSubscriptionId, subscriptionId)
                    .SetProperty(cs => cs.ExternalCustomerId, $"cus_{Guid.NewGuid():N}"),
                TestContext.Current.CancellationToken);

        return subscriptionId;
    }

    /// <summary>Marks the plan's open price row as the coach's scheduled change for the next renewal.</summary>
    /// <param name="factory">The test host.</param>
    /// <param name="profileId">The coach's profile id.</param>
    /// <param name="planCode">Code of the plan the coach moves to at renewal.</param>
    public static async Task SetPendingPlanAsync(FitnessApiFactory factory, long profileId, string planCode)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var priceId = await db.SubscriptionPlanPrices
            .Where(p => p.SubscriptionPlan.Code == planCode && p.ValidTo == null)
            .Select(p => p.Id)
            .SingleAsync(TestContext.Current.CancellationToken);

        await db.CoachSubscriptions
            .Where(cs => cs.ProfessionalProfileId == profileId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(cs => cs.PendingSubscriptionPlanPriceId, priceId), TestContext.Current.CancellationToken);
    }

    /// <summary>Marks a plan inactive.</summary>
    /// <param name="factory">The test host.</param>
    /// <param name="planCode">Code of the plan.</param>
    public static async Task DeactivatePlanAsync(FitnessApiFactory factory, string planCode)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        await db.SubscriptionPlans
            .Where(p => p.Code == planCode)
            .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.IsActive, false), TestContext.Current.CancellationToken);
    }

    /// <summary>Reads the coach's subscription row with its plan and price rows.</summary>
    /// <param name="factory">The test host.</param>
    /// <param name="profileId">The coach's profile id.</param>
    public static async Task<CoachSubscription> ReadRowAsync(FitnessApiFactory factory, long profileId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.CoachSubscriptions.AsNoTracking()
            .Include(cs => cs.SubscriptionPlan)
            .Include(cs => cs.SubscriptionPlanPrice)
            .SingleAsync(cs => cs.ProfessionalProfileId == profileId, TestContext.Current.CancellationToken);
    }

    /// <summary>Stamps the row's last applied provider event, so a test can prove a write left it alone.</summary>
    /// <param name="factory">The test host.</param>
    /// <param name="profileId">The coach's profile id.</param>
    /// <param name="at">The instant to store.</param>
    public static async Task SetLastExternalEventAsync(FitnessApiFactory factory, long profileId, DateTimeOffset at)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        await db.CoachSubscriptions
            .Where(cs => cs.ProfessionalProfileId == profileId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(cs => cs.LastExternalEventAt, at), TestContext.Current.CancellationToken);
    }

    /// <summary>A coach with a trainer role that was removed, so no coach role is active.</summary>
    /// <param name="factory">The test host.</param>
    public static Task<Actor> CoachWithAllRolesRemovedAsync(FitnessApiFactory factory) =>
        TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist)
            .WithRemovedCoachRoles(UserRole.Trainer, UserRole.Nutritionist)
            .CreateAsync(TestContext.Current.CancellationToken);
}
