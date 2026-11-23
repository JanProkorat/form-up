using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Endpoints.TrainingPlans;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace FormUp.Tests.Endpoints.CoachRoleRemoval;

/// <summary>
/// Real-pipeline tests for the read-only gate after a coach role is removed: real database, real
/// Mongo (mock Mongo ignores filters), role removed through the real <c>DELETE /users/me/roles/{Role}</c>.
/// </summary>
[Collection(TestCollection.Shard3)]
public class RemovedCoachRoleGateIntegrationTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Actor> DualCoachWithRemovedAsync(string removedRole)
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        (await coach.Http.DeleteAsync($"/users/me/roles/{removedRole}", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        return coach;
    }

    private static async Task<HttpResponseMessage> SendAsync(Actor actor, string method, string path, object body)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(body) };
        return await actor.Http.SendAsync(request, Ct);
    }

    private static async Task AssertCoachRoleRemovedAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, raw);
        using var document = JsonDocument.Parse(raw);
        document.RootElement.GetProperty("errors")[0].GetProperty("code").GetString()
            .Should().Be(ErrorCodes.CoachRoleRemoved);
    }

    [Theory]
    [InlineData("Trainer", "POST", "/training/plans")]
    [InlineData("Trainer", "POST", "/training/plan-templates")]
    [InlineData("Trainer", "POST", "/training/session-templates")]
    [InlineData("Trainer", "POST", "/training/workout-templates")]
    [InlineData("Trainer", "POST", "/exercises")]
    [InlineData("Trainer", "POST", "/trainer/clients/5b1c4c2e-7d11-4f0c-8f56-2b6a3a1a0c01/notes")]
    [InlineData("Nutritionist", "POST", "/nutrition/plans")]
    [InlineData("Nutritionist", "POST", "/nutrition/plan-templates")]
    [InlineData("Nutritionist", "POST", "/nutrition/meal-templates")]
    [InlineData("Nutritionist", "POST", "/recipes")]
    [InlineData("Nutritionist", "POST", "/foods")]
    public async Task RemovedRole_WriteOfItsFamily_Returns403CoachRoleRemoved(string removedRole, string method, string path)
    {
        var coach = await DualCoachWithRemovedAsync(removedRole);

        var response = await SendAsync(coach, method, path, new { });

        await AssertCoachRoleRemovedAsync(response);
    }

    [Theory]
    [InlineData("Trainer", "/training/session-templates")]
    [InlineData("Nutritionist", "/nutrition/meal-templates")]
    public async Task RemovedRole_ReadOfItsFamily_StillReturns200(string removedRole, string path)
    {
        var coach = await DualCoachWithRemovedAsync(removedRole);

        var response = await coach.Http.GetAsync(path, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RemovedRole_StatelessCalculator_StaysOpen()
    {
        var coach = await DualCoachWithRemovedAsync("Nutritionist");

        var response = await SendAsync(coach, "POST", $"/nutrition/clients/{Guid.NewGuid()}/calculate-goals", new { });

        (await response.Content.ReadAsStringAsync(Ct)).Should().NotContain(ErrorCodes.CoachRoleRemoved);
    }

    [Fact]
    public async Task RemovedRole_OtherDisciplineWrite_IsUnaffected()
    {
        var coach = await DualCoachWithRemovedAsync("Trainer");

        var response = await SendAsync(coach, "POST", "/recipes", new { });

        (await response.Content.ReadAsStringAsync(Ct)).Should().NotContain(ErrorCodes.CoachRoleRemoved);
    }

    [Theory]
    [InlineData(UserRole.Nutritionist, "/recipes")]
    [InlineData(UserRole.Nutritionist, "/foods")]
    [InlineData(UserRole.Trainer, "/exercises")]
    public async Task AdminPlusRemovedCoachRole_LibraryWrite_StillReturns403CoachRoleRemoved(UserRole removedRole, string path)
    {
        var account = await TestActors.Professional(factory, UserRole.Admin, removedRole)
            .WithRemovedCoachRoles(removedRole)
            .CreateAsync(Ct);

        var response = await SendAsync(account, "POST", path, new { });

        await AssertCoachRoleRemovedAsync(response);
    }

    [Theory]
    [InlineData("/recipes")]
    [InlineData("/foods")]
    [InlineData("/exercises")]
    public async Task AdminOnly_LibraryWrite_PassesTheRemovedRoleGate(string path)
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);

        var response = await SendAsync(admin, "POST", path, new { });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the empty body fails validation, after the gate");
        (await response.Content.ReadAsStringAsync(Ct)).Should().NotContain(ErrorCodes.CoachRoleRemoved);
    }

    /// <summary>
    /// A removed coach role stays on the account as a role claim, so Admin plus a removed Trainer is not admin-only:
    /// it keeps today's paths on the Trainers endpoints, and is refused as before on a library of the other discipline.
    /// </summary>
    [Fact]
    public async Task AdminPlusRemovedCoachRole_IsNotAdminOnly_KeepsTodaysBehaviour()
    {
        var account = await TestActors.Professional(factory, UserRole.Admin, UserRole.Trainer)
            .WithRemovedCoachRoles(UserRole.Trainer)
            .CreateAsync(Ct);

        (await account.Http.GetAsync("/trainer/pending-invites", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var trainersWrite = await SendAsync(account, "POST", "/trainer/pending-invites", new { });
        (await trainersWrite.Content.ReadAsStringAsync(Ct)).Should().NotContain(ErrorCodes.CoachRoleRemoved);

        (await SendAsync(account, "POST", "/recipes", new { })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AddBack_RestoresTheRefusedWrite()
    {
        var coach = await DualCoachWithRemovedAsync("Trainer");
        await AssertCoachRoleRemovedAsync(await SendAsync(coach, "POST", "/exercises", new { }));

        var addBack = await SendAsync(coach, "POST", "/users/me/roles", new { Role = "Trainer" });
        addBack.IsSuccessStatusCode.Should().BeTrue(await addBack.Content.ReadAsStringAsync(Ct));

        var response = await SendAsync(coach, "POST", "/exercises", new { });

        (await response.Content.ReadAsStringAsync(Ct)).Should().NotContain(ErrorCodes.CoachRoleRemoved);
    }

    [Theory]
    [InlineData(TrainingPlanStatus.Active, 0, 2, true)]
    [InlineData(TrainingPlanStatus.Active, 14, 2, true)]
    [InlineData(TrainingPlanStatus.Active, -35, 2, false)]
    [InlineData(TrainingPlanStatus.Draft, 0, 2, false)]
    public async Task RemovedTrainer_PlanScopedWrite_AllowedOnlyWhileTheActivePlanIsInProgress(
        TrainingPlanStatus status, int startOffsetDays, int weekCount, bool allowed)
    {
        var (coach, plan) = await SeedTrainerPlanAsync(status, startOffsetDays, weekCount);

        var response = await SendAsync(coach, "PUT", $"/training/plans/{plan.ExternalId}", UpdateBody(plan, weekCount));

        if (allowed)
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        }
        else
        {
            await AssertCoachRoleRemovedAsync(response);
        }
    }

    [Fact]
    public async Task RemovedTrainer_PlanScopedWrite_OnMissingPlan_IsRefused()
    {
        var coach = await DualCoachWithRemovedAsync("Trainer");

        var response = await SendAsync(coach, "PUT", $"/training/plans/{Guid.NewGuid()}", new { });

        await AssertCoachRoleRemovedAsync(response);
    }

    [Fact]
    public async Task RemovedTrainer_UpdateThatAddsWeeks_IsRefused_AndNothingIsWritten()
    {
        var (coach, plan) = await SeedTrainerPlanAsync(TrainingPlanStatus.Active, 0, 2);

        var response = await SendAsync(coach, "PUT", $"/training/plans/{plan.ExternalId}", UpdateBody(plan, 3));

        await AssertCoachRoleRemovedAsync(response);
        using var scope = factory.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<IMongoContext>().TrainingPlans
            .Find(p => p.ExternalId == plan.ExternalId).FirstAsync(Ct);
        stored.Weeks.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(NutritionPlanStatus.Active, 0, true)]
    [InlineData(NutritionPlanStatus.Active, -35, false)]
    [InlineData(NutritionPlanStatus.Draft, 0, false)]
    public async Task RemovedNutritionist_PlanScopedWrite_AllowedOnlyWhileTheActivePlanIsInProgress(
        NutritionPlanStatus status, int startOffsetDays, bool allowed)
    {
        var coach = await DualCoachWithRemovedAsync("Nutritionist");
        var plan = new NutritionPlan
        {
            ExternalId = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            NutritionistId = coach.UserId,
            Name = "Gate plan",
            Status = status,
            StartDate = TrainingPlanTestHelpers.LastMonday().AddDays(startOffsetDays),
            Version = 1,
            DateCreated = DateTime.UtcNow.AddDays(-30),
            Weeks = [new PlanWeek { WeekNumber = 1 }, new PlanWeek { WeekNumber = 2 }],
        };
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMongoContext>().NutritionPlans
                .InsertOneAsync(plan, cancellationToken: Ct);
        }

        var response = await SendAsync(coach, "PUT", $"/nutrition/plans/{plan.ExternalId}", new { });

        if (allowed)
        {
            (await response.Content.ReadAsStringAsync(Ct)).Should().NotContain(ErrorCodes.CoachRoleRemoved);
        }
        else
        {
            await AssertCoachRoleRemovedAsync(response);
        }
    }

    private async Task<(Actor Coach, TrainingPlan Plan)> SeedTrainerPlanAsync(
        TrainingPlanStatus status, int startOffsetDays, int weekCount)
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).CreateAsync(Ct);
        (await coach.Http.DeleteAsync("/users/me/roles/Trainer", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var plan = new TrainingPlan
        {
            ExternalId = Guid.NewGuid(),
            ClientId = client.UserId,
            TrainerId = coach.UserId,
            Name = "Gate plan",
            Status = status,
            StartDate = TrainingPlanTestHelpers.LastMonday().AddDays(startOffsetDays),
            Version = 1,
            DateCreated = DateTime.UtcNow.AddDays(-30),
            Weeks = Enumerable.Range(1, weekCount)
                .Select(number => new TrainingWeek { WeekNumber = number, Status = WeekStatus.Draft, Days = [] })
                .ToList(),
        };
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMongoContext>().TrainingPlans
            .InsertOneAsync(plan, cancellationToken: Ct);

        return (coach, plan);
    }

    private static object UpdateBody(TrainingPlan plan, int weekCount) => new
    {
        Name = plan.Name,
        Version = plan.Version,
        StartDate = plan.StartDate,
        Weeks = Enumerable.Range(1, weekCount)
            .Select(number => new { WeekNumber = number, Sessions = Array.Empty<object>() })
            .ToArray(),
    };
}
