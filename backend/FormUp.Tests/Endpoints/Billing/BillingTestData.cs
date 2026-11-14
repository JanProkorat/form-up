using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Billing;

/// <summary>
/// Small seeding helpers shared by the billing endpoint tests.
/// </summary>
internal static class BillingTestData
{
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
}
