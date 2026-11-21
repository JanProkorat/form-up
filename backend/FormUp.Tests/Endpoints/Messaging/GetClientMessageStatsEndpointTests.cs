using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Features.Messaging.GetClientMessageStats;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace FormUp.Tests.Endpoints.Messaging;

/// <summary>
/// Integration tests for <c>GET /trainer/clients/{ClientId}/message-stats</c>. Uses the
/// Testcontainers-backed <see cref="FitnessApiFactory"/> host — the timezone-aware week
/// bucketing needs real Postgres timestamps, and the composite auth path (client lookup, then
/// link capabilities) needs the real link tables.
/// </summary>
[Collection(TestCollection.Shard2)]
public class GetClientMessageStatsEndpointTests(FitnessApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static string UniqueEmail(string tag) => $"{Guid.NewGuid():N}@get-msg-stats-{tag}.com";

    [Fact]
    public async Task GetStats_UnknownClientId_Returns404()
    {
        var (http, _) = await SetupTrainerAsync();

        var response = await http.GetAsync(
            $"/trainer/clients/{Guid.NewGuid()}/message-stats", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetStats_ForeignClient_NotLinkedToCaller_Returns404()
    {
        var (http, _) = await SetupTrainerAsync();
        var (_, otherTrainerId) = await SetupTrainerAsync();
        var (_, foreignClientPublicId) = await SetupLinkedClientAsync(otherTrainerId);

        var response = await http.GetAsync(
            $"/trainer/clients/{foreignClientPublicId}/message-stats", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetStats_WeeksZero_Returns400()
    {
        var (http, trainerId) = await SetupTrainerAsync();
        var (_, clientPublicId) = await SetupLinkedClientAsync(trainerId);

        var response = await http.GetAsync(
            $"/trainer/clients/{clientPublicId}/message-stats?weeks=0", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetStats_WeeksOverTwentySix_Returns400()
    {
        var (http, trainerId) = await SetupTrainerAsync();
        var (_, clientPublicId) = await SetupLinkedClientAsync(trainerId);

        var response = await http.GetAsync(
            $"/trainer/clients/{clientPublicId}/message-stats?weeks=27", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetStats_DefaultWeeks_ReturnsFourRows()
    {
        var (http, trainerId) = await SetupTrainerAsync();
        var (_, clientPublicId) = await SetupLinkedClientAsync(trainerId);

        var response = await http.GetAsync(
            $"/trainer/clients/{clientPublicId}/message-stats", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Deserialize(response);
        body.Should().HaveCount(4);
    }

    [Fact]
    public async Task GetStats_NoConversation_ReturnsExactlyNZeroRows_Never404()
    {
        var (http, trainerId) = await SetupTrainerAsync();
        var (_, clientPublicId) = await SetupLinkedClientAsync(trainerId);

        var response = await http.GetAsync(
            $"/trainer/clients/{clientPublicId}/message-stats?weeks=6", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Deserialize(response);

        body.Should().HaveCount(6);
        body.Should().OnlyContain(w => w.CoachMessages == 0 && w.ClientMessages == 0);
    }

    [Fact]
    public async Task GetStats_RowsAreOrderedOldestFirst()
    {
        var (http, trainerId) = await SetupTrainerAsync();
        var (_, clientPublicId) = await SetupLinkedClientAsync(trainerId);

        var response = await http.GetAsync(
            $"/trainer/clients/{clientPublicId}/message-stats?weeks=5", TestContext.Current.CancellationToken);
        var body = await Deserialize(response);

        body.Should().HaveCount(5);
        body.Select(w => w.WeekStart).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task GetStats_HappyPath_CountsCoachAndClientMessagesInCurrentWeek()
    {
        var (http, trainerId) = await SetupTrainerAsync();
        var (clientUserId, clientPublicId) = await SetupLinkedClientAsync(trainerId);

        var conversationId = await SeedConversationAsync(trainerId, clientUserId);
        // "Now" always falls in the current ISO week, which is the LAST returned row regardless
        // of the caller's time zone offset — the endpoint always includes the current partial
        // week as its final bucket.
        await SeedMessageAsync(conversationId, trainerId, DateTime.UtcNow);
        await SeedMessageAsync(conversationId, clientUserId, DateTime.UtcNow);

        var response = await http.GetAsync(
            $"/trainer/clients/{clientPublicId}/message-stats?weeks=4", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Deserialize(response);

        body.Should().HaveCount(4);
        var currentWeekRow = body.Last();
        currentWeekRow.CoachMessages.Should().Be(1);
        currentWeekRow.ClientMessages.Should().Be(1);

        // Every earlier week has no seeded messages.
        body.Take(3).Should().OnlyContain(w => w.CoachMessages == 0 && w.ClientMessages == 0);
    }

    [Fact]
    public async Task GetStats_MessagesOutsideRequestedWindow_AreNotCounted()
    {
        var (http, trainerId) = await SetupTrainerAsync();
        var (clientUserId, clientPublicId) = await SetupLinkedClientAsync(trainerId);

        var conversationId = await SeedConversationAsync(trainerId, clientUserId);
        // Far outside any requested window (weeks=2 covers roughly the last 14 days).
        await SeedMessageAsync(conversationId, trainerId, DateTime.UtcNow.AddDays(-365));

        var response = await http.GetAsync(
            $"/trainer/clients/{clientPublicId}/message-stats?weeks=2", TestContext.Current.CancellationToken);
        var body = await Deserialize(response);

        body.Should().HaveCount(2);
        body.Should().OnlyContain(w => w.CoachMessages == 0 && w.ClientMessages == 0);
    }

    [Fact]
    public async Task HandleAsync_CallerUserRowMissing_Returns200InUtc()
    {
        var trainerId = Guid.NewGuid();
        var clientUser = EntityBuilder.User.WithEmail("stats-client@test.com").Build();
        var clientProfile = EntityBuilder.ClientProfile.WithId(1).WithUser(clientUser).Build();

        // No ApplicationUser row for the caller — the time-zone lookup yields nothing.
        var db = new MockDbBuilder().With(clientProfile).Build();

        var linkAuthorization = Substitute.For<IClientLinkAuthorizationService>();
        linkAuthorization
            .GetCapabilitiesByClientPublicIdAsync(trainerId, clientProfile.PublicId, Arg.Any<CancellationToken>())
            .Returns(new LinkCapabilities(true, true));

        // 2025-03-12 is a Wednesday; its ISO week starts Monday 2025-03-10 in UTC.
        var fixedNow = new FakeNow(new DateTimeOffset(2025, 3, 12, 10, 0, 0, TimeSpan.Zero));

        var ep = Factory.Create<GetClientMessageStatsEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(trainerId, AppRoles.Trainer))),
            db, linkAuthorization, fixedNow);

        await ep.HandleAsync(
            new GetClientMessageStatsRequest { ClientId = clientProfile.PublicId, Weeks = 2 },
            TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(200);
        ep.Response.Select(w => w.WeekStart).Should().Equal(new DateOnly(2025, 3, 3), new DateOnly(2025, 3, 10));
    }

    [Fact]
    public void LocalMidnightToUtc_MidnightSkippedByDstGap_MovesToFirstValidInstant()
    {
        // Clocks jump 00:00 -> 01:00 on Monday 2025-03-10, so local midnight does not exist.
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2025, 1, 1),
            new DateTime(2025, 12, 31),
            TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 3, 10),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 11, 1));
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test/SkipMidnight", TimeSpan.Zero, "t", "t", "t-dst", [rule]);
        var monday = new DateOnly(2025, 3, 10);
        zone.IsInvalidTime(monday.ToDateTime(TimeOnly.MinValue)).Should().BeTrue("the fixture must skip midnight");

        var utc = GetClientMessageStatsEndpoint.LocalMidnightToUtc(monday, zone);

        // First valid local instant is 01:00 at +01:00, i.e. 00:00 UTC.
        utc.Should().Be(new DateTime(2025, 3, 10, 0, 0, 0, DateTimeKind.Utc));
    }

    // ── fixtures ─────────────────────────────────────────────────────────────

    private sealed class FakeNow(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private async Task<(HttpClient Http, Guid UserId)> SetupTrainerAsync()
    {
        var http = factory.CreateClient();
        var email = UniqueEmail("trainer");

        await TestHelpers.RegisterAsync(http, email, "TestPass1!", "Test", "Trainer", "Trainer");
        var (token, _) = await TestHelpers.LoginAsync(http, email, "TestPass1!");
        TestHelpers.SetBearerToken(http, token);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.AsNoTracking()
            .FirstAsync(u => u.Email == email, TestContext.Current.CancellationToken);

        return (http, user.Id);
    }

    private async Task<(Guid ClientUserId, Guid ClientPublicId)> SetupLinkedClientAsync(Guid professionalUserId)
    {
        var clientUserId = await TestHelpers.RegisterLinkedClientAsync(
            factory, professionalUserId, TestContext.Current.CancellationToken);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var clientProfile = await db.ClientProfiles.AsNoTracking()
            .FirstAsync(cp => cp.UserId == clientUserId, TestContext.Current.CancellationToken);

        return (clientUserId, clientProfile.PublicId);
    }

    private async Task<long> SeedConversationAsync(Guid professionalUserId, Guid clientUserId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var conversation = new Conversation
        {
            PublicId = Guid.NewGuid(),
            ProfessionalUserId = professionalUserId,
            ClientUserId = clientUserId
        };
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return conversation.Id;
    }

    /// <summary>
    /// Seeds a message and forces its <c>DateCreated</c> to <paramref name="timestampUtc"/>.
    /// <see cref="ApplicationDbContext"/>'s <c>ApplyTimestamps</c> unconditionally overwrites
    /// <c>DateCreated</c> with <c>DateTime.UtcNow</c> on insert, so the desired timestamp must be
    /// re-applied afterward via <c>ExecuteUpdateAsync</c> (bypasses the change tracker) — the same
    /// two-pass trick <c>GetMessagesEndpointTests.SeedMessagesAsync</c> uses.
    /// </summary>
    private async Task SeedMessageAsync(long conversationId, Guid senderUserId, DateTime timestampUtc)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var publicId = Guid.NewGuid();
        db.ChatMessages.Add(new ChatMessage
        {
            PublicId = publicId,
            ConversationId = conversationId,
            SenderUserId = senderUserId,
            Text = "hello",
            IsRead = false
        });
        await db.SaveChangesAsync(ct);

        await db.ChatMessages
            .Where(m => m.PublicId == publicId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.DateCreated, timestampUtc), ct);
    }

    private static async Task<List<WeeklyMessageStatsDto>> Deserialize(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<List<WeeklyMessageStatsDto>>(
            JsonOptions, TestContext.Current.CancellationToken))!;

    private sealed class WeeklyMessageStatsDto
    {
        public DateOnly WeekStart { get; set; }
        public int CoachMessages { get; set; }
        public int ClientMessages { get; set; }
    }
}
