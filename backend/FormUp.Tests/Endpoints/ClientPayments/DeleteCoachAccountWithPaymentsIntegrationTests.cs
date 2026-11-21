using System.Net;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static FormUp.Tests.Endpoints.ClientPayments.ClientPaymentFixtures;

namespace FormUp.Tests.Endpoints.ClientPayments;

/// <summary>
/// Deleting a coach's account while a client still uses one of their packages and discounts.
/// </summary>
[Collection(TestCollection.Shard4)]
public class DeleteCoachAccountWithPaymentsIntegrationTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DeleteAccount_CoachWithAssignedPackageAndDiscount_Succeeds_AndLeavesNoPaymentRows()
    {
        var world = await CreateWorldAsync(factory);
        var packageId = await CreatePackageAsync(world.Coach);
        var discountId = await CreateDiscountAsync(world.Coach);
        await SetTermsOkAsync(world, packageId, discountId);
        (await ListAsync(world)).Should().NotBeEmpty("the periods exist before the coach leaves");

        var response = await world.Coach.Http.DeleteAsync("/users/me", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var coachProfileId = world.Coach.ProfileId;

        (await db.CoachPackages.CountAsync(p => p.ProfessionalProfileId == coachProfileId, Ct)).Should().Be(0);
        (await db.CoachDiscounts.CountAsync(d => d.ProfessionalProfileId == coachProfileId, Ct)).Should().Be(0);
        (await db.ClientPaymentAssignments.CountAsync(a => a.ProfessionalProfileId == coachProfileId, Ct)).Should().Be(0);
        (await db.ClientPayments.CountAsync(p => p.ProfessionalProfileId == coachProfileId, Ct)).Should().Be(0);
        (await db.ClientPaymentTerms.CountAsync(t => t.ClientPaymentAssignment.ProfessionalProfileId == coachProfileId, Ct))
            .Should().Be(0);
    }
}
