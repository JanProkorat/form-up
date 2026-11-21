using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using FormUp.Application.Domain.Enums;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;

namespace FormUp.Tests.Endpoints.Users.EmailLinks;

/// <summary>
/// Integration tests for <c>POST /users/email-links/turn-off</c>.
/// </summary>
[Collection(TestCollection.Shard2)]
public class EmailLinkTurnOffTests(FitnessApiFactory factory)
{
    private const string Route = "/users/email-links/turn-off";

    private Task<HttpResponseMessage> TurnOffAsync(string token) =>
        factory.CreateClient().PostAsJsonAsync(Route, new { Token = token }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task TurnOff_ValidToken_SetsOnlyThatEventsEmailOffAndLeavesEveryOtherRowAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        var actor = await TestActors.Trainer(factory).CreateAsync(ct);
        await EmailLinkTestSupport.SeedAsync(factory, actor.UserId, NotificationEvent.WeeklyCheckInSubmitted, email: true, push: false);
        await EmailLinkTestSupport.SeedAsync(factory, actor.UserId, NotificationEvent.NewMessage, email: true, push: true);

        var response = await TurnOffAsync(EmailLinkTestSupport.Valid(factory, actor.UserId, NotificationEvent.WeeklyCheckInSubmitted));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        body.GetProperty("email").GetString().Should().Be(actor.Email);
        body.GetProperty("event").GetString().Should().Be("WeeklyCheckInSubmitted");

        var rows = await EmailLinkTestSupport.RowsAsync(factory, actor.UserId);
        rows.Should().HaveCount(2);
        rows.Single(row => row.Event == NotificationEvent.WeeklyCheckInSubmitted)
            .Should().Match<Application.Domain.Entities.NotificationPreference>(row => !row.EmailEnabled && !row.PushEnabled);
        rows.Single(row => row.Event == NotificationEvent.NewMessage)
            .Should().Match<Application.Domain.Entities.NotificationPreference>(row => row.EmailEnabled && row.PushEnabled);
    }

    [Theory]
    [InlineData(NotificationEvent.JoinRequest, true)]
    [InlineData(NotificationEvent.WorkoutFinished, false)]
    public async Task TurnOff_UserWithNoSavedRow_CreatesAnExplicitRowWithPushAtItsDefault(
        NotificationEvent notificationEvent, bool defaultPush)
    {
        var actor = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);

        var response = await TurnOffAsync(EmailLinkTestSupport.Valid(factory, actor.UserId, notificationEvent));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var row = (await EmailLinkTestSupport.RowsAsync(factory, actor.UserId)).Should().ContainSingle().Subject;
        row.Event.Should().Be(notificationEvent);
        row.EmailEnabled.Should().BeFalse();
        row.PushEnabled.Should().Be(defaultPush);
    }

    [Fact]
    public async Task TurnOff_SameLinkTwice_BothSucceedAndLeaveOneOffRow()
    {
        var actor = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);
        var token = EmailLinkTestSupport.Valid(factory, actor.UserId, NotificationEvent.JoinRequest);

        (await TurnOffAsync(token)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await TurnOffAsync(token)).StatusCode.Should().Be(HttpStatusCode.OK);

        var row = (await EmailLinkTestSupport.RowsAsync(factory, actor.UserId)).Should().ContainSingle().Subject;
        row.EmailEnabled.Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task TurnOff_InvalidLink_Returns400WithOneIdenticalBodyAndChangesNothing(string scenario)
    {
        var ct = TestContext.Current.CancellationToken;
        var actor = await TestActors.Trainer(factory).CreateAsync(ct);
        await EmailLinkTestSupport.SeedAsync(factory, actor.UserId, NotificationEvent.JoinRequest, email: true, push: true);
        var baseline = await EmailLinkTestSupport.ComparableBodyAsync(await TurnOffAsync("not-a-token"));

        var response = await TurnOffAsync(EmailLinkTestSupport.Invalid(factory, scenario, actor.UserId, NotificationEvent.JoinRequest));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await EmailLinkTestSupport.ComparableBodyAsync(response);
        baseline.Should().Contain("EMAIL_LINK_INVALID");
        (body == baseline).Should().BeTrue($"every invalid link must get the same body, but got: {body}");
        var row = (await EmailLinkTestSupport.RowsAsync(factory, actor.UserId)).Should().ContainSingle().Subject;
        row.EmailEnabled.Should().BeTrue();
        row.PushEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task TurnOff_UnknownAccount_InsertsNoRow()
    {
        var ghost = Guid.NewGuid();

        var response = await TurnOffAsync(EmailLinkTestSupport.Valid(factory, ghost, NotificationEvent.JoinRequest));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await EmailLinkTestSupport.RowsAsync(factory, ghost)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task TurnOff_BlankToken_Returns400WithTheSameErrorCode(string token)
    {
        var response = await TurnOffAsync(token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("EMAIL_LINK_INVALID");
    }

    [Fact]
    public async Task TurnOff_OversizedToken_Returns400WithTheSameErrorCode()
    {
        var response = await TurnOffAsync(new string('A', 257));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("EMAIL_LINK_INVALID");
    }

    [Fact]
    public async Task TurnOff_Get_Returns405AndChangesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var actor = await TestActors.Trainer(factory).CreateAsync(ct);
        var token = EmailLinkTestSupport.Valid(factory, actor.UserId, NotificationEvent.JoinRequest);

        var response = await factory.CreateClient().GetAsync($"{Route}?token={token}", ct);

        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        (await EmailLinkTestSupport.RowsAsync(factory, actor.UserId)).Should().BeEmpty();
    }

    public static TheoryData<string> Scenarios() => [.. EmailLinkTestSupport.InvalidScenarios];
}
