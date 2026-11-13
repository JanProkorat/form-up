using System.Globalization;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Services;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Infrastructure.Services;

/// <summary>
/// Integration tests for <see cref="PhotoDiaryReminderScheduler"/> using a virtual clock.
/// Uses Testcontainers PostgreSQL + MongoDB (Docker required).
/// </summary>
[Collection(TestCollection.Name)]
public class PhotoDiaryReminderSchedulerTests(FitnessApiFactory factory)
{
    // ── Pinned dates: one in summer time, one in winter time ───────────────────
    // Both are past dates, so a request's end date never expires under them, and
    // neither is within a week of a DST switch in Europe/Prague or America/New_York.

    private const string SummerDate = "2026-07-15";
    private const string WinterDate = "2026-01-14";
    private const string PragueZone = "Europe/Prague";
    private const string NewYorkZone = "America/New_York";

    private static DateOnly Pinned(string isoDate) =>
        DateOnly.ParseExact(isoDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Converts a wall-clock time in <paramref name="zoneId"/> on <paramref name="date"/> to a UTC instant.</summary>
    private static DateTime LocalToUtc(string zoneId, DateOnly date, int hour, int minute = 0) =>
        TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(date.ToDateTime(new TimeOnly(hour, minute)), DateTimeKind.Unspecified),
            TimeZoneInfo.FindSystemTimeZoneById(zoneId));

    // ── Helper: unique emails ──────────────────────────────────────────────────

    private static string UniqueEmail(string tag = "sched") =>
        $"{Guid.NewGuid():N}@{tag}-reminder-test.com";

    // ── Setup: register nutritionist + client, link them, insert diary request ─

