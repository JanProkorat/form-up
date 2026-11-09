using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.CoachSubscriptions.ChooseFreePlan;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Endpoints.NutritionPlans;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.CoachSubscriptions;

/// <summary>
/// Integration tests for <c>POST /users/me/subscription/choose-free</c>.
/// </summary>
[Collection(TestCollection.Name)]
public class ChooseFreePlanEndpointTests(FitnessApiFactory factory)
{
    private const string Route = "/users/me/subscription/choose-free";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A nutritionist whose given Pro plan ended, with clients linked the given number of days ago.</summary>
    private async Task<(Actor Coach, List<Actor> Clients)> EndedGiftCoachAsync(params int[] linkedDaysAgo)
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeWithEndedGiftAsync(factory, coach);
        var clients = await TestActors.LinkClientsAsync(factory, coach, UserRole.Nutritionist, linkedDaysAgo);

        return (coach, clients);
    }

    private static Task<HttpResponseMessage> ChooseAsync(Actor coach, params Guid[] keepClientIds) =>
        coach.Http.PostAsJsonAsync(Route, new ChooseFreePlanRequest { KeepClientIds = keepClientIds.ToList() }, Ct);

    private async Task<Dictionary<Guid, ConnectionState>> LinkStatesAsync(Actor coach)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.ClientProfessionalLinks.AsNoTracking()
            .Where(link => link.ProfessionalProfileId == coach.ProfileId)
            .Select(link => new { link.ClientProfile.PublicId, link.ConnectionState })
            .ToDictionaryAsync(link => link.PublicId, link => link.ConnectionState, Ct);
    }

    private async Task AssertStillWaitingForAChoiceAsync(Actor coach)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await db.CoachSubscriptions.AsNoTracking()
            .Where(cs => cs.ProfessionalProfileId == coach.ProfileId)
            .Select(cs => new { cs.GivenFreeAt, cs.FreeUntil, cs.SubscriptionPlan.Code })
            .SingleAsync(Ct);

        row.GivenFreeAt.Should().NotBeNull();
        row.FreeUntil.Should().NotBeNull();
        row.Code.Should().Be("pro");
    }

    [Fact]
    public async Task Choose_UnderTheCap_MovesToTheFreePlanAndLeavesTheLinksAlone()
    {
        var (coach, clients) = await EndedGiftCoachAsync(30, 20);

        var response = await coach.Http.PostAsJsonAsync(Route, new { }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await db.CoachSubscriptions.AsNoTracking()
            .Where(cs => cs.ProfessionalProfileId == coach.ProfileId)
            .Select(cs => new
            {
                cs.SubscriptionPlan.Code,
                cs.Status,
                cs.GivenFreeAt,
                cs.FreeUntil,
                cs.PaysSecondRoleAddOn,
                PriceIsOpen = cs.SubscriptionPlanPrice.ValidTo == null,
                PricePlanCode = cs.SubscriptionPlanPrice.SubscriptionPlan.Code,
            })
            .SingleAsync(Ct);

        row.Should().BeEquivalentTo(new
        {
            Code = SubscriptionPlanCodes.Free,
            Status = SubscriptionStatus.Active,
            GivenFreeAt = (DateTimeOffset?)null,
            FreeUntil = (DateTimeOffset?)null,
            PaysSecondRoleAddOn = false,
            PriceIsOpen = true,
            PricePlanCode = SubscriptionPlanCodes.Free,
        });
        (await LinkStatesAsync(coach)).Values.Should().OnlyContain(state => state == ConnectionState.Connected);
        clients.Should().HaveCount(2);
    }

    [Fact]
    public async Task Choose_WritesAnAuditRowForTheCoach()
    {
        var (coach, _) = await EndedGiftCoachAsync();

        (await coach.Http.PostAsJsonAsync(Route, new { }, Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = factory.Services.CreateScope();
        var audit = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().AuditLogs.AsNoTracking()
            .Where(a => a.EntityId == coach.UserId && a.Action == ChooseFreePlanEndpoint.AuditAction)
            .ToListAsync(Ct);

        audit.Should().ContainSingle().Which.UserId.Should().Be(coach.UserId);
    }

    [Fact]
    public async Task Choose_OverTheCapWithAKeepList_KeepsTheChosenAndReleasesTheRest()
    {
        var (coach, clients) = await EndedGiftCoachAsync(30, 20, 10);

        var response = await ChooseAsync(coach, clients[1].PublicId, clients[2].PublicId);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var states = await LinkStatesAsync(coach);
        states[clients[0].PublicId].Should().Be(ConnectionState.FreePlanReleased);
        states[clients[1].PublicId].Should().Be(ConnectionState.FreePlanKept);
        states[clients[2].PublicId].Should().Be(ConnectionState.FreePlanKept);
    }

    [Fact]
    public async Task Choose_AfterTheFirstChoice_Returns409BecauseThereIsNothingLeftToChoose()
    {
        var (coach, _) = await EndedGiftCoachAsync();
        (await coach.Http.PostAsJsonAsync(Route, new { }, Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var second = await coach.Http.PostAsJsonAsync(Route, new { }, Ct);

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await second.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.PlanChoiceNotRequired);
    }

    [Fact]
    public async Task Choose_CoachWithNoSubscription_Returns409()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);

        var response = await coach.Http.PostAsJsonAsync(Route, new { }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.PlanChoiceNotRequired);
    }

    [Theory]
    [InlineData("gift-in-force")]
    [InlineData("gift-without-end")]
    [InlineData("paid-active")]
    [InlineData("paid-canceled")]
    public async Task Choose_WhenNoGivenPlanHasEnded_Returns409AndChangesNothing(string situation)
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var now = DateTimeOffset.UtcNow;

        switch (situation)
        {
            case "gift-in-force":
                await CoachSubscriptionBuilder.SubscribeToPlanAsync(
                    factory, coach, "pro", freeUntil: now.AddDays(3), givenFreeAt: now.AddDays(-3));
                break;
            case "gift-without-end":
                await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, "pro", givenFreeAt: now.AddDays(-3));
                break;
            case "paid-canceled":
                await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, "pro", SubscriptionStatus.Canceled);
                break;
            default:
                await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, "pro");
                break;
        }

        var response = await coach.Http.PostAsJsonAsync(Route, new { }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.PlanChoiceNotRequired);
    }

    [Fact]
    public async Task Choose_OverTheCapWithoutAKeepList_Returns400AndChangesNothing()
    {
        var (coach, _) = await EndedGiftCoachAsync(30, 20, 10);

        var response = await coach.Http.PostAsJsonAsync(Route, new { }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.KeepClientsCountInvalid);
        await AssertStillWaitingForAChoiceAsync(coach);
        (await LinkStatesAsync(coach)).Values.Should().OnlyContain(state => state == ConnectionState.Connected);
    }

    [Fact]
    public async Task Choose_DuplicateIds_Returns400()
    {
        var (coach, clients) = await EndedGiftCoachAsync(30, 20, 10);

        var response = await ChooseAsync(coach, clients[0].PublicId, clients[0].PublicId);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.KeepClientsCountInvalid);
        await AssertStillWaitingForAChoiceAsync(coach);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Choose_WrongNumberOfClients_Returns400(int count)
    {
        var (coach, clients) = await EndedGiftCoachAsync(30, 20, 10);

        var response = await ChooseAsync(coach, clients.Take(count).Select(client => client.PublicId).ToArray());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.KeepClientsCountInvalid);
        await AssertStillWaitingForAChoiceAsync(coach);
    }

    [Fact]
    public async Task Choose_ClientOfAnotherCoach_Returns404AndChangesNothing()
    {
        var (coach, clients) = await EndedGiftCoachAsync(30, 20, 10);
        var (_, otherClients) = await EndedGiftCoachAsync(30);

        var response = await ChooseAsync(coach, clients[0].PublicId, otherClients[0].PublicId);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await AssertStillWaitingForAChoiceAsync(coach);
        (await LinkStatesAsync(coach)).Values.Should().OnlyContain(state => state == ConnectionState.Connected);
    }

    private static DateTime ThisMonday()
    {
        var today = DateTime.UtcNow.Date;

        return today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
    }

    private async Task SeedRunningPlanAsync(Guid coachUserId, Guid clientUserId)
    {
        var plan = PlanTestHelpers.CreatePlan(
            nutritionistId: coachUserId, clientId: clientUserId, status: NutritionPlanStatus.Active);
        plan.StartDate = ThisMonday();
        plan.Weeks[0].Status = WeekStatus.Published;
        plan.Weeks[0].DatePublished = DateTime.UtcNow;

        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMongoContext>()
            .NutritionPlans.InsertOneAsync(plan, cancellationToken: Ct);
    }

    private async Task<Guid> SeedTemplateAsync(Guid ownerId)
    {
        var template = new NutritionPlanTemplate
        {
            ExternalId = Guid.NewGuid(),
            OwnerId = ownerId,
            Name = "Choose-free template",
            Visibility = LibraryVisibility.Private,
            Version = 1,
            DateCreated = DateTime.UtcNow,
            WeekCount = 1,
            Weeks = [new TemplateWeek { WeekNumber = 1, Days = [] }],
        };

        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMongoContext>()
            .NutritionPlanTemplates.InsertOneAsync(template, cancellationToken: Ct);

        return template.ExternalId;
    }

    [Fact]
    public async Task AfterChoosingFree_AReleasedClientRunsUntilItsPublishedWeekEndsThenIsOverThePlanLimit()
    {
        var (coach, clients) = await EndedGiftCoachAsync(30, 20, 10);
        await SeedRunningPlanAsync(coach.UserId, clients[0].UserId);
        var templateId = await SeedTemplateAsync(coach.UserId);

        (await ChooseAsync(coach, clients[1].PublicId, clients[2].PublicId)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var released = await clients[0].Http.GetAsync("/client/nutrition/plan/full", Ct);
        released.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonDocument.Parse(await released.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("runsUntil").GetString()
            .Should().Be(DateOnly.FromDateTime(ThisMonday().AddDays(6)).ToString("yyyy-MM-dd"));

        var refused = await coach.Http.PostAsJsonAsync(
            $"/nutrition/plan-templates/{templateId}/instantiate", new { ClientId = clients[0].PublicId, Name = "Plan" }, Ct);
        var allowed = await coach.Http.PostAsJsonAsync(
            $"/nutrition/plan-templates/{templateId}/instantiate", new { ClientId = clients[1].PublicId, Name = "Plan" }, Ct);

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await refused.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.ClientOverPlanLimit);
        allowed.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private static async Task<HashSet<Guid>> KeptClientIdsAsync(Actor coach)
    {
        var response = await coach.Http.GetAsync("/users/me/subscription/keep-clients", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("clients").EnumerateArray()
            .Where(client => client.GetProperty("isKept").GetBoolean())
            .Select(client => client.GetProperty("clientId").GetGuid())
            .ToHashSet();
    }

    [Fact]
    public async Task KeepClients_OnTheFreePlanOverTheCap_CanBeRepickedAfterChoosingFree()
    {
        var (coach, clients) = await EndedGiftCoachAsync(30, 20, 10);
        (await ChooseAsync(coach, clients[1].PublicId, clients[2].PublicId)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await KeptClientIdsAsync(coach)).Should().BeEquivalentTo([clients[1].PublicId, clients[2].PublicId]);

        var repick = await coach.Http.PutAsJsonAsync(
            "/users/me/subscription/keep-clients", new { ClientIds = new[] { clients[0].PublicId, clients[1].PublicId } }, Ct);

        repick.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var states = await LinkStatesAsync(coach);
        states[clients[0].PublicId].Should().Be(ConnectionState.FreePlanKept);
        states[clients[1].PublicId].Should().Be(ConnectionState.FreePlanKept);
        states[clients[2].PublicId].Should().Be(ConnectionState.FreePlanReleased);
        (await KeptClientIdsAsync(coach)).Should().BeEquivalentTo([clients[0].PublicId, clients[1].PublicId]);
    }

    [Fact]
    public async Task KeepClients_OnTheFreePlanOverTheCap_RejectsAWrongNumberOfClients()
    {
        var (coach, clients) = await EndedGiftCoachAsync(30, 20, 10);
        (await ChooseAsync(coach, clients[1].PublicId, clients[2].PublicId)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var response = await coach.Http.PutAsJsonAsync(
            "/users/me/subscription/keep-clients", new { ClientIds = new[] { clients[0].PublicId } }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.KeepClientsCountInvalid);
    }

    [Fact]
    public async Task KeepClients_OnTheFreePlanWithinTheCap_StillReturns409()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, SubscriptionPlanCodes.Free);
        var clients = await TestActors.LinkClientsAsync(factory, coach, UserRole.Trainer, 30, 20);

        var get = await coach.Http.GetAsync("/users/me/subscription/keep-clients", Ct);
        var put = await coach.Http.PutAsJsonAsync(
            "/users/me/subscription/keep-clients", new { ClientIds = clients.Select(client => client.PublicId).ToArray() }, Ct);

        get.StatusCode.Should().Be(HttpStatusCode.Conflict);
        put.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await put.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.SubscriptionNotLapsed);
    }
}
