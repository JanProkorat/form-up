using System.Net;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.SubscriptionPlans;

/// <summary>
/// Integration tests for <c>DELETE /admin/subscription-plans/{Code}</c>
/// (<see cref="Application.Features.SubscriptionPlans.DeactivateSubscriptionPlan.DeactivateSubscriptionPlanEndpoint"/>).
/// Uses <see cref="FitnessApiFactory"/> since Admin is not publicly self-registerable — the
/// only way to prove the role gate is the real ASP.NET Core pipeline.
/// </summary>
[Collection(TestCollection.Name)]
public class DeactivateSubscriptionPlanEndpointTests(FitnessApiFactory factory)
{
    private static string UniqueCode() => $"tier-{Guid.NewGuid():N}"[..30];

    private async Task<string> SeedPlanAsync(bool isActive)
    {
        var code = UniqueCode();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var plan = new SubscriptionPlan
        {
            Code = code,
            NameCs = "Test",
            NameEn = "Test",
            NameDe = "Test",
            BothRolesIncluded = true,
            Currency = "CZK",
            PriceMinorUnits = 0,
            BillingInterval = BillingInterval.Monthly,
            IsActive = isActive,
        };
        SubscriptionPlanPriceBuilder.OpenPriceFor(plan);
        db.SubscriptionPlans.Add(plan);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return code;
    }

    [Fact]
    public async Task Deactivate_ActivePlan_SetsIsActiveFalseWithoutDeletingRow()
    {
        var client = await TestHelpers.RegisterAdminAsync(factory, TestContext.Current.CancellationToken);
        var code = await SeedPlanAsync(isActive: true);

        var response = await client.DeleteAsync(
            $"/admin/subscription-plans/{code}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var persisted = await db.SubscriptionPlans
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Code == code, TestContext.Current.CancellationToken);

        persisted.Should().NotBeNull();
        persisted!.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Deactivate_AlreadyInactivePlan_IsIdempotentAndReturns204()
    {
        var client = await TestHelpers.RegisterAdminAsync(factory, TestContext.Current.CancellationToken);
        var code = await SeedPlanAsync(isActive: false);

        var response = await client.DeleteAsync(
            $"/admin/subscription-plans/{code}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var persisted = await db.SubscriptionPlans
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Code == code, TestContext.Current.CancellationToken);

        persisted.Should().NotBeNull();
        persisted!.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Deactivate_FreePlan_Returns409FreePlanLockedAndStaysActive()
    {
        var client = await TestHelpers.RegisterAdminAsync(factory, TestContext.Current.CancellationToken);
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);

        var response = await client.DeleteAsync(
            "/admin/subscription-plans/free", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain(ErrorCodes.FreePlanLocked);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var free = await db.SubscriptionPlans
            .AsNoTracking()
            .FirstAsync(p => p.Code == "free", TestContext.Current.CancellationToken);
        free.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task Deactivate_UnknownCode_Returns404()
    {
        var client = await TestHelpers.RegisterAdminAsync(factory, TestContext.Current.CancellationToken);

        var response = await client.DeleteAsync(
            $"/admin/subscription-plans/{UniqueCode()}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
