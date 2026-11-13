using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.CoachSubscriptions.GetKeepClients;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Endpoints.TrainingPlans;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.CoachSubscriptions;

/// <summary>
/// Integration tests for <c>GET</c> and <c>PUT /users/me/subscription/keep-clients</c>.
/// </summary>
[Collection(TestCollection.Name)]
public class KeepClientsEndpointTests(FitnessApiFactory factory)
{
    private const string Route = "/users/me/subscription/keep-clients";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A coach whose paid plan was canceled, with three clients linked 30, 20 and 10 days ago (the Free cap is 2).
    /// </summary>
    private async Task<(Actor Coach, List<Actor> Clients)> LapsedCoachAsync()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, "pro", SubscriptionStatus.Canceled);

        var clients = new List<Actor>();

        foreach (var daysAgo in new[] { 30, 20, 10 })
        {
            var client = await TestActors.Client(factory).CreateAsync(Ct);
            await TestActors.Link(factory, coach, client)
                .WithActivatedAtUtc(DateTime.UtcNow.AddDays(-daysAgo))
                .CreateAsync(Ct);
            clients.Add(client);
        }

        return (coach, clients);
    }

    private static async Task<GetKeepClientsResponse> GetAsync(Actor coach)
    {
        var response = await coach.Http.GetAsync(Route, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return (await response.Content.ReadFromJsonAsync<GetKeepClientsResponse>(JsonOptions, Ct))!;
    }

    private static Task<HttpResponseMessage> PutAsync(Actor coach, params Guid[] clientIds) =>
        coach.Http.PutAsJsonAsync(Route, new { ClientIds = clientIds }, Ct);

    private async Task<Dictionary<Guid, ConnectionState>> LinkStatesAsync(Actor coach)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var links = await db.ClientProfessionalLinks.AsNoTracking()
            .Where(link => link.ProfessionalProfileId == coach.ProfileId)
            .Select(link => new { link.ClientProfile.PublicId, link.ConnectionState })
            .ToListAsync(Ct);

        return links.ToDictionary(link => link.PublicId, link => link.ConnectionState);
    }

    [Fact]
    public async Task Get_LapsedCoach_ListsTheLongestLinkedAsKeptFirstWithTheCap()
    {
        var (coach, clients) = await LapsedCoachAsync();

        var response = await GetAsync(coach);

        response.Cap.Should().Be(2);
        response.Clients.Select(client => client.ClientId).Should()
            .BeEquivalentTo(clients.Select(client => client.PublicId));
        response.Clients.Where(client => client.IsKept).Select(client => client.ClientId).Should()
            .BeEquivalentTo([clients[0].PublicId, clients[1].PublicId]);
        response.Clients.Single(client => !client.IsKept).Should().Match<KeepClientDto>(client =>
            client.ClientId == clients[2].PublicId
            && client.AfterLapse == KeepClientAfterLapse.ReadOnly
            && client.PublishedWeeksEnd == null
            && client.Roles.Contains(UserRole.Trainer));
    }

    [Fact]
    public async Task Get_OverCapClientWithAPublishedWeekStillRunning_ShowsWhereItEndsAndThatItRunsUntilThen()
    {
        var (coach, clients) = await LapsedCoachAsync();
        var today = DateTime.UtcNow.Date;
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var plan = TrainingPlanTestHelpers.CreatePlan(
            trainerId: coach.UserId, clientId: clients[2].UserId, status: TrainingPlanStatus.Active);
        plan.StartDate = monday;
        plan.Weeks[0].Status = WeekStatus.Published;
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMongoContext>()
                .TrainingPlans.InsertOneAsync(plan, cancellationToken: Ct);
        }

        var overCap = (await GetAsync(coach)).Clients.Single(client => !client.IsKept);

        overCap.PublishedWeeksEnd.Should().Be(DateOnly.FromDateTime(monday.AddDays(6)));
        overCap.AfterLapse.Should().Be(KeepClientAfterLapse.RunsUntilEndThenReadOnly);
    }

    private async Task SetLinkStateAsync(Actor coach, Actor client, ConnectionState state)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var link = await db.ClientProfessionalLinks.SingleAsync(
            l => l.ProfessionalProfileId == coach.ProfileId && l.ClientProfile.PublicId == client.PublicId, Ct);
        link.ConnectionState = state;
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>
    /// A coach whose choice [0, 1] is saved and whose first kept client then left: one kept client, one empty slot,
    /// the third client released. Returns the coach and the clients in link order.
    /// </summary>
    private async Task<(Actor Coach, List<Actor> Clients)> CoachWithAnEmptyKeptSlotAsync()
    {
        var (coach, clients) = await LapsedCoachAsync();

        (await PutAsync(coach, clients[0].PublicId, clients[1].PublicId)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await SetLinkStateAsync(coach, clients[0], ConnectionState.Ended);

        return (coach, clients);
    }

    [Fact]
    public async Task Put_FirstChoice_KeepsTheChosenAndReleasesTheRestAndLocksTheChoice()
    {
        var (coach, clients) = await LapsedCoachAsync();

        var before = await GetAsync(coach);
        before.IsChoiceLocked.Should().BeFalse();

        (await PutAsync(coach, clients[1].PublicId, clients[2].PublicId)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var states = await LinkStatesAsync(coach);
        states[clients[0].PublicId].Should().Be(ConnectionState.FreePlanReleased);
        states[clients[1].PublicId].Should().Be(ConnectionState.FreePlanKept);
        states[clients[2].PublicId].Should().Be(ConnectionState.FreePlanKept);

        var after = await GetAsync(coach);
        after.Clients.Where(client => client.IsKept).Select(client => client.ClientId).Should()
            .BeEquivalentTo([clients[1].PublicId, clients[2].PublicId]);
        after.IsChoiceLocked.Should().BeTrue();
        after.FreeSlotCount.Should().Be(0);
    }

    [Fact]
    public async Task Put_SwappingAKeptClientAfterTheChoice_Returns409ChoiceLockedAndChangesNothing()
    {
        var (coach, clients) = await LapsedCoachAsync();
        (await PutAsync(coach, clients[1].PublicId, clients[2].PublicId)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var saved = await LinkStatesAsync(coach);

        var response = await PutAsync(coach, clients[0].PublicId, clients[2].PublicId);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.KeepClientsChoiceLocked);
        (await LinkStatesAsync(coach)).Should().BeEquivalentTo(saved);
    }

    [Fact]
    public async Task Put_ResavingTheSameKeptSetAfterTheChoice_StaysValid()
    {
        var (coach, clients) = await LapsedCoachAsync();
        (await PutAsync(coach, clients[1].PublicId, clients[2].PublicId)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await PutAsync(coach, clients[1].PublicId, clients[2].PublicId)).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Put_KeptClientLeft_FillingTheFreeSlotWithARestrictedClientKeepsItAndReleasesNoOneElse()
    {
        var (coach, clients) = await CoachWithAnEmptyKeptSlotAsync();

        var get = await GetAsync(coach);
        get.IsChoiceLocked.Should().BeTrue();
        get.FreeSlotCount.Should().Be(1);

        (await PutAsync(coach, clients[1].PublicId, clients[2].PublicId)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var states = await LinkStatesAsync(coach);
        states[clients[0].PublicId].Should().Be(ConnectionState.Ended);
        states[clients[1].PublicId].Should().Be(ConnectionState.FreePlanKept);
        states[clients[2].PublicId].Should().Be(ConnectionState.FreePlanKept);
    }

    [Fact]
    public async Task Put_FreeSlotButTheRequestAlsoDropsAKeptClient_Returns409ChoiceLocked()
    {
        var (coach, clients) = await CoachWithAnEmptyKeptSlotAsync();
        var another = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, another).WithState(ConnectionState.FreePlanReleased).CreateAsync(Ct);

        // Same count as the free slot allows, but client 1 (kept) is swapped for two restricted clients.
        var response = await PutAsync(coach, clients[2].PublicId, another.PublicId);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.KeepClientsChoiceLocked);
        (await LinkStatesAsync(coach))[clients[1].PublicId].Should().Be(ConnectionState.FreePlanKept);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Put_LockedChoiceWithAFreeSlot_AddingFewerOrMoreThanTheFreeSlots_Returns400CountInvalid(int total)
    {
        var (coach, clients) = await CoachWithAnEmptyKeptSlotAsync();
        var another = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, another).WithState(ConnectionState.FreePlanReleased).CreateAsync(Ct);

        var ids = new[] { clients[1].PublicId, clients[2].PublicId, another.PublicId }.Take(total).ToArray();

        var response = await PutAsync(coach, ids);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.KeepClientsCountInvalid);
    }

    [Fact]
    public async Task Put_LockedChoiceWithANewlyLinkedOverCapClient_ResavingTheKeptSetReleasesTheNewClient()
    {
        var (coach, clients) = await LapsedCoachAsync();
        (await PutAsync(coach, clients[0].PublicId, clients[1].PublicId)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var newcomer = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, newcomer).CreateAsync(Ct);

        (await PutAsync(coach, clients[0].PublicId, clients[1].PublicId)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var states = await LinkStatesAsync(coach);
        states[newcomer.PublicId].Should().Be(ConnectionState.FreePlanReleased);
        states[clients[0].PublicId].Should().Be(ConnectionState.FreePlanKept);
        states[clients[1].PublicId].Should().Be(ConnectionState.FreePlanKept);
    }

    [Fact]
    public async Task Put_DuplicateIds_Returns400KeepClientsCountInvalid()
    {
        var (coach, clients) = await LapsedCoachAsync();

        var response = await PutAsync(coach, clients[0].PublicId, clients[0].PublicId);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.KeepClientsCountInvalid);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Put_WrongNumberOfClients_Returns400KeepClientsCountInvalid(int count)
    {
        var (coach, clients) = await LapsedCoachAsync();

        var response = await PutAsync(coach, clients.Take(count).Select(client => client.PublicId).ToArray());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.KeepClientsCountInvalid);
    }

    [Fact]
    public async Task Put_ClientOfAnotherCoach_Returns404AndChangesNothing()
    {
        var (coach, clients) = await LapsedCoachAsync();
        var (_, otherClients) = await LapsedCoachAsync();

        var response = await PutAsync(coach, clients[0].PublicId, otherClients[0].PublicId);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await LinkStatesAsync(coach)).Values.Should().OnlyContain(state => state == ConnectionState.Connected);
    }

    [Fact]
    public async Task Put_EndedClient_Returns404()
    {
        var (coach, clients) = await LapsedCoachAsync();
        var ended = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, ended).Inactive().CreateAsync(Ct);

        var response = await PutAsync(coach, clients[0].PublicId, ended.PublicId);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task KeepClients_CallerWithoutAProfessionalProfile_Returns404()
    {
        var userId = Guid.NewGuid();
        var email = $"{userId:N}@actor-fixture.com";
        var http = factory.CreateClient();
        TestHelpers.SetBearerToken(http, TestTokenFactory.CreateAccessToken(factory, userId, email, [AppRoles.Trainer]));

        (await http.GetAsync(Route, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await http.PutAsJsonAsync(Route, new { ClientIds = Array.Empty<Guid>() }, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(SubscriptionStatus.Active, false)]
    [InlineData(SubscriptionStatus.PastDue, false)]
    [InlineData(SubscriptionStatus.Canceled, true)]
    public async Task KeepClients_PlanNotLapsed_Returns409SubscriptionNotLapsed(SubscriptionStatus status, bool withFreeUntilInForce)
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(
            factory, coach, "pro", status, freeUntil: withFreeUntilInForce ? DateTimeOffset.UtcNow.AddDays(10) : null);

        var get = await coach.Http.GetAsync(Route, Ct);
        var put = await PutAsync(coach);

        get.StatusCode.Should().Be(HttpStatusCode.Conflict);
        put.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await put.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.SubscriptionNotLapsed);
    }
}
