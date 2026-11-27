using FluentAssertions;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Services;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FormUp.Tests.Services;

/// <summary>
/// Shared-container Postgres fixture for <see cref="NotificationServiceTests"/> (#1104 Phase B
/// — see <see cref="SharedTestContainers"/>): creates its own database inside the one shared
/// server once for the collection instead of per fact.
/// </summary>
public class NotificationServiceContainerFixture(SharedTestContainers sharedContainers) : IAsyncLifetime
{
    public string ConnectionString { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        ConnectionString = await sharedContainers.CreatePostgresDatabaseAsync("notification_service");

        await using var db = BuildDbContext(ConnectionString);
        await db.Database.MigrateAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public static ApplicationDbContext BuildDbContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .ConfigureWarnings(w =>
                w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new ApplicationDbContext(options);
    }
}

[CollectionDefinition("NotificationService")]
public class NotificationServiceCollection : ICollectionFixture<NotificationServiceContainerFixture>;

/// <summary>
/// Testcontainers integration tests for <see cref="NotificationService"/> — verifies the
/// #788 fix: notifications are localized to the RECIPIENT's stored
/// <see cref="ApplicationUser.Language"/> at write time (title/body persisted already
/// translated), independent of whichever user/process triggered the notification, and
/// fall back to English when the recipient has no stored language.
/// </summary>
/// <remarks>
/// Isolated by a fresh <c>Guid.NewGuid()</c> recipient per fact (see <see cref="SeedUserAsync"/>)
/// — no reset needed despite the shared Postgres container (#1104).
/// </remarks>
[Collection("NotificationService")]
public class NotificationServiceTests(NotificationServiceContainerFixture containerFixture) : IAsyncLifetime
{
    private ApplicationDbContext _db =
        NotificationServiceContainerFixture.BuildDbContext(containerFixture.ConnectionString);
    private FakePushNotificationService _push = null!;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await _db.DisposeAsync();

