using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace FormUp.Tests.Endpoints.NutritionPlans;

/// <summary>
/// Testcontainers integration tests (real MongoDB) for <c>DELETE /nutrition/plans/{PlanId}</c>.
/// The mock Mongo ignores filters, so the hard delete and the started-week rule are proven here.
/// </summary>
[Collection(TestCollection.Shard4)]
public class DeletePlanIntegrationTests(FitnessApiFactory factory)
{
    // Far ahead of UTC so the client's local date differs from the UTC date for part of every day.
    private const string ClientTimeZoneId = "Pacific/Kiritimati";

    private async Task<(Actor Nutritionist, Actor Client)> CreateLinkedPairAsync(bool canViewNutritionPlans = true)
    {
        var ct = TestContext.Current.CancellationToken;
        var nutritionist = await TestActors.Nutritionist(factory).CreateAsync(ct);
        var client = await TestActors.Client(factory).CreateAsync(ct);
        await TestActors.Link(factory, nutritionist, client)
            .AsRole(UserRole.Nutritionist)
            .CanViewNutritionPlans(canViewNutritionPlans)
            .CreateAsync(ct);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.FirstAsync(u => u.Id == client.UserId, ct);
        user.TimeZone = ClientTimeZoneId;
        await db.SaveChangesAsync(ct);

        return (nutritionist, client);
    }

    private static DateOnly ClientToday() =>
        ClientLocalDateResolver.ResolveLocalDate(
            DateTime.UtcNow, ClientLocalDateResolver.ResolveTimeZone(ClientTimeZoneId));

    private async Task<NutritionPlan> SeedPlanAsync(
        Actor nutritionist, Actor client, DateOnly? startDate, WeekStatus weekStatus)
    {
        var plan = new NutritionPlan
        {
            ExternalId = Guid.NewGuid(),
            ClientId = client.UserId,
            NutritionistId = nutritionist.UserId,
            Name = "Delete Plan Test",
            Status = weekStatus == WeekStatus.Published ? NutritionPlanStatus.Active : NutritionPlanStatus.Draft,
            StartDate = startDate?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            Version = 1,
            DateCreated = DateTime.UtcNow,
            Weeks = [new PlanWeek { WeekNumber = 1, Status = weekStatus }],
        };

        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        await mongo.NutritionPlans.InsertOneAsync(plan, cancellationToken: TestContext.Current.CancellationToken);
        return plan;
    }

    private async Task<bool> PlanExistsAsync(Guid externalId)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        return await mongo.NutritionPlans
            .Find(p => p.ExternalId == externalId)
            .AnyAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Delete_DraftPlan_Returns204_AndRemovesDocument()
    {
        var (nutritionist, client) = await CreateLinkedPairAsync();
        var plan = await SeedPlanAsync(nutritionist, client, ClientToday().AddDays(-14), WeekStatus.Draft);

        var response = await nutritionist.Http.DeleteAsync(
            $"/nutrition/plans/{plan.ExternalId}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await PlanExistsAsync(plan.ExternalId)).Should().BeFalse("the delete is permanent, not an archive");
    }

    [Fact]
    public async Task Delete_PublishedWeekStartsToday_Returns409_AndKeepsDocument()
    {
        var (nutritionist, client) = await CreateLinkedPairAsync();
        var plan = await SeedPlanAsync(nutritionist, client, ClientToday(), WeekStatus.Published);

        var response = await nutritionist.Http.DeleteAsync(
            $"/nutrition/plans/{plan.ExternalId}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain(ErrorCodes.PlanAlreadyStarted);
        (await PlanExistsAsync(plan.ExternalId)).Should().BeTrue();
    }

    [Fact]
    public async Task Delete_PublishedWeekStartsTomorrow_Returns204_AndRemovesDocument()
    {
        var (nutritionist, client) = await CreateLinkedPairAsync();
        var plan = await SeedPlanAsync(nutritionist, client, ClientToday().AddDays(1), WeekStatus.Published);

        var response = await nutritionist.Http.DeleteAsync(
            $"/nutrition/plans/{plan.ExternalId}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await PlanExistsAsync(plan.ExternalId)).Should().BeFalse();
    }

    [Fact]
    public async Task Delete_LinkWithoutNutritionAccess_Returns404_AndKeepsDocument()
    {
        var (nutritionist, client) = await CreateLinkedPairAsync(canViewNutritionPlans: false);
        var plan = await SeedPlanAsync(nutritionist, client, ClientToday().AddDays(7), WeekStatus.Draft);

        var response = await nutritionist.Http.DeleteAsync(
            $"/nutrition/plans/{plan.ExternalId}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await PlanExistsAsync(plan.ExternalId)).Should().BeTrue();
    }
}
