using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.WeeklyCheckIns;

/// <summary>
/// GET /trainer/weekly-check-ins: the caller's check-ins read from their form responses.
/// </summary>
[Collection(TestCollection.Shard4)]
public class GetTrainerCheckInsEndpointTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<List<CheckInDto>> GetAsync(Actor coach, string query = "")
    {
        var response = await coach.Http.GetAsync($"/trainer/weekly-check-ins{query}", Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<CheckInsWrapper>(cancellationToken: Ct);
        return body!.CheckIns;
    }

    [Fact]
    public async Task GetList_Unauthenticated_Returns401()
    {
        var response = await factory.CreateClient().GetAsync("/trainer/weekly-check-ins", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetList_WeekGiven_ReturnsOnlyOwnCheckInsOfThatWeekWithClientPublicId()
    {
        var scenario = await CheckInFixtures.CreateScenarioAsync(factory);
        var week = CheckInFixtures.Monday(1);
        var id = await CheckInFixtures.AddCheckInAsync(
            factory, scenario, week, DateTime.UtcNow.AddDays(1),
            QuestionnaireResponseStatus.Submitted, DateTime.UtcNow.AddHours(-1));
        await CheckInFixtures.AddCheckInAsync(factory, scenario, CheckInFixtures.Monday(2), DateTime.UtcNow.AddDays(1));
        var other = await CheckInFixtures.CreateScenarioAsync(factory, client: scenario.Client);
        await CheckInFixtures.AddCheckInAsync(factory, other, week, DateTime.UtcNow.AddDays(1));

        var checkIns = await GetAsync(scenario.Coach, $"?weekStartDate={week:yyyy-MM-dd}");

        var dto = checkIns.Should().ContainSingle().Subject;
        dto.Id.Should().Be(id);
        dto.ClientUserId.Should().Be(scenario.Client.UserId);
        dto.ClientPublicId.Should().Be(scenario.Client.PublicId);
        dto.Status.Should().Be("Responded");
        dto.Profession.Should().Be("Training");
    }

    [Fact]
    public async Task GetList_WeekOmitted_ReturnsUnreviewedOfCurrentCollaborationOnly()
    {
        var scenario = await CheckInFixtures.CreateScenarioAsync(factory, activatedAtUtc: DateTime.UtcNow.AddDays(-5));
        var waiting = await CheckInFixtures.AddCheckInAsync(
            factory, scenario, CheckInFixtures.Monday(0), DateTime.UtcNow.AddDays(1));
        await CheckInFixtures.AddCheckInAsync(
            factory, scenario, CheckInFixtures.Monday(1), DateTime.UtcNow.AddDays(1),
            QuestionnaireResponseStatus.Submitted, DateTime.UtcNow.AddHours(-1), reviewedAt: DateTime.UtcNow);
        await CheckInFixtures.AddCheckInAsync(
            factory, scenario, CheckInFixtures.Monday(3), DateTime.UtcNow.AddDays(-20),
            QuestionnaireResponseStatus.Submitted, DateTime.UtcNow.AddDays(-21));

        var checkIns = await GetAsync(scenario.Coach);

        checkIns.Select(c => c.Id).Should().Equal(waiting);
    }

    [Fact]
    public async Task GetList_EachRow_ReportsItsOwnFormAndTheClientsTimeZone()
    {
        var scenario = await CheckInFixtures.CreateScenarioAsync(factory, activatedAtUtc: DateTime.UtcNow.AddDays(-5));
        var (otherFormId, otherFormPublicId) = await CheckInFixtures.AddFormAsync(factory, scenario, $"Override {Guid.NewGuid():N}");
        var defaultRow = await CheckInFixtures.AddCheckInAsync(
            factory, scenario, CheckInFixtures.Monday(0), DateTime.UtcNow.AddDays(1));
        var overrideRow = await CheckInFixtures.AddCheckInAsync(
            factory, scenario, CheckInFixtures.Monday(1), DateTime.UtcNow.AddDays(1), questionnaireId: otherFormId);
        await SetTimeZoneAsync(scenario.Client.UserId, "America/New_York");

        var checkIns = await GetAsync(scenario.Coach);

        var defaultDto = checkIns.Single(c => c.Id == defaultRow);
        var overrideDto = checkIns.Single(c => c.Id == overrideRow);
        defaultDto.FormId.Should().Be(await FormPublicIdAsync(scenario.QuestionnaireId));
        defaultDto.FormName.Should().Be("Weekly check-in");
        overrideDto.FormId.Should().Be(otherFormPublicId);
        overrideDto.FormName.Should().StartWith("Override ");
        checkIns.Should().OnlyContain(c => c.ClientTimeZone == "America/New_York");
    }

    [Fact]
    public async Task GetList_CoachOverTheCap_FlagsOnlyTheOverCapClients()
    {
        var scenarios = await CheckInFixtures.CreateFreePlanCoachScenariosAsync(factory, 3);

        foreach (var scenario in scenarios)
        {
            await CheckInFixtures.AddCheckInAsync(
                factory, scenario, CheckInFixtures.Monday(0), DateTime.UtcNow.AddDays(1));
        }

        var checkIns = await GetAsync(scenarios[0].Coach);

        checkIns.Should().HaveCount(3);
        checkIns.Single(c => c.ClientUserId == scenarios[2].Client.UserId).ClientIsOutsidePlan.Should().BeTrue();
        checkIns.Where(c => c.ClientUserId != scenarios[2].Client.UserId).Should().OnlyContain(c => !c.ClientIsOutsidePlan);
    }

    private async Task SetTimeZoneAsync(Guid userId, string timeZone)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.SingleAsync(u => u.Id == userId, Ct);
        user.TimeZone = timeZone;
        await db.SaveChangesAsync(Ct);
    }

    private async Task<Guid> FormPublicIdAsync(long questionnaireId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Questionnaires.Where(q => q.Id == questionnaireId).Select(q => q.PublicId).SingleAsync(Ct);
    }

    private record CheckInsWrapper(List<CheckInDto> CheckIns);

    private record CheckInDto(
        Guid Id, Guid ClientUserId, Guid ClientPublicId, string Profession, string Status,
        Guid FormId, string FormName, string ClientTimeZone, bool ClientIsOutsidePlan);
}
