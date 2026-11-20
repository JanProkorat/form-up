using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Seed;

/// <summary>
/// Tests for the subscription plan part of <see cref="ApplicationDbContextSeed.SeedAsync"/>.
/// </summary>
[Collection(TestCollection.Name)]
public class SubscriptionPlanSeedTests(FitnessApiFactory factory)
{
    [Fact]
    public async Task SeedAsync_SeedsFreeStartProStudioEachWithOneOpenPriceRow()
    {
        await ApplicationDbContextSeed.SeedAsync(factory.Services);
        await ApplicationDbContextSeed.SeedAsync(factory.Services);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var plans = await db.SubscriptionPlans
            .AsNoTracking()
            .Include(p => p.Prices)
            .Where(p => new[] { "free", "start", "pro", "studio", "small", "medium", "big" }.Contains(p.Code))
            .ToDictionaryAsync(p => p.Code, TestContext.Current.CancellationToken);

        plans.Keys.Should().BeEquivalentTo("free", "start", "pro", "studio");

        foreach (var plan in plans.Values)
        {
            var price = plan.Prices.Should().ContainSingle(
                p => p.BillingInterval == BillingInterval.Monthly,
                "the seed writes one monthly price row per plan and a second boot must not add another").Subject;
            price.ValidTo.Should().BeNull();
            price.PriceMinorUnits.Should().Be(plan.PriceMinorUnits);
            price.SecondRoleAddOnPriceMinorUnits.Should().Be(plan.SecondRoleAddOnPriceMinorUnits);
        }

        plans["free"].Prices.Should().NotContain(p => p.BillingInterval == BillingInterval.Annual);
        AnnualRow(plans["start"]).Should().BeEquivalentTo(new { PriceMinorUnits = 299000L, SecondRoleAddOnPriceMinorUnits = (long?)149000L });
        AnnualRow(plans["pro"]).Should().BeEquivalentTo(new { PriceMinorUnits = 599000L, SecondRoleAddOnPriceMinorUnits = (long?)299000L });
        AnnualRow(plans["studio"]).Should().BeEquivalentTo(new { PriceMinorUnits = 999000L, SecondRoleAddOnPriceMinorUnits = (long?)null });

        plans["free"].MaxActiveClients.Should().Be(2);
        plans["free"].PriceMinorUnits.Should().Be(0);
        plans["free"].BothRolesIncluded.Should().BeTrue();

        plans["start"].PriceMinorUnits.Should().Be(29900);
        plans["start"].SecondRoleAddOnPriceMinorUnits.Should().Be(14900);
        plans["start"].MaxActiveClients.Should().Be(10);
        plans["start"].BothRolesIncluded.Should().BeFalse();

        plans["pro"].PriceMinorUnits.Should().Be(59900);
        plans["pro"].SecondRoleAddOnPriceMinorUnits.Should().Be(29900);
        plans["pro"].MaxActiveClients.Should().Be(30);
        plans["pro"].CanSendQuestionnaires.Should().BeTrue();
        plans["pro"].CanUseWeeklyCheckIns.Should().BeTrue();

        // Client payments (packages) come with Pro.
        plans["free"].CanUsePayments.Should().BeFalse();
        plans["start"].CanUsePayments.Should().BeFalse();
        plans["pro"].CanUsePayments.Should().BeTrue();
        plans["studio"].CanUsePayments.Should().BeTrue();

        // Packages follow client payments; every other new feature is on from Free up.
        plans["free"].CanUsePackages.Should().BeFalse();
        plans["start"].CanUsePackages.Should().BeFalse();
        plans["pro"].CanUsePackages.Should().BeTrue();
        plans["studio"].CanUsePackages.Should().BeTrue();

        foreach (var plan in plans.Values)
        {
            plan.CanUsePlanTemplates.Should().BeTrue();
            plan.CanUseRecipes.Should().BeTrue();
            plan.CanUseIngredients.Should().BeTrue();
            plan.CanUseProgressPhotos.Should().BeTrue();
            plan.CanUseWeightAndMeasurements.Should().BeTrue();
            plan.CanUseAutomations.Should().BeTrue();
        }

        plans["studio"].PriceMinorUnits.Should().Be(99900);
        plans["studio"].BothRolesIncluded.Should().BeTrue();
        plans["studio"].MaxActiveClients.Should().BeNull();
        plans["studio"].CanUsePerClientCheckInConfig.Should().BeTrue();
    }

    [Fact]
    public async Task SeedAsync_PlanWithoutAnAnnualRow_GetsOne_AndAClosedAnnualRowIsLeftAlone()
    {
        await ApplicationDbContextSeed.SeedAsync(factory.Services);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var annualRows = await db.SubscriptionPlanPrices
                .Where(p => p.BillingInterval == BillingInterval.Annual && (p.SubscriptionPlan.Code == "start" || p.SubscriptionPlan.Code == "pro"))
                .Include(p => p.SubscriptionPlan)
                .ToListAsync(TestContext.Current.CancellationToken);

            // Start looks like a database seeded before annual billing; Pro had its annual price closed by an admin.
            db.SubscriptionPlanPrices.RemoveRange(annualRows.Where(p => p.SubscriptionPlan.Code == "start"));
            annualRows.Single(p => p.SubscriptionPlan.Code == "pro").ValidTo = DateTime.UtcNow;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await ApplicationDbContextSeed.SeedAsync(factory.Services);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var plans = await verifyDb.SubscriptionPlans
            .AsNoTracking()
            .Include(p => p.Prices)
            .Where(p => p.Code == "start" || p.Code == "pro")
            .ToDictionaryAsync(p => p.Code, TestContext.Current.CancellationToken);

        AnnualRow(plans["start"]).PriceMinorUnits.Should().Be(299000);
        plans["start"].Prices.Count(p => p.BillingInterval == BillingInterval.Annual).Should().Be(1);
        plans["pro"].Prices.Should().ContainSingle(p => p.BillingInterval == BillingInterval.Annual)
            .Which.ValidTo.Should().NotBeNull("an admin who closed the annual price is not overridden by a later boot");

        // Restore the shared database for the tests that follow.
        await using var restoreScope = factory.Services.CreateAsyncScope();
        var restoreDb = restoreScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await restoreDb.SubscriptionPlanPrices
            .Where(p => p.BillingInterval == BillingInterval.Annual && p.SubscriptionPlan.Code == "pro")
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.ValidTo, (DateTime?)null), TestContext.Current.CancellationToken);
    }

    private static SubscriptionPlanPrice AnnualRow(SubscriptionPlan plan) =>
        plan.Prices.Should().ContainSingle(p => p.BillingInterval == BillingInterval.Annual && p.ValidTo == null).Subject;
}