    [Theory]
    // English is excluded: several English labels legitimately equal their machine key ("sex", "injuries").
    [InlineData("cs")]
    [InlineData("de")]
    public void LocalizeOnboardingFields_EveryFieldKey_HasATranslatedLabel(string language)
    {
        var keys = typeof(NotificationTemplates).GetFields()
            .Where(field => field.IsLiteral && field.Name.StartsWith("OnboardingField", StringComparison.Ordinal))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

        keys.Should().HaveCountGreaterThan(8);
        foreach (var key in keys)
        {
            NotificationTemplates.LocalizeOnboardingFields([key], language).Should().NotBe(key);
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Inserts a minimal user row via raw SQL with the given stored language
    /// (null supported, to exercise the fallback path).
    /// </summary>
    private async Task<Guid> SeedUserAsync(string? language)
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = Guid.NewGuid();

        await using var conn = new NpgsqlConnection(containerFixture.ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO users (
                id, user_name, normalized_user_name,
                email, normalized_email, email_confirmed,
                password_hash, security_stamp, concurrency_stamp,
                phone_number_confirmed, two_factor_enabled,
                lockout_enabled, access_failed_count,
                first_name, last_name, is_active, date_created,
                gdpr_consent, verification_emails_sent, time_zone, language
            ) VALUES (
                @id, @email, @emailUpper,
                @email, @emailUpper, true,
                '', gen_random_uuid()::text, gen_random_uuid()::text,
                false, false,
                true, 0,
                'Test', 'User', true, now(),
                true, 0, 'Europe/Prague', @language
            )";
        cmd.Parameters.AddWithValue("id", userId);
        cmd.Parameters.AddWithValue("email", $"{userId:N}@notification-service-test.com");
        cmd.Parameters.AddWithValue("emailUpper", $"{userId:N}@NOTIFICATION-SERVICE-TEST.COM");
        cmd.Parameters.AddWithValue("language", language is null ? (object)DBNull.Value : language);
        await cmd.ExecuteNonQueryAsync(ct);

        return userId;
    }

    private NotificationService BuildSut()
    {
        // Rebuild DbContext for a clean tracked-entity cache each call.
        _db.Dispose();
        _db = NotificationServiceContainerFixture.BuildDbContext(containerFixture.ConnectionString);
        _push = new FakePushNotificationService();

        return new NotificationService(_db, _push);
    }

    // ── tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// A recipient with a stored "cs" language gets a Czech title/body persisted, and the
    /// SAME Czech text is forwarded to the push service (#788 — the OS push banner must
    /// already be in the right language at send time).
    /// </summary>
    [Fact]
    public async Task CreateAsync_RecipientLanguageCs_PersistsAndPushesCzechText()
    {
        var ct = TestContext.Current.CancellationToken;
        var recipientId = await SeedUserAsync("cs");

        var sut = BuildSut();
        await sut.CreateAsync(
            recipientId,
            NotificationType.ClientRequestAccepted,
            new Dictionary<string, string> { ["clientName"] = "Petra Nováková" },
            ct: ct);

        var stored = await _db.Notifications
            .AsNoTracking()
            .SingleAsync(n => n.RecipientUserId == recipientId, ct);

        stored.Title.Should().Be("Pozvánka přijata");
        stored.Body.Should().Be("Petra Nováková přijal(a) vaši pozvánku.");

        _push.Calls.Should().ContainSingle(c =>
            c.UserId == recipientId &&
            c.Title == "Pozvánka přijata" &&
            c.Body == "Petra Nováková přijal(a) vaši pozvánku.");
    }

    /// <summary>
    /// A recipient with a stored "de" language gets German text — proves the fix is not
    /// English-vs-Czech only but genuinely per-recipient-language.
    /// </summary>
    [Fact]
    public async Task CreateAsync_RecipientLanguageDe_PersistsGermanText()
    {
        var ct = TestContext.Current.CancellationToken;
        var recipientId = await SeedUserAsync("de");

        var sut = BuildSut();
        await sut.CreateAsync(
            recipientId,
            NotificationType.ClientRequestAccepted,
            new Dictionary<string, string> { ["clientName"] = "Petra" },
            ct: ct);

        var stored = await _db.Notifications
            .AsNoTracking()
            .SingleAsync(n => n.RecipientUserId == recipientId, ct);

        stored.Title.Should().Be("Einladung angenommen");
        stored.Body.Should().Be("Petra hat Ihre Einladung angenommen.");
    }

    /// <summary>
    /// A recipient with NO stored language (null — e.g. never made an authenticated
    /// request since the #788 migration landed) falls back to English, per the
    /// orchestrator-approved default.
    /// </summary>
    [Fact]
    public async Task CreateAsync_RecipientLanguageNull_FallsBackToEnglish()
    {
        var ct = TestContext.Current.CancellationToken;
        var recipientId = await SeedUserAsync(language: null);

        var sut = BuildSut();
        await sut.CreateAsync(
            recipientId,
            NotificationType.ClientRequestAccepted,
            new Dictionary<string, string> { ["clientName"] = "Petra" },
            ct: ct);

        var stored = await _db.Notifications
            .AsNoTracking()
            .SingleAsync(n => n.RecipientUserId == recipientId, ct);

        stored.Title.Should().Be("Invitation accepted");
        stored.Body.Should().Be("Petra accepted your invitation.");
    }

    /// <summary>
    /// A NotificationType with multiple wordings (PlanPublished) resolves the correct
    /// variant's copy, not the default — proves the variant discriminator works and that
    /// distinct scenarios sharing one NotificationType don't collide.
    /// </summary>
    [Fact]
    public async Task CreateAsync_WithVariant_ResolvesVariantSpecificCopy()
    {
        var ct = TestContext.Current.CancellationToken;
        var recipientId = await SeedUserAsync("en");

        var sut = BuildSut();
        await sut.CreateAsync(
            recipientId,
            NotificationType.PlanPublished,
            new Dictionary<string, string> { ["weekNumber"] = "3" },
            variant: NotificationTemplates.PlanPublishedNutritionPublished,
            ct: ct);

        var stored = await _db.Notifications
            .AsNoTracking()
            .SingleAsync(n => n.RecipientUserId == recipientId, ct);

        stored.Title.Should().Be("Nutrition plan updated");
        stored.Body.Should().Be("Week 3 of your nutrition plan has been published.");
    }

    /// <summary>
    /// The interpolation parameters are persisted verbatim as JSON in
    /// <see cref="Notification.Data"/> so extra keys (e.g. a deep-link id not used by the
    /// current templates) still ride along.
    /// </summary>
    [Fact]
    public async Task CreateAsync_PersistsParametersAsJsonData()
    {
        var ct = TestContext.Current.CancellationToken;
        var recipientId = await SeedUserAsync("en");
        var inviteId = Guid.NewGuid();

        var sut = BuildSut();
        await sut.CreateAsync(
            recipientId,
            NotificationType.InvitationReceived,
            new Dictionary<string, string>
            {
                ["trainerName"] = "Coach Jana",
                ["inviteId"] = inviteId.ToString(),
            },
            ct: ct);

        var stored = await _db.Notifications
            .AsNoTracking()
            .SingleAsync(n => n.RecipientUserId == recipientId, ct);

        stored.Data.Should().NotBeNull();
        stored.Data.Should().Contain("Coach Jana");
        stored.Data.Should().Contain(inviteId.ToString());
    }

    /// <summary>
    /// Saves a join-request preference row for the user through its own context.
    /// </summary>
    private async Task SeedJoinRequestPreferenceAsync(Guid userId, bool pushEnabled)
    {
        await using var seedDb = NotificationServiceContainerFixture.BuildDbContext(containerFixture.ConnectionString);
        seedDb.NotificationPreferences.Add(new NotificationPreference
        {
            UserId = userId,
            Event = NotificationEvent.JoinRequest,
            EmailEnabled = true,
            PushEnabled = pushEnabled,
        });
        await seedDb.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static readonly Dictionary<string, string> JoinRequestParameters = new() { ["clientName"] = "Petra" };

    /// <summary>
    /// A join request with push turned off still writes the in-app row but sends no push.
    /// </summary>
    [Fact]
    public async Task CreateAsync_JoinRequestPushDisabled_WritesInAppRowAndSkipsPush()
    {
        var ct = TestContext.Current.CancellationToken;
        var recipientId = await SeedUserAsync("en");
        await SeedJoinRequestPreferenceAsync(recipientId, pushEnabled: false);

        var sut = BuildSut();
        await sut.CreateAsync(recipientId, NotificationType.ClientRequestReceived, JoinRequestParameters, ct: ct);

        var stored = await _db.Notifications.AsNoTracking().CountAsync(n => n.RecipientUserId == recipientId, ct);

        stored.Should().Be(1);
        _push.Calls.Should().BeEmpty();
    }

    /// <summary>
    /// A join request with push turned on sends the push.
    /// </summary>
    [Fact]
    public async Task CreateAsync_JoinRequestPushEnabled_SendsPush()
    {
        var ct = TestContext.Current.CancellationToken;
        var recipientId = await SeedUserAsync("en");
        await SeedJoinRequestPreferenceAsync(recipientId, pushEnabled: true);

        var sut = BuildSut();
        await sut.CreateAsync(recipientId, NotificationType.ClientRequestReceived, JoinRequestParameters, ct: ct);

        _push.Calls.Should().ContainSingle(c => c.UserId == recipientId);
    }

    /// <summary>
    /// The push data carries the notification's own data (inviteId) next to the type, so a tapped
    /// push can deep-link to the invite.
    /// </summary>
    [Fact]
    public async Task CreateAsync_InvitationReceived_PushDataCarriesInviteIdAndType()
    {
        var ct = TestContext.Current.CancellationToken;
        var recipientId = await SeedUserAsync("en");
        var inviteId = Guid.NewGuid().ToString();

        var sut = BuildSut();
        await sut.CreateAsync(
            recipientId,
            NotificationType.InvitationReceived,
            new Dictionary<string, string> { ["trainerName"] = "Coach Carl", ["inviteId"] = inviteId },
            ct: ct);

        var data = _push.Calls.Should().ContainSingle().Subject.Data;
        var json = System.Text.Json.JsonSerializer.Serialize(data);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        document.RootElement.GetProperty("inviteId").GetString().Should().Be(inviteId);
        document.RootElement.GetProperty("type").GetString().Should().Be(nameof(NotificationType.InvitationReceived));
    }

    /// <summary>
    /// A notification without parameters still pushes its type.
    /// </summary>
    [Fact]
    public async Task CreateAsync_NoParameters_PushDataStillHasType()
    {
        var ct = TestContext.Current.CancellationToken;
        var recipientId = await SeedUserAsync("en");

        var sut = BuildSut();
        await sut.CreateAsync(recipientId, NotificationType.ClientRequestAccepted, null, ct: ct);

        var json = System.Text.Json.JsonSerializer.Serialize(_push.Calls.Should().ContainSingle().Subject.Data);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        document.RootElement.GetProperty("type").GetString().Should().Be(nameof(NotificationType.ClientRequestAccepted));
    }

    /// <summary>
    /// A join request for a user with no saved row falls back to the default (push on).
    /// </summary>
    [Fact]
    public async Task CreateAsync_JoinRequestNoSavedRow_UsesDefaultAndSendsPush()
    {
        var ct = TestContext.Current.CancellationToken;
        var recipientId = await SeedUserAsync("en");

        var sut = BuildSut();
        await sut.CreateAsync(recipientId, NotificationType.ClientRequestReceived, JoinRequestParameters, ct: ct);

        _push.Calls.Should().ContainSingle(c => c.UserId == recipientId);
    }

    /// <summary>
    /// The photos and workout notices write their in-app row, but the push switch decides
    /// whether a device is pinged.
    /// </summary>
    [Theory]
    [InlineData(NotificationType.ProgressPhotosSubmitted, NotificationEvent.ProgressPhotosSubmitted)]
    [InlineData(NotificationType.WorkoutFinished, NotificationEvent.WorkoutFinished)]
    public async Task CreateAsync_CoachEventPushDisabled_WritesInAppRowAndSkipsPush(
        NotificationType type, NotificationEvent preferenceEvent)
    {
        var ct = TestContext.Current.CancellationToken;
        var recipientId = await SeedUserAsync("en");
        await using (var seedDb = NotificationServiceContainerFixture.BuildDbContext(containerFixture.ConnectionString))
        {
            seedDb.NotificationPreferences.Add(new NotificationPreference
            {
                UserId = recipientId,
                Event = preferenceEvent,
                EmailEnabled = true,
                PushEnabled = false,
            });
            await seedDb.SaveChangesAsync(ct);
        }

        var parameters = new Dictionary<string, string> { ["clientName"] = "Petra", ["sessionName"] = "Push" };
        await BuildSut().CreateAsync(recipientId, type, parameters, ct: ct);

        (await _db.Notifications.AsNoTracking().CountAsync(n => n.RecipientUserId == recipientId, ct)).Should().Be(1);
        _push.Calls.Should().BeEmpty();
    }

    /// <summary>
    /// Push-only delivery localizes the push to the recipient's language and writes no in-app row.
    /// </summary>
    [Fact]
    public async Task SendPushOnlyAsync_CzechRecipient_PushesLocalizedTitleAndWritesNoRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var recipientId = await SeedUserAsync("cs");
        var conversationId = Guid.NewGuid().ToString();

        await BuildSut().SendPushOnlyAsync(
            recipientId,
            NotificationType.NewMessage,
            new Dictionary<string, string>
            {
                ["senderName"] = "Petra",
                ["conversationId"] = conversationId,
                ["messageText"] = "Ahoj {senderName}",
            },
            ct: ct);

        var push = _push.Calls.Should().ContainSingle(c => c.UserId == recipientId).Subject;
        push.Title.Should().Be("Petra");
        push.Body.Should().Be("Ahoj {senderName}");
        (await _db.Notifications.AsNoTracking().CountAsync(n => n.RecipientUserId == recipientId, ct)).Should().Be(0);
    }

    /// <summary>
    /// Push-only delivery honours the recipient's push switch and still writes no row.
    /// </summary>
    [Fact]
    public async Task SendPushOnlyAsync_NewMessagePushDisabled_SkipsPushAndWritesNoRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var recipientId = await SeedUserAsync("en");
        await using (var seedDb = NotificationServiceContainerFixture.BuildDbContext(containerFixture.ConnectionString))
        {
            seedDb.NotificationPreferences.Add(new NotificationPreference
            {
                UserId = recipientId,
                Event = NotificationEvent.NewMessage,
                EmailEnabled = true,
                PushEnabled = false,
            });
            await seedDb.SaveChangesAsync(ct);
        }

        await BuildSut().SendPushOnlyAsync(
            recipientId,
            NotificationType.NewMessage,
            new Dictionary<string, string> { ["senderName"] = "Petra", ["messageText"] = "Hi" },
            ct: ct);

        _push.Calls.Should().BeEmpty();
        (await _db.Notifications.AsNoTracking().CountAsync(n => n.RecipientUserId == recipientId, ct)).Should().Be(0);
    }

    /// <summary>
    /// Without a saved row the default decides: workout finished is off by default, progress photos on.
    /// </summary>
    [Theory]
    [InlineData(NotificationType.WorkoutFinished, false)]
    [InlineData(NotificationType.ProgressPhotosSubmitted, true)]
    public async Task CreateAsync_CoachEventNoSavedRow_FollowsTheDefaultPushChoice(NotificationType type, bool expectPush)
    {
        var ct = TestContext.Current.CancellationToken;
        var recipientId = await SeedUserAsync("en");

        var parameters = new Dictionary<string, string> { ["clientName"] = "Petra", ["sessionName"] = "Push" };
        await BuildSut().CreateAsync(recipientId, type, parameters, ct: ct);

        _push.Calls.Any(c => c.UserId == recipientId).Should().Be(expectPush);
    }
}
