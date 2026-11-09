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

    [Fact]
    public async Task Put_ValidChoice_KeepsTheChosenAndReleasesTheRestAndCanBeChanged()
    {
        var (coach, clients) = await LapsedCoachAsync();

        (await PutAsync(coach, clients[1].PublicId, clients[2].PublicId)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var first = await LinkStatesAsync(coach);
        first[clients[0].PublicId].Should().Be(ConnectionState.FreePlanReleased);
        first[clients[1].PublicId].Should().Be(ConnectionState.FreePlanKept);
        first[clients[2].PublicId].Should().Be(ConnectionState.FreePlanKept);
        (await GetAsync(coach)).Clients.Where(client => client.IsKept).Select(client => client.ClientId).Should()
            .BeEquivalentTo([clients[1].PublicId, clients[2].PublicId]);

        (await PutAsync(coach, clients[0].PublicId, clients[2].PublicId)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var second = await LinkStatesAsync(coach);
        second[clients[0].PublicId].Should().Be(ConnectionState.FreePlanKept);
        second[clients[1].PublicId].Should().Be(ConnectionState.FreePlanReleased);
        second[clients[2].PublicId].Should().Be(ConnectionState.FreePlanKept);
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