    /// <summary>
    /// Creates a nutritionist and a client, links them, and inserts a workflow-mode
    /// PhotoDiaryRequest with the given status and acceptedAt.
    /// Returns the nutritionist UserId, client UserId, and the diary request Id.
    /// </summary>
    private async Task<(Guid ProfessionalId, Guid ClientUserId, Guid RequestId, long LinkId)>
        SetupWorkflowRequestAsync(
            PhotoDiaryStatus status = PhotoDiaryStatus.Accepted,
            DateTimeOffset? acceptedAt = null,
            int durationDays = 7,
            string clientTimeZone = "Europe/Prague",
            DateTimeOffset? createdAt = null)
    {
        var profEmail = UniqueEmail("prof");
        var clientEmail = UniqueEmail("client");

        var profHttp = factory.CreateClient();
        var clientHttp = factory.CreateClient();

        await TestHelpers.RegisterAsync(profHttp, profEmail, "TestPass1!", "Nutritionist", "Reminder", "Nutritionist");
        await TestHelpers.RegisterAsync(clientHttp, clientEmail, "TestPass1!", "Client", "Reminder", "Client");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var profUser = await db.Users.FirstAsync(u => u.Email == profEmail, TestContext.Current.CancellationToken);
        var clientUser = await db.Users.FirstAsync(u => u.Email == clientEmail, TestContext.Current.CancellationToken);

        // Set client timezone
        clientUser.TimeZone = clientTimeZone;

        var profProfile = await db.ProfessionalProfiles
            .FirstAsync(p => p.UserId == profUser.Id, TestContext.Current.CancellationToken);
        var clientProfile = await db.ClientProfiles
            .FirstAsync(p => p.UserId == clientUser.Id, TestContext.Current.CancellationToken);

        // Link client to nutritionist
        var link = new ClientProfessionalLink
        {
            ClientProfileId = clientProfile.Id,
            ProfessionalProfileId = profProfile.Id,
            ProfessionalRole = UserRole.Nutritionist,
            ConnectionState = ConnectionState.Connected,
            CanViewNutritionPlans = true,
            CanViewTrainingPlans = false,
            PublicId = Guid.NewGuid(),
            DateCreated = DateTime.UtcNow,
        };
        db.ClientProfessionalLinks.Add(link);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Insert the diary request
        var now = DateTimeOffset.UtcNow;
        var resolvedAcceptedAt = acceptedAt ?? now.AddDays(-1);
        // A request is created before it is accepted; the end date counts from creation.
        var resolvedCreatedAt = createdAt ?? resolvedAcceptedAt;

        var request = new PhotoDiaryRequest
        {
            Id = Guid.NewGuid(),
            ProfessionalId = profUser.Id,
            LinkId = link.Id,
            Mode = PhotoDiaryMode.Workflow,
            Status = status,
            DurationDays = durationDays,
            AcceptedAt = status == PhotoDiaryStatus.Cancelled ? null : resolvedAcceptedAt,
            CompletedAt = status == PhotoDiaryStatus.Completed ? now : null,
            DismissReason = status == PhotoDiaryStatus.Dismissed ? "test" : null,
            CreatedAt = resolvedCreatedAt,
            UpdatedAt = now,
        };

        db.PhotoDiaryRequests.Add(request);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return (profUser.Id, clientUser.Id, request.Id, link.Id);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SummerDate)]
    [InlineData(WinterDate)]
    public async Task Tick_BeforeNoon_DoesNotFire(string pinnedDate)
    {
        // Seed the cursor at 12:30 Prague local (AFTER noon) so noon is NOT in the next window.
        // The UTC instants come from the Prague zone rules, so they hold in CEST and CET.
        var date = Pinned(pinnedDate);
        var afterNoonPrague = LocalToUtc(PragueZone, date, 12, 30);

        var (_, clientUserId, requestId, _) = await SetupWorkflowRequestAsync(
            acceptedAt: new DateTimeOffset(LocalToUtc(PragueZone, date, 12).AddDays(-1)),
            clientTimeZone: PragueZone);

        var scheduler = factory.Services.GetRequiredService<PhotoDiaryReminderScheduler>();
        scheduler.ResetCursor();

        scheduler.SetLastTickAt(afterNoonPrague);

        // Tick one hour later: window is (12:30, 13:30] Prague local. Noon is NOT in the window.
        scheduler.OverrideNow = afterNoonPrague.AddHours(1);
        await scheduler.TickAsync(scheduler.OverrideNow.Value, TestContext.Current.CancellationToken);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var reminderLogs = await db.PhotoDiaryReminderLogs
            .Where(l => l.DiaryRequestId == requestId)
            .ToListAsync(TestContext.Current.CancellationToken);

        reminderLogs.Should().BeEmpty("noon already passed before the tick window — reminder must not fire");
    }

    [Theory]
    [InlineData(SummerDate)]
    [InlineData(WinterDate)]
    public async Task Tick_AtNoon_Prague_Fires(string pinnedDate)
    {
        // Window (09:00, 12:30] Prague local contains noon → FIRES, in CEST (10:00 UTC)
        // and in CET (11:00 UTC) alike.
        var date = Pinned(pinnedDate);
        var beforeNoonPrague = LocalToUtc(PragueZone, date, 9);
        var afterNoonPrague = LocalToUtc(PragueZone, date, 12, 30);

        var (_, clientUserId, requestId, _) = await SetupWorkflowRequestAsync(
            acceptedAt: new DateTimeOffset(LocalToUtc(PragueZone, date, 12).AddDays(-1)),
            clientTimeZone: PragueZone);

        var notifier = factory.Services.GetRequiredService<FakeRealtimeNotifier>();
        notifier.Reset();

        var scheduler = factory.Services.GetRequiredService<PhotoDiaryReminderScheduler>();
        scheduler.ResetCursor();

        scheduler.SetLastTickAt(beforeNoonPrague);

        scheduler.OverrideNow = afterNoonPrague;
        await scheduler.TickAsync(scheduler.OverrideNow.Value, TestContext.Current.CancellationToken);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var reminderLogs = await db.PhotoDiaryReminderLogs
            .Where(l => l.DiaryRequestId == requestId)
            .ToListAsync(TestContext.Current.CancellationToken);

        reminderLogs.Should().HaveCount(1, "noon crossed in this tick window → should fire once");

        var notification = await db.Notifications
            .Where(n => n.RecipientUserId == clientUserId && n.Type == NotificationType.PhotoDiaryReminder)
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);

        notification.Should().NotBeNull("in-app notification must be created");

        var signalRCalls = notifier.Calls
            .Where(c => c.UserId == clientUserId && c.EventType == "newnotification")
            .ToList();

        signalRCalls.Should().HaveCount(1, "scheduler must broadcast exactly one newnotification to the client");
    }

    /// <summary>
    /// The scheduler has no HTTP request / Accept-Language to go on — it must localize
    /// using the client's persisted <see cref="ApplicationUser.Language"/> column
    /// (#788). Proves the scheduler now routes through
    /// <see cref="INotificationService.CreateAsync"/> instead of constructing the
    /// Notification entity directly with hardcoded English text.
    /// </summary>
    [Fact]
    public async Task Tick_ClientLanguageDe_CreatesLocalizedNotification()
    {
        var date = Pinned(SummerDate);
        var beforeNoonPrague = LocalToUtc(PragueZone, date, 9);
        var afterNoonPrague = LocalToUtc(PragueZone, date, 12, 30);

        var (_, clientUserId, requestId, _) = await SetupWorkflowRequestAsync(
            acceptedAt: new DateTimeOffset(LocalToUtc(PragueZone, date, 12).AddDays(-1)),
            clientTimeZone: PragueZone);

        using (var scope = factory.Services.CreateScope())
        {
            var setupDb = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var clientUser = await setupDb.Users.FirstAsync(
                u => u.Id == clientUserId, TestContext.Current.CancellationToken);
            clientUser.Language = "de";
            await setupDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var scheduler = factory.Services.GetRequiredService<PhotoDiaryReminderScheduler>();
        scheduler.ResetCursor();
        scheduler.SetLastTickAt(beforeNoonPrague);
        scheduler.OverrideNow = afterNoonPrague;
        await scheduler.TickAsync(scheduler.OverrideNow.Value, TestContext.Current.CancellationToken);

        using var db = factory.Services.CreateScope().ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var notification = await db.Notifications
            .Where(n => n.RecipientUserId == clientUserId && n.Type == NotificationType.PhotoDiaryReminder)
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);

        notification.Should().NotBeNull();
        notification!.Title.Should().Be("Vergessen Sie Ihr Tagebuchfoto nicht");
        notification.Body.Should().Contain("Ernährungsberater");
    }

    [Fact]
    public async Task Tick_PhotoAlreadyUploadedToday_DoesNotFire()
    {
        // If the client already uploaded a photo for the diary request today, no reminder.
        // Strategy: use real UTC time so ApplyTimestamps stamps the photo with DateCreated = today,
        // then use real UTC noon as the virtual clock.  UTC timezone = no DST offset.
        var utcNow = DateTime.UtcNow;

        // AcceptedAt = yesterday so the request is inside its 7-day window.
        var (_, clientUserId, requestId, _) = await SetupWorkflowRequestAsync(
            acceptedAt: new DateTimeOffset(utcNow.AddDays(-1)),
            clientTimeZone: "UTC");

        using var setupScope = factory.Services.CreateScope();
        var setupDb = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var clientProfile = await setupDb.ClientProfiles
            .FirstAsync(p => p.UserId == clientUserId, TestContext.Current.CancellationToken);

        // ApplyTimestamps will set DateCreated = real UtcNow — that's "today" in UTC.
        var photo = new PlanPhoto
        {
            ClientProfileId = clientProfile.Id,
            DiaryRequestId = requestId,
            BlobUrl = "https://example.com/photo.jpg",
            Category = PlanPhotoCategory.Food,
            TakenAt = utcNow,
            UploadedByUserId = clientUserId,
        };
        setupDb.PlanPhotos.Add(photo);
        await setupDb.SaveChangesAsync(TestContext.Current.CancellationToken);

        var scheduler = factory.Services.GetRequiredService<PhotoDiaryReminderScheduler>();
        scheduler.ResetCursor();

        // Virtual noon UTC = today's noon UTC.
        var noonUtcToday = utcNow.Date.AddHours(12);
        // Seed cursor to one hour before noon so noon is within the second tick window.
        var seedTime = noonUtcToday.AddHours(-1);

        scheduler.OverrideNow = seedTime;
        await scheduler.TickAsync(scheduler.OverrideNow.Value, TestContext.Current.CancellationToken);

        // Tick at noon + 30 min: noon IS in window, but photo already uploaded → no reminder.
        scheduler.OverrideNow = noonUtcToday.AddMinutes(30);
        await scheduler.TickAsync(scheduler.OverrideNow.Value, TestContext.Current.CancellationToken);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var reminderLogs = await db.PhotoDiaryReminderLogs
            .Where(l => l.DiaryRequestId == requestId)
            .ToListAsync(TestContext.Current.CancellationToken);

        reminderLogs.Should().BeEmpty("client already uploaded a photo today — reminder must be skipped");
    }

    [Fact]
    public async Task Tick_StatusNotAcceptedOrInProgress_DoesNotFire()
    {
        // Completed requests are excluded by the candidate query.
        var date = Pinned(SummerDate);

        var (_, _, requestId, _) = await SetupWorkflowRequestAsync(
            status: PhotoDiaryStatus.Completed,
            acceptedAt: new DateTimeOffset(LocalToUtc(PragueZone, date, 12).AddDays(-1)),
            clientTimeZone: PragueZone);

        var scheduler = factory.Services.GetRequiredService<PhotoDiaryReminderScheduler>();
        scheduler.ResetCursor();

        // Window (09:00, 12:30] Prague local contains noon, but the request is Completed → skip.
        scheduler.SetLastTickAt(LocalToUtc(PragueZone, date, 9));
        scheduler.OverrideNow = LocalToUtc(PragueZone, date, 12, 30);
        await scheduler.TickAsync(scheduler.OverrideNow.Value, TestContext.Current.CancellationToken);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var reminderLogs = await db.PhotoDiaryReminderLogs
            .Where(l => l.DiaryRequestId == requestId)
            .ToListAsync(TestContext.Current.CancellationToken);

        reminderLogs.Should().BeEmpty("Completed requests must not fire reminders");
    }

    [Fact]
    public async Task Tick_ModeBulk_DoesNotFire()
    {
        // Bulk-mode requests must not trigger the workflow reminder.
        var profEmail = UniqueEmail("bulk-prof");
        var clientEmail = UniqueEmail("bulk-client");

        var profHttp = factory.CreateClient();
        var clientHttp = factory.CreateClient();
        await TestHelpers.RegisterAsync(profHttp, profEmail, "TestPass1!", "N", "Bulk", "Nutritionist");
        await TestHelpers.RegisterAsync(clientHttp, clientEmail, "TestPass1!", "C", "Bulk", "Client");

        using var setupScope = factory.Services.CreateScope();
        var setupDb = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var profUser = await setupDb.Users.FirstAsync(u => u.Email == profEmail, TestContext.Current.CancellationToken);
        var clientUser = await setupDb.Users.FirstAsync(u => u.Email == clientEmail, TestContext.Current.CancellationToken);
        var profProfile = await setupDb.ProfessionalProfiles.FirstAsync(p => p.UserId == profUser.Id, TestContext.Current.CancellationToken);
        var clientProfile = await setupDb.ClientProfiles.FirstAsync(p => p.UserId == clientUser.Id, TestContext.Current.CancellationToken);

        var link = new ClientProfessionalLink
        {
            ClientProfileId = clientProfile.Id,
            ProfessionalProfileId = profProfile.Id,
            ProfessionalRole = UserRole.Nutritionist,
            ConnectionState = ConnectionState.Connected,
            CanViewNutritionPlans = true,
            CanViewTrainingPlans = false,
            PublicId = Guid.NewGuid(),
            DateCreated = DateTime.UtcNow,
        };
        setupDb.ClientProfessionalLinks.Add(link);
        await setupDb.SaveChangesAsync(TestContext.Current.CancellationToken);

        var date = Pinned(SummerDate);
        var pinnedNoon = new DateTimeOffset(LocalToUtc(PragueZone, date, 12));
        var bulkRequest = new PhotoDiaryRequest
        {
            Id = Guid.NewGuid(),
            ProfessionalId = profUser.Id,
            LinkId = link.Id,
            Mode = PhotoDiaryMode.Bulk,  // ← Bulk, not Workflow
            Status = PhotoDiaryStatus.Accepted,
            DurationDays = 7,
            AcceptedAt = pinnedNoon.AddDays(-1),
            CreatedAt = pinnedNoon.AddDays(-1),
            UpdatedAt = pinnedNoon,
        };
        setupDb.PhotoDiaryRequests.Add(bulkRequest);
        await setupDb.SaveChangesAsync(TestContext.Current.CancellationToken);

        var scheduler = factory.Services.GetRequiredService<PhotoDiaryReminderScheduler>();
        scheduler.ResetCursor();

        // Window (09:00, 12:30] Prague local contains noon, but Bulk → no reminder.
        scheduler.SetLastTickAt(LocalToUtc(PragueZone, date, 9));
        scheduler.OverrideNow = LocalToUtc(PragueZone, date, 12, 30);
        await scheduler.TickAsync(scheduler.OverrideNow.Value, TestContext.Current.CancellationToken);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var reminderLogs = await verifyDb.PhotoDiaryReminderLogs
            .Where(l => l.DiaryRequestId == bulkRequest.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        reminderLogs.Should().BeEmpty("Bulk-mode requests must not fire workflow reminders");
    }

    [Theory]
    [InlineData(SummerDate)]
    [InlineData(WinterDate)]
    public async Task Tick_Idempotent_TwoTicksSameDay_OnlyOneReminder(string pinnedDate)
    {
        // Simulates a scheduler restart mid-tick: the first run inserts the log but
        // _lastTickAt was not persisted. On restart we force the cursor back to before noon
        // so the scheduler tries to fire again — the unique constraint must catch it.
        var date = Pinned(pinnedDate);
        var beforeNoon = LocalToUtc(PragueZone, date, 9);
        var afterNoon = LocalToUtc(PragueZone, date, 12, 30);

        var (_, clientUserId, requestId, _) = await SetupWorkflowRequestAsync(
            acceptedAt: new DateTimeOffset(LocalToUtc(PragueZone, date, 12).AddDays(-1)),
            clientTimeZone: PragueZone);

        var scheduler = factory.Services.GetRequiredService<PhotoDiaryReminderScheduler>();
        scheduler.ResetCursor();

        // First run: cursor 09:00 local, tick at 12:30 local → fires.
        scheduler.SetLastTickAt(beforeNoon);
        scheduler.OverrideNow = afterNoon;
        await scheduler.TickAsync(afterNoon, TestContext.Current.CancellationToken);

        // Simulate restart: force cursor back to before noon on the same day.
        scheduler.SetLastTickAt(beforeNoon);
        scheduler.OverrideNow = afterNoon.AddMinutes(30);  // 13:00 local — noon still in the window
        await scheduler.TickAsync(afterNoon.AddMinutes(30), TestContext.Current.CancellationToken);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var reminderLogs = await db.PhotoDiaryReminderLogs
            .Where(l => l.DiaryRequestId == requestId)
            .ToListAsync(TestContext.Current.CancellationToken);

        reminderLogs.Should().HaveCount(1, "unique index must prevent a second reminder on the same day");

        var notifications = await db.Notifications
            .Where(n => n.RecipientUserId == clientUserId && n.Type == NotificationType.PhotoDiaryReminder)
            .ToListAsync(TestContext.Current.CancellationToken);

        notifications.Should().HaveCount(1, "only one notification must be created despite the retry");
    }

    [Theory]
    [InlineData(SummerDate)]
    [InlineData(WinterDate)]
    public async Task Tick_NewYorkTimezone_FiresAtNoonEastern(string pinnedDate)
    {
        // Noon New York is 16:00 UTC in EDT and 17:00 UTC in EST. The window (11:00, 12:30]
        // New York local contains noon in both.
        var date = Pinned(pinnedDate);
        var virtualNoonNY = LocalToUtc(NewYorkZone, date, 12);

        var (_, clientUserId, requestId, _) = await SetupWorkflowRequestAsync(
            acceptedAt: new DateTimeOffset(virtualNoonNY.AddDays(-1)),
            clientTimeZone: NewYorkZone);

        var notifier = factory.Services.GetRequiredService<FakeRealtimeNotifier>();
        notifier.Reset();

        var scheduler = factory.Services.GetRequiredService<PhotoDiaryReminderScheduler>();
        scheduler.ResetCursor();

        // Seed cursor to 11:00 New York local (before noon).
        scheduler.SetLastTickAt(LocalToUtc(NewYorkZone, date, 11));

        // Tick at 12:30 New York local.
        scheduler.OverrideNow = LocalToUtc(NewYorkZone, date, 12, 30);
        await scheduler.TickAsync(scheduler.OverrideNow.Value, TestContext.Current.CancellationToken);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var reminderLogs = await db.PhotoDiaryReminderLogs
            .Where(l => l.DiaryRequestId == requestId)
            .ToListAsync(TestContext.Current.CancellationToken);

        reminderLogs.Should().HaveCount(1, "noon New York should trigger a reminder in both EDT and EST");
    }

    [Theory]
    [InlineData(SummerDate)]
    [InlineData(WinterDate)]
    public async Task Tick_NewYorkTimezone_BeforeNoonEastern_DoesNotFire(string pinnedDate)
    {
        // Seed the cursor at 12:30 New York local (AFTER noon) → noon is NOT in the next window.
        var date = Pinned(pinnedDate);
        var afterNoonNY = LocalToUtc(NewYorkZone, date, 12, 30);

        var (_, _, requestId, _) = await SetupWorkflowRequestAsync(
            acceptedAt: new DateTimeOffset(LocalToUtc(NewYorkZone, date, 12).AddDays(-1)),
            clientTimeZone: NewYorkZone);

        var scheduler = factory.Services.GetRequiredService<PhotoDiaryReminderScheduler>();
        scheduler.ResetCursor();

        scheduler.SetLastTickAt(afterNoonNY);

        // Tick one hour later: window is (12:30, 13:30] New York local. Noon is NOT in it.
        scheduler.OverrideNow = afterNoonNY.AddHours(1);
        await scheduler.TickAsync(scheduler.OverrideNow.Value, TestContext.Current.CancellationToken);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var reminderLogs = await db.PhotoDiaryReminderLogs
            .Where(l => l.DiaryRequestId == requestId)
            .ToListAsync(TestContext.Current.CancellationToken);

        reminderLogs.Should().BeEmpty("noon already passed before this tick window — no reminder");
    }

    [Fact]
    public async Task Tick_AutoFinalizeFromTheEndDate()
    {
        // A request created and accepted 9 days ago (DurationDays=7) is past its end date
        // (CreatedAt + 7 days) and should be auto-finalized.
        var acceptedAt = new DateTimeOffset(DateTime.UtcNow.AddDays(-9));

        var (professionalId, clientUserId, requestId, _) = await SetupWorkflowRequestAsync(
            acceptedAt: acceptedAt,
            durationDays: 7,
            clientTimeZone: "Europe/Prague");

        var notifier = factory.Services.GetRequiredService<FakeRealtimeNotifier>();
        notifier.Reset();

        var scheduler = factory.Services.GetRequiredService<PhotoDiaryReminderScheduler>();
        scheduler.ResetCursor();

        // Use real-time-relative noon UTC to avoid the DateCreated photo issue.
        var noonUtcToday = DateTime.UtcNow.Date.AddHours(12);
        var seedTime = noonUtcToday.AddHours(-1);

        scheduler.OverrideNow = seedTime;
        await scheduler.TickAsync(scheduler.OverrideNow.Value, TestContext.Current.CancellationToken);

        // Tick at noon+30min: the auto-finalize condition triggers (past the end date).
        scheduler.OverrideNow = noonUtcToday.AddMinutes(30);
        await scheduler.TickAsync(scheduler.OverrideNow.Value, TestContext.Current.CancellationToken);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var request = await db.PhotoDiaryRequests
            .FirstAsync(r => r.Id == requestId, TestContext.Current.CancellationToken);

        request.Status.Should().Be(PhotoDiaryStatus.Completed, "request should be auto-finalized");
        request.CompletedAt.Should().NotBeNull("CompletedAt must be set on auto-finalize");

        // Verify photoDiarySubmitted emitted to the professional.
        var submittedEvents = notifier.Calls
            .Where(c => c.UserId == professionalId && c.EventType == "photodiarysubmitted")
            .ToList();

        submittedEvents.Should().HaveCount(1, "photoDiarySubmitted must be emitted to the professional");
    }

    [Fact]
    public async Task Tick_DayIndexInPayload_CorrectValue()
    {
        // AcceptedAt = 2 days ago in Prague local time → today is day 3.
        var prague = TimeZoneInfo.FindSystemTimeZoneById(PragueZone);
        var date = Pinned(SummerDate);
        var virtualNow = LocalToUtc(PragueZone, date, 12, 30); // after noon Prague

        var virtualNowPrague = TimeZoneInfo.ConvertTimeFromUtc(virtualNow, prague);

        // AcceptedAt = 2 days ago local (at 09:00 local).
        var acceptedAtLocal = virtualNowPrague.Date.AddDays(-2).Add(TimeSpan.FromHours(9));
        var acceptedAtUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(acceptedAtLocal, DateTimeKind.Unspecified), prague);

        var (_, clientUserId, requestId, _) = await SetupWorkflowRequestAsync(
            acceptedAt: new DateTimeOffset(acceptedAtUtc),
            clientTimeZone: "Europe/Prague");

        var scheduler = factory.Services.GetRequiredService<PhotoDiaryReminderScheduler>();
        scheduler.ResetCursor();

        // Seed cursor to 09:00 Prague local (before noon).
        scheduler.SetLastTickAt(LocalToUtc(PragueZone, date, 9));

        // Tick at 12:30 Prague local → fires.
        scheduler.OverrideNow = virtualNow;
        await scheduler.TickAsync(scheduler.OverrideNow.Value, TestContext.Current.CancellationToken);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var notification = await db.Notifications
            .Where(n => n.RecipientUserId == clientUserId && n.Type == NotificationType.PhotoDiaryReminder)
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);

        notification.Should().NotBeNull("notification must be created");
        // #788: Data is now the JSON-serialized Dictionary<string,string> params passed to
        // INotificationService.CreateAsync, so dayIndex is a quoted string, not a bare
        // number, unlike the pre-#788 anonymous-object payload.
        notification!.Data.Should().Contain("\"dayIndex\":\"3\"",
            "day index must be 3 when AcceptedAt was 2 local days ago");
    }

    [Fact]
    public async Task Tick_PushFailure_DoesNotBlockNotificationPersistence()
    {
        // If the push service throws, the in-app notification must still be persisted
        // and no exception should bubble out.
        // Use UTC timezone + real-time based noon so AcceptedAt and window stay valid.
        var utcNow = DateTime.UtcNow;
        var noonUtcToday = utcNow.Date.AddHours(12);

        var (_, clientUserId, requestId, _) = await SetupWorkflowRequestAsync(
            acceptedAt: new DateTimeOffset(utcNow.AddDays(-1)),
            clientTimeZone: "UTC");

        var push = factory.Services.GetRequiredService<FakePushNotificationService>();
        push.SimulateThrowOnNextCall();

        var scheduler = factory.Services.GetRequiredService<PhotoDiaryReminderScheduler>();
        scheduler.ResetCursor();

        // Seed cursor to one hour before noon.
        scheduler.SetLastTickAt(noonUtcToday.AddHours(-1));

        var act = async () =>
        {
            // Tick at noon + 30 min: noon (12:00 UTC) is in window.
            scheduler.OverrideNow = noonUtcToday.AddMinutes(30);
            await scheduler.TickAsync(scheduler.OverrideNow.Value, TestContext.Current.CancellationToken);
        };

        await act.Should().NotThrowAsync("push failure must not propagate out of the scheduler");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var notification = await db.Notifications
            .Where(n => n.RecipientUserId == clientUserId && n.Type == NotificationType.PhotoDiaryReminder)
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);

        notification.Should().NotBeNull("in-app notification must still be persisted despite push failure");
    }

    /// <summary>
    /// Regression test for the EF Core change-tracker cascade (issue #278).
    ///
    /// When two candidates are processed in the same tick and the FIRST candidate's
    /// reminder-log insert violates the unique constraint (ix_photo_diary_reminder_logs_request_date,
    /// Postgres 23505), the SECOND candidate must still receive its reminder log,
    /// in-app notification, and push notification.
    ///
    /// Before the fix: the catch block called db.Remove(logEntry) on a never-persisted entity,
    /// leaving the shared DbContext's change tracker in a corrupted state.  The second candidate's
    /// SaveChanges would then fail with a cascade exception.
    ///
    /// After the fix: each candidate runs inside its own IServiceScope / IApplicationDbContext,
    /// so a failed SaveChanges disposes that scope and the next candidate starts clean.
    ///
    /// This test exercises the error_path from the design handoff:
    ///   "Scheduler tick: first candidate's reminder-log insert violates the unique constraint.
    ///    After the catch, the same DbContext is used for the second candidate — its SaveChanges
    ///    must succeed cleanly (no cascade from the first failure)."
    /// </summary>
    [Fact]
    public async Task Tick_UniqueViolationOnFirstCandidate_DoesNotCascadeToSecondCandidate()
    {
        // Arrange: two active diary requests, both in UTC so noon = 12:00 UTC.
        var utcNow = DateTime.UtcNow;
        var noonUtc = utcNow.Date.AddHours(12);
        var acceptedAt = new DateTimeOffset(utcNow.AddDays(-1));

        var (_, clientAId, requestAId, _) = await SetupWorkflowRequestAsync(
            acceptedAt: acceptedAt,
            clientTimeZone: "UTC");

        var (_, clientBId, requestBId, _) = await SetupWorkflowRequestAsync(
            acceptedAt: acceptedAt,
            clientTimeZone: "UTC");

        // Pre-seed a PhotoDiaryReminderLog row for candidate A's (DiaryRequestId, ClientLocalDate)
        // so its insert will hit the 23505 unique violation.
        var clientLocalDate = DateOnly.FromDateTime(utcNow.Date);
        using (var seedScope = factory.Services.CreateScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            seedDb.PhotoDiaryReminderLogs.Add(new PhotoDiaryReminderLog
            {
                DiaryRequestId = requestAId,
                ClientLocalDate = clientLocalDate,
                SentAt = noonUtc.AddHours(-1)   // pre-existing log for today
            });
            await seedDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var push = factory.Services.GetRequiredService<FakePushNotificationService>();
        push.Reset();

        var notifier = factory.Services.GetRequiredService<FakeRealtimeNotifier>();
        notifier.Reset();

        var scheduler = factory.Services.GetRequiredService<PhotoDiaryReminderScheduler>();
        scheduler.ResetCursor();

        // Seed cursor to one hour before noon so noon (12:00 UTC) is in the tick window.
        scheduler.SetLastTickAt(noonUtc.AddHours(-1));
        scheduler.OverrideNow = noonUtc.AddMinutes(30);

        // Act — one tick processes both candidates (A hits 23505, B must succeed).
        await scheduler.TickAsync(scheduler.OverrideNow.Value, TestContext.Current.CancellationToken);

        // Assert
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Candidate A: no new log row (the pre-seeded one remains, duplicate was rejected).
        var logsForA = await db.PhotoDiaryReminderLogs
            .Where(l => l.DiaryRequestId == requestAId && l.ClientLocalDate == clientLocalDate)
            .ToListAsync(TestContext.Current.CancellationToken);
        logsForA.Should().HaveCount(1,
            "candidate A had a pre-existing log row — the unique violation must be swallowed, " +
            "not crash the scheduler, and the existing row must not be deleted");

        // Candidate B: reminder log must be inserted (no cascade from A's failure).
        var logsForB = await db.PhotoDiaryReminderLogs
            .Where(l => l.DiaryRequestId == requestBId && l.ClientLocalDate == clientLocalDate)
            .ToListAsync(TestContext.Current.CancellationToken);
        logsForB.Should().HaveCount(1,
            "candidate B's reminder-log insert must succeed despite A's 23505 violation");

        // Candidate B: in-app notification must be created.
        var notificationForB = await db.Notifications
            .Where(n => n.RecipientUserId == clientBId && n.Type == NotificationType.PhotoDiaryReminder)
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        notificationForB.Should().NotBeNull(
            "candidate B's in-app notification must be persisted — it must not be affected by A's failure");

        // Candidate A: no in-app notification (reminder was skipped as duplicate).
        var notificationForA = await db.Notifications
            .Where(n => n.RecipientUserId == clientAId && n.Type == NotificationType.PhotoDiaryReminder)
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        notificationForA.Should().BeNull(
            "candidate A's notification must NOT be created — the reminder was a duplicate and was skipped");

        // Push must be invoked exactly once (for candidate B only).
        push.Calls.Should().HaveCount(1,
            "push must be sent for candidate B only — candidate A's reminder was a duplicate");
        push.Calls[0].UserId.Should().Be(clientBId,
            "the push must target candidate B's client");

        // SignalR broadcast must fire exactly once (for candidate B's newnotification).
        var newNotificationCalls = notifier.Calls
            .Where(c => c.EventType == "newnotification")
            .ToList();
        newNotificationCalls.Should().HaveCount(1,
            "newnotification must be broadcast once (for candidate B) — candidate A was skipped");
        newNotificationCalls[0].UserId.Should().Be(clientBId);
    }

    // ── Mode, end date and cancel (request created through the API) ───────────

    private async Task<(Actor Professional, Actor Client, Guid RequestId)> CreateRequestViaApiAsync(
        string mode, bool accept)
    {
        var professional = await TestActors.Nutritionist(factory).CreateAsync(TestContext.Current.CancellationToken);
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        await TestActors.Link(factory, professional, client)
            .AsRole(UserRole.Nutritionist)
            .CreateAsync(TestContext.Current.CancellationToken);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.FirstAsync(u => u.Id == client.UserId, TestContext.Current.CancellationToken);
            user.TimeZone = "UTC";
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var created = await professional.Http.PostAsJsonAsync(
            "/trainer/photo-diary-requests",
            new { ClientId = client.PublicId, Mode = mode, DurationDays = 7 },
            TestContext.Current.CancellationToken);
        created.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var requestId = (await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(
            TestContext.Current.CancellationToken)).GetProperty("id").GetGuid();

        if (accept)
        {
            var accepted = await client.Http.PostAsJsonAsync(
                $"/client/photo-diary-requests/{requestId}/accept", new { }, TestContext.Current.CancellationToken);
            accepted.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        }

        return (professional, client, requestId);
    }

    /// <summary>Ticks once with client noon (UTC) inside the window.</summary>
    private async Task TickAtClientNoonAsync()
    {
        var noonUtc = DateTime.UtcNow.Date.AddHours(12);

        var scheduler = factory.Services.GetRequiredService<PhotoDiaryReminderScheduler>();
        scheduler.ResetCursor();
        scheduler.SetLastTickAt(noonUtc.AddHours(-1));
        scheduler.OverrideNow = noonUtc.AddMinutes(30);
        await scheduler.TickAsync(scheduler.OverrideNow.Value, TestContext.Current.CancellationToken);
    }

    private async Task<int> ReminderCountAsync(Guid requestId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.PhotoDiaryReminderLogs.CountAsync(
            l => l.DiaryRequestId == requestId, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Tick_EveryDayRequestCreatedAndAcceptedViaApi_RemindsAtClientNoon()
    {
        var (_, client, requestId) = await CreateRequestViaApiAsync("Workflow", accept: true);

        await TickAtClientNoonAsync();

        (await ReminderCountAsync(requestId)).Should().Be(1);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.Notifications.AnyAsync(
            n => n.RecipientUserId == client.UserId && n.Type == NotificationType.PhotoDiaryReminder,
            TestContext.Current.CancellationToken)).Should().BeTrue();
    }

    [Fact]
    public async Task Tick_OnceRequestCreatedAndAcceptedViaApi_DoesNotRemind()
    {
        var (_, _, requestId) = await CreateRequestViaApiAsync("Bulk", accept: true);

        await TickAtClientNoonAsync();

        (await ReminderCountAsync(requestId)).Should().Be(0);
    }

    [Fact]
    public async Task Tick_CancelledViaApi_DoesNotRemind()
    {
        var (professional, _, requestId) = await CreateRequestViaApiAsync("Workflow", accept: true);

        var cancelled = await professional.Http.PostAsJsonAsync(
            $"/trainer/photo-diary-requests/{requestId}/cancel", new { }, TestContext.Current.CancellationToken);
        cancelled.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        await TickAtClientNoonAsync();

        (await ReminderCountAsync(requestId)).Should().Be(0);
    }

    [Fact]
    public async Task Tick_AtTheEndDate_DoesNotRemind_AndFinalizes()
    {
        var virtualNow = DateTime.UtcNow.Date.AddHours(12).AddMinutes(30);

        // Created exactly 7 days before the tick, so now == CreatedAt + DurationDays.
        var (_, _, requestId, _) = await SetupWorkflowRequestAsync(
            acceptedAt: new DateTimeOffset(virtualNow.AddDays(-2), TimeSpan.Zero),
            createdAt: new DateTimeOffset(virtualNow.AddDays(-7), TimeSpan.Zero),
            durationDays: 7,
            clientTimeZone: "UTC");

        await TickAtClientNoonAsync();

        (await ReminderCountAsync(requestId)).Should().Be(0);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var request = await db.PhotoDiaryRequests.FirstAsync(
            r => r.Id == requestId, TestContext.Current.CancellationToken);
        request.Status.Should().Be(PhotoDiaryStatus.Completed);
    }

    [Fact]
    public async Task Tick_JustBeforeTheEndDate_StillReminds_WithDayNumberFromAcceptance()
    {
        var virtualNow = DateTime.UtcNow.Date.AddHours(12).AddMinutes(30);

        // One minute short of the end date; accepted 2 days ago, so today is day 3.
        var (_, clientUserId, requestId, _) = await SetupWorkflowRequestAsync(
            acceptedAt: new DateTimeOffset(virtualNow.AddDays(-2), TimeSpan.Zero),
            createdAt: new DateTimeOffset(virtualNow.AddDays(-7).AddMinutes(1), TimeSpan.Zero),
            durationDays: 7,
            clientTimeZone: "UTC");

        await TickAtClientNoonAsync();

        (await ReminderCountAsync(requestId)).Should().Be(1);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var notification = await db.Notifications.FirstAsync(
            n => n.RecipientUserId == clientUserId && n.Type == NotificationType.PhotoDiaryReminder,
            TestContext.Current.CancellationToken);
        notification.Data.Should().Contain("\"dayIndex\":\"3\"");
    }
}
