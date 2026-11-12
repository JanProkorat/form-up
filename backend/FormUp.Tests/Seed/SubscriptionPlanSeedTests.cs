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
                "the seed writes one price row per plan and a second boot must not add another").Subject;
            price.ValidTo.Should().BeNull();
            price.PriceMinorUnits.Should().Be(plan.PriceMinorUnits);
            price.SecondRoleAddOnPriceMinorUnits.Should().Be(plan.SecondRoleAddOnPriceMinorUnits);
        }

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

        plans["studio"].PriceMinorUnits.Should().Be(99900);
        plans["studio"].BothRolesIncluded.Should().BeTrue();
        plans["studio"].MaxActiveClients.Should().BeNull();
        plans["studio"].CanUsePerClientCheckInConfig.Should().BeTrue();
    }
}
