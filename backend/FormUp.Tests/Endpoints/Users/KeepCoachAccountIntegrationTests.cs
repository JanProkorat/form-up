using System.Net;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Users;

/// <summary>
/// Integration tests for <c>POST /users/me/coach-account/keep</c>.
/// </summary>
[Collection(TestCollection.Name)]
public class KeepCoachAccountIntegrationTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Keep_BeforeThePeriodEnd_ClearsTheDisable()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await DisableCoachAccountIntegrationTests.SeedSubscriptionAsync(
            factory, coach.ProfileId, SubscriptionStatus.Active, DateTimeOffset.UtcNow.AddDays(10));
        (await coach.Http.PostAsync("/users/me/coach-account/disable", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await coach.Http.PostAsync("/users/me/coach-account/keep", null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var profile = await db.ProfessionalProfiles.AsNoTracking().FirstAsync(p => p.UserId == coach.UserId, Ct);
        profile.CoachAccountActiveUntil.Should().BeNull();
        profile.TrainerRoleRemovedAt.Should().BeNull();
        (await db.CoachSubscriptions.AsNoTracking()
            .Where(cs => cs.ProfessionalProfileId == coach.ProfileId)
            .Select(cs => cs.CancelAtPeriodEnd)
            .FirstAsync(Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task Keep_WhenAnAdminCancelledTheRenewal_ClearsTheDisable_ButKeepsTheCancellation()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await DisableCoachAccountIntegrationTests.SeedSubscriptionAsync(
            factory, coach.ProfileId, SubscriptionStatus.Active, DateTimeOffset.UtcNow.AddDays(10));
        (await coach.Http.PostAsync("/users/me/coach-account/disable", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.CoachSubscriptions
            .Where(cs => cs.ProfessionalProfileId == coach.ProfileId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(cs => cs.RenewalCancelledByAdminAt, DateTimeOffset.UtcNow), Ct);

        var response = await coach.Http.PostAsync("/users/me/coach-account/keep", null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await db.ProfessionalProfiles.AsNoTracking().Where(p => p.UserId == coach.UserId)
            .Select(p => p.CoachAccountActiveUntil).FirstAsync(Ct)).Should().BeNull();
        var row = await db.CoachSubscriptions.AsNoTracking().FirstAsync(cs => cs.ProfessionalProfileId == coach.ProfileId, Ct);
        row.CancelAtPeriodEnd.Should().BeTrue("the admin's cancellation is not the coach's to undo");
        row.RenewalCancelledByAdminAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Keep_NothingPending_Returns400NotDisabling()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);

        var response = await coach.Http.PostAsync("/users/me/coach-account/keep", null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.CoachAccountNotDisabling);
    }

    [Fact]
    public async Task Keep_AfterTheDisableEnded_Returns400DisableEnded()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        (await coach.Http.PostAsync("/users/me/coach-account/disable", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await coach.Http.PostAsync("/users/me/coach-account/keep", null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.CoachAccountDisableEnded);
    }
}
