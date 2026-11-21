using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Messaging;

/// <summary>
/// Integration tests for the per-user IANA time zone bucketing on
/// <c>GET /trainer/clients/{ClientId}/message-stats</c> — the cases
/// <see cref="GetClientMessageStatsEndpointTests"/> does not cover, which all use the default
/// "Europe/Prague" caller time zone. Uses the Testcontainers-backed <see cref="FitnessApiFactory"/>
/// host; real UTC "now" (via <see cref="TimeProvider.System"/>, the app's registered clock)
/// combined with a fixed, deliberately far-in-the-past seeded message timestamp — the endpoint has
/// no injectable fake clock, so these tests anchor on "now" rather than pinning it.
/// </summary>
[Collection(TestCollection.Shard2)]
public class GetClientMessageStatsIntegrationTests(FitnessApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static string UniqueEmail(string tag) => $"{Guid.NewGuid():N}@get-msg-stats-tz-{tag}.com";

    [Fact]
    public async Task GetStats_UnknownTimeZoneId_FallsBackToUtc_ReturnsOkNotError()
    {
        var (http, trainerId) = await SetupTrainerAsync();
        var (_, clientPublicId) = await SetupLinkedClientAsync(trainerId);
        await SetTrainerTimeZoneAsync(trainerId, "Not/ARealZone");

        var response = await http.GetAsync(
            $"/trainer/clients/{clientPublicId}/message-stats?weeks=4", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Deserialize(response);
        body.Should().HaveCount(4);
    }

    [Fact]
    public async Task GetStats_PositiveOffsetTimeZone_LateUtcSundayMessage_CountsInFollowingLocalWeek_NotUtcWeek()
    {
        // Pacific/Kiritimati is UTC+14 with no DST — the largest, simplest-to-reason-about
        // positive offset. A message sent late Sunday UTC is already Monday afternoon there, so a
        // correct implementation buckets it into the FOLLOWING ISO week versus a UTC-anchored
        // (date_trunc-style) implementation, which would bucket it into the week ending that
        // Sunday. This is the regression the "bucket in memory in the caller's time zone, never
        // via SQL date_trunc" rule in the issue exists to prevent.
        var (http, trainerId) = await SetupTrainerAsync();
        var (clientUserId, clientPublicId) = await SetupLinkedClientAsync(trainerId);
        var conversationId = await SeedConversationAsync(trainerId, clientUserId);
        await SetTrainerTimeZoneAsync(trainerId, "Pacific/Kiritimati");

        var nowUtc = DateTime.UtcNow;
        var lastSunday = nowUtc.Date.AddDays(-(int)nowUtc.DayOfWeek);
        var sundayLateUtc = lastSunday.AddHours(22);
        if (sundayLateUtc >= nowUtc)
        {
            sundayLateUtc = sundayLateUtc.AddDays(-7);
        }

        await SeedMessageAsync(conversationId, trainerId, sundayLateUtc);

        var kiribatiTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Pacific/Kiritimati");
        var localTimestamp = TimeZoneInfo.ConvertTimeFromUtc(sundayLateUtc, kiribatiTimeZone);
        var expectedWeekStart = IsoWeekMonday(DateOnly.FromDateTime(localTimestamp));
        var wrongUtcWeekStart = IsoWeekMonday(DateOnly.FromDateTime(sundayLateUtc));

        expectedWeekStart.Should().NotBe(
            wrongUtcWeekStart, "the local and UTC weeks must genuinely differ for this test to prove anything");

        var response = await http.GetAsync(
            $"/trainer/clients/{clientPublicId}/message-stats?weeks=3", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Deserialize(response);

        var expectedRow = body.SingleOrDefault(w => w.WeekStart == expectedWeekStart);
        expectedRow.Should().NotBeNull("the message must bucket by the caller's local time zone, not UTC");
        expectedRow!.CoachMessages.Should().Be(1);

        var wrongRow = body.SingleOrDefault(w => w.WeekStart == wrongUtcWeekStart);
        if (wrongRow is not null)
        {
            wrongRow.CoachMessages.Should().Be(0, "a UTC-based bucketing bug would place the message here instead");
        }
    }

    // ── fixtures ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Mirrors the endpoint's own private <c>IsoWeekMonday</c> — returns the Monday of the ISO
    /// week containing <paramref name="date"/>. Duplicated here (not internals-shared) because
    /// this is the spec being tested against, not the implementation detail.
    /// </summary>
    private static DateOnly IsoWeekMonday(DateOnly date)
    {
        var dayIndex = date.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)date.DayOfWeek;
        return date.AddDays(-(dayIndex - 1));
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
    /// See <c>GetClientMessageStatsEndpointTests.SeedMessageAsync</c> for why the two-pass
    /// insert + <c>ExecuteUpdateAsync</c> is required to pin <c>DateCreated</c>.
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

    private async Task SetTrainerTimeZoneAsync(Guid trainerId, string ianaId)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        await db.Users
            .Where(u => u.Id == trainerId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.TimeZone, ianaId), ct);
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
