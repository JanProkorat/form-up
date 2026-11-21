using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using FormUp.Application.Domain.Enums;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;

namespace FormUp.Tests.Endpoints.Users.EmailLinks;

/// <summary>
/// Integration tests for <c>POST /users/email-links/turn-on</c>.
/// </summary>
[Collection(TestCollection.Shard4)]
public class EmailLinkTurnOnTests(FitnessApiFactory factory)
{
    private const string Route = "/users/email-links/turn-on";

    private Task<HttpResponseMessage> TurnOnAsync(string token) =>
        factory.CreateClient().PostAsJsonAsync(Route, new { Token = token }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task TurnOn_ValidToken_SetsEmailOnAndLeavesPushAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        var actor = await TestActors.Trainer(factory).CreateAsync(ct);
        await EmailLinkTestSupport.SeedAsync(factory, actor.UserId, NotificationEvent.NewMessage, email: false, push: false);

        var response = await TurnOnAsync(EmailLinkTestSupport.Valid(factory, actor.UserId, NotificationEvent.NewMessage));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        body.GetProperty("email").GetString().Should().Be(actor.Email);
        body.GetProperty("event").GetString().Should().Be("NewMessage");
        var row = (await EmailLinkTestSupport.RowsAsync(factory, actor.UserId)).Should().ContainSingle().Subject;
        row.EmailEnabled.Should().BeTrue();
        row.PushEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task TurnOn_UserWithNoSavedRow_CreatesAnExplicitRowWithPushAtItsDefault()
    {
        var actor = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);

        var response = await TurnOnAsync(EmailLinkTestSupport.Valid(factory, actor.UserId, NotificationEvent.ProgressPhotosSubmitted));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var row = (await EmailLinkTestSupport.RowsAsync(factory, actor.UserId)).Should().ContainSingle().Subject;
        row.EmailEnabled.Should().BeTrue();
        row.PushEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task TurnOn_AfterTurnOffWithTheSameLink_RestoresTheEmail()
    {
        var actor = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);
        var token = EmailLinkTestSupport.Valid(factory, actor.UserId, NotificationEvent.WeeklyCheckInSubmitted);

        var off = await factory.CreateClient().PostAsJsonAsync(
            "/users/email-links/turn-off", new { Token = token }, TestContext.Current.CancellationToken);
        var on = await TurnOnAsync(token);

        off.StatusCode.Should().Be(HttpStatusCode.OK);
        on.StatusCode.Should().Be(HttpStatusCode.OK);
        var row = (await EmailLinkTestSupport.RowsAsync(factory, actor.UserId)).Should().ContainSingle().Subject;
        row.EmailEnabled.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task TurnOn_InvalidLink_Returns400WithOneIdenticalBodyAndChangesNothing(string scenario)
    {
        var ct = TestContext.Current.CancellationToken;
        var actor = await TestActors.Trainer(factory).CreateAsync(ct);
        await EmailLinkTestSupport.SeedAsync(factory, actor.UserId, NotificationEvent.JoinRequest, email: false, push: true);
        var baseline = await EmailLinkTestSupport.ComparableBodyAsync(await TurnOnAsync("not-a-token"));

        var response = await TurnOnAsync(EmailLinkTestSupport.Invalid(factory, scenario, actor.UserId, NotificationEvent.JoinRequest));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await EmailLinkTestSupport.ComparableBodyAsync(response);
        baseline.Should().Contain("EMAIL_LINK_INVALID");
        (body == baseline).Should().BeTrue($"every invalid link must get the same body, but got: {body}");
        var row = (await EmailLinkTestSupport.RowsAsync(factory, actor.UserId)).Should().ContainSingle().Subject;
        row.EmailEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task TurnOn_Get_Returns405()
    {
        var response = await factory.CreateClient().GetAsync($"{Route}?token=x", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }

    public static TheoryData<string> Scenarios() => [.. EmailLinkTestSupport.InvalidScenarios];
}
