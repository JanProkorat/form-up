using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Services;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FormUp.Tests.Services;

/// <summary>
/// The grouped progress-photo email: a client's burst of free-form photos becomes one email and one in-app notice
/// per coach, sent by <see cref="NotificationEmailGroupingScheduler"/>. Photos are inserted directly with explicit
/// upload times and the burst is opened by <see cref="ProgressPhotoBurst"/>, so nothing waits for the hosted loop.
/// </summary>
[Collection(TestCollection.Name)]
public class ProgressPhotoGroupingTests(FitnessApiFactory factory) : IAsyncLifetime
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static readonly DateTime Start = DateTime.UtcNow.AddHours(-3);

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        // The scheduler sweeps every due burst, so rows left by earlier tests are cleared first.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        await db.PendingNotificationEmails.ExecuteDeleteAsync(Ct);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private NotificationEmailGroupingScheduler Scheduler =>
        factory.Services.GetRequiredService<NotificationEmailGroupingScheduler>();

    private async Task<(Actor Coach, Actor Client, long LinkId)> SeedLinkAsync(Actor? existingClient = null)
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = existingClient ?? await TestActors.Client(factory).WithName("Petra", "Svobodova").CreateAsync(Ct);
        var linkId = await TestActors.Link(factory, coach, client).CreateAsync(Ct);

        return (coach, client, linkId);
    }

    private async Task EnableEmailAsync(Guid coachUserId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.NotificationPreferences.Add(new NotificationPreference
        {
            UserId = coachUserId,
            Event = NotificationEvent.ProgressPhotosSubmitted,
            EmailEnabled = true,
            PushEnabled = false,
        });
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>
    /// Inserts a photo that the given coaches can see and opens or extends their bursts the way the finalize endpoints do.
    /// </summary>
    private async Task UploadAsync(
        Actor client,
        TimeSpan offset,
        Guid[] viewerCoachUserIds,
        PlanPhotoCategory category = PlanPhotoCategory.FreeForm,
        Guid? diaryRequestId = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var photoBuilder = EntityBuilder.PlanPhoto
            .ForClient(client.ProfileId, client.UserId)
            .WithCategory(category)
            .CreatedAt(Start + offset);
        var photo = (diaryRequestId is { } requestId ? photoBuilder.AnsweringRequest(requestId) : photoBuilder).Build();

        db.PlanPhotos.Add(photo);

        foreach (var coachUserId in viewerCoachUserIds)
        {
            db.PlanPhotoViewers.Add(new PlanPhotoViewer { PlanPhoto = photo, ProfessionalUserId = coachUserId });
        }

        await db.SaveChangesAsync(Ct);

        // Saving stamps the creation time, so the upload time is set afterwards and mirrored on the tracked photo.
        var uploadedAt = Start + offset;
        await db.PlanPhotos.Where(p => p.Id == photo.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.DateCreated, uploadedAt), Ct);
        photo.DateCreated = uploadedAt;

        await ProgressPhotoBurst.QueueAsync(db, NullLogger.Instance, photo, client.PublicId, Ct);
    }

    private async Task<List<PendingNotificationEmail>> PendingAsync(Guid recipientUserId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.PendingNotificationEmails.AsNoTracking()
            .Where(p => p.RecipientUserId == recipientUserId)
            .ToListAsync(Ct);
    }

    private async Task<List<Notification>> NoticesAsync(Guid recipientUserId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.Notifications.AsNoTracking()
            .Where(n => n.RecipientUserId == recipientUserId && n.Type == NotificationType.ProgressPhotosSubmitted)
            .ToListAsync(Ct);
    }

    private async Task<IReadOnlyList<NotificationEmailWorkItem>> SentToAsync(Guid recipientUserId) =>
        await factory.Services.GetRequiredService<FakeEmailService>()
            .NotificationsForAsync(factory.Services.GetRequiredService<IBackgroundEmailQueue>(), recipientUserId);

    [Fact]
    public async Task RunDueNowAsync_BurstOfFreeFormPhotos_SendsOneEmailAndOneNoticeWithTheCountAndNoPhotoAddress()
    {
        var (coach, client, _) = await SeedLinkAsync();
        await EnableEmailAsync(coach.UserId);

        foreach (var minute in new[] { 0, 1, 2 })
        {
            await UploadAsync(client, TimeSpan.FromMinutes(minute), [coach.UserId]);
        }

        var burst = (await PendingAsync(coach.UserId)).Should().ContainSingle("a burst is one row per coach and client").Subject;
        burst.GroupId.Should().Be(client.PublicId);
        burst.DueAt.Should().BeCloseTo(Start.AddMinutes(12), TimeSpan.FromMilliseconds(1), "10 quiet minutes after the last photo");

        await Scheduler.RunDueNowAsync(burst.DueAt.AddSeconds(1), Ct);

        var sent = (await SentToAsync(coach.UserId)).Should().ContainSingle().Subject;
        sent.Event.Should().Be(NotificationEvent.ProgressPhotosSubmitted);
        sent.Values[NotificationEmailWorkItem.PhotoCountKey].Should().Be("3");
        sent.Values[NotificationEmailWorkItem.FreeFormKey].Should().Be("true");
        sent.Values[NotificationEmailWorkItem.ClientNameKey].Should().Be("Petra Svobodova");
        sent.Values[NotificationEmailWorkItem.ActionUrlKey].Should().EndWith($"/clients/{client.PublicId}/photos");
        sent.Values.Values.Should().NotContain(value => value.Contains("plan-photos"));

        var (_, html) = new EmailTemplateRenderer(factory.Services.GetRequiredService<IConfiguration>()).RenderNotification(sent);
        html.Should().NotContain("plan-photos").And.NotContain("<img");

        var notice = (await NoticesAsync(coach.UserId)).Should().ContainSingle("one burst is one in-app notice").Subject;
        notice.Data.Should().Contain("\"photoCount\":\"3\"").And.Contain(client.PublicId.ToString()).And.NotContain("requestId");
        (await PendingAsync(coach.UserId)).Should().BeEmpty();

        await Scheduler.RunDueNowAsync(burst.DueAt.AddDays(1), Ct);

        (await SentToAsync(coach.UserId)).Should().ContainSingle("a sent burst is never sent again");
        (await NoticesAsync(coach.UserId)).Should().ContainSingle();
    }

    [Fact]
    public async Task RunDueNowAsync_SinglePhoto_WaitsForTheWindowThenSendsOneEmailAndOneNotice()
    {
        var (coach, client, _) = await SeedLinkAsync();
        await EnableEmailAsync(coach.UserId);
        await UploadAsync(client, TimeSpan.Zero, [coach.UserId]);
        var burst = (await PendingAsync(coach.UserId)).Single();

        await Scheduler.RunDueNowAsync(burst.DueAt.AddSeconds(-1), Ct);

        (await SentToAsync(coach.UserId)).Should().BeEmpty();
        (await NoticesAsync(coach.UserId)).Should().BeEmpty();
        (await PendingAsync(coach.UserId)).Should().ContainSingle();

        await Scheduler.RunDueNowAsync(burst.DueAt.AddSeconds(1), Ct);

        var sent = (await SentToAsync(coach.UserId)).Should().ContainSingle().Subject;
        sent.Values[NotificationEmailWorkItem.PhotoCountKey].Should().Be("1");
        (await NoticesAsync(coach.UserId)).Should().ContainSingle();
    }

    [Fact]
    public async Task RunDueNowAsync_EmailSwitchOffByDefault_SendsNoEmailButStillCreatesTheNotice()
    {
        var (coach, client, _) = await SeedLinkAsync();
        await UploadAsync(client, TimeSpan.Zero, [coach.UserId]);
        await UploadAsync(client, TimeSpan.FromMinutes(1), [coach.UserId]);
        var burst = (await PendingAsync(coach.UserId)).Single();

        await Scheduler.RunDueNowAsync(burst.DueAt.AddSeconds(1), Ct);

        (await SentToAsync(coach.UserId)).Should().BeEmpty("the progress-photo email is off until the coach turns it on");
        (await NoticesAsync(coach.UserId)).Should().ContainSingle();
        (await PendingAsync(coach.UserId)).Should().BeEmpty();
    }

    [Fact]
    public async Task RunDueNowAsync_TwoCoachesSeeTheBurst_EachGetsOneEmailAndOneNotice()
    {
        var (firstCoach, client, _) = await SeedLinkAsync();
        var (secondCoach, _, _) = await SeedLinkAsync(client);
        await EnableEmailAsync(firstCoach.UserId);
        await EnableEmailAsync(secondCoach.UserId);

        await UploadAsync(client, TimeSpan.Zero, [firstCoach.UserId, secondCoach.UserId]);
        await UploadAsync(client, TimeSpan.FromMinutes(1), [firstCoach.UserId, secondCoach.UserId]);

        var due = (await PendingAsync(firstCoach.UserId)).Concat(await PendingAsync(secondCoach.UserId)).Max(p => p.DueAt);
        await Scheduler.RunDueNowAsync(due.AddSeconds(1), Ct);

        foreach (var coach in new[] { firstCoach, secondCoach })
        {
            (await SentToAsync(coach.UserId)).Should().ContainSingle().Which.Values[NotificationEmailWorkItem.PhotoCountKey].Should().Be("2");
            (await NoticesAsync(coach.UserId)).Should().ContainSingle();
        }
    }

    [Fact]
    public async Task RunDueNowAsync_OnlyFreeFormPhotosCount_FoodAndRequestAnswersAreIgnored()
    {
        var (coach, client, linkId) = await SeedLinkAsync();
        await EnableEmailAsync(coach.UserId);
        Guid requestId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var request = new PhotoDiaryRequest
            {
                Id = Guid.NewGuid(),
                ProfessionalId = coach.UserId,
                LinkId = linkId,
                PlanId = Guid.NewGuid(),
                DurationDays = 7,
                Status = PhotoDiaryStatus.InProgress,
                Mode = PhotoDiaryMode.Workflow,
                AcceptedAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.PhotoDiaryRequests.Add(request);
            await db.SaveChangesAsync(Ct);
            requestId = request.Id;
        }

        // All four share one instant and the coach has a viewer row for each, so only the filter can exclude the others.
        await UploadAsync(client, TimeSpan.Zero, [coach.UserId]);
        await UploadAsync(client, TimeSpan.Zero, [coach.UserId], PlanPhotoCategory.Food);
        await UploadAsync(client, TimeSpan.Zero, [coach.UserId], PlanPhotoCategory.Training);
        await UploadAsync(client, TimeSpan.Zero, [coach.UserId], PlanPhotoCategory.Body, requestId);

        var burst = (await PendingAsync(coach.UserId)).Should().ContainSingle().Subject;

        await Scheduler.RunDueNowAsync(burst.DueAt.AddSeconds(1), Ct);

        (await SentToAsync(coach.UserId)).Should().ContainSingle()
            .Which.Values[NotificationEmailWorkItem.PhotoCountKey].Should().Be("1");
    }

    [Fact]
    public async Task RunDueNowAsync_LinkEndedBeforeSending_SendsNothingAndClearsTheBurst()
    {
        var (coach, client, linkId) = await SeedLinkAsync();
        await EnableEmailAsync(coach.UserId);
        await UploadAsync(client, TimeSpan.Zero, [coach.UserId]);
        var burst = (await PendingAsync(coach.UserId)).Single();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.ClientProfessionalLinks.Where(l => l.Id == linkId)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.ConnectionState, ConnectionState.Ended), Ct);
        }

        await Scheduler.RunDueNowAsync(burst.DueAt.AddSeconds(1), Ct);

        (await SentToAsync(coach.UserId)).Should().BeEmpty();
        (await NoticesAsync(coach.UserId)).Should().BeEmpty();
        (await PendingAsync(coach.UserId)).Should().BeEmpty();
    }

    [Fact]
    public async Task RunDueNowAsync_CoachHasNoViewerRowForTheWindow_SendsNothingAndClearsTheBurst()
    {
        var (coach, client, _) = await SeedLinkAsync();
        await EnableEmailAsync(coach.UserId);
        var otherCoach = await TestActors.Trainer(factory).CreateAsync(Ct);

        // The photo is visible to somebody else only; the coach's burst row must find nothing to count.
        await UploadAsync(client, TimeSpan.Zero, [otherCoach.UserId]);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.UpsertPendingNotificationEmailAsync(
                coach.UserId, NotificationEvent.ProgressPhotosSubmitted, client.PublicId, Start, Ct);
        }

        var burst = (await PendingAsync(coach.UserId)).Single();

        await Scheduler.RunDueNowAsync(burst.DueAt.AddSeconds(1), Ct);

        (await SentToAsync(coach.UserId)).Should().BeEmpty();
        (await NoticesAsync(coach.UserId)).Should().BeEmpty();
        (await PendingAsync(coach.UserId)).Should().BeEmpty();
    }

    [Fact]
    public void DueAt_PhotosEveryNineMinutes_NeverLaterThanThirtyMinutesAfterTheFirst()
    {
        var first = Start;

        NotificationEmailGrouping.DueAt(NotificationEvent.ProgressPhotosSubmitted, first, first)
            .Should().Be(first.AddMinutes(10));
        NotificationEmailGrouping.DueAt(NotificationEvent.ProgressPhotosSubmitted, first, first.AddMinutes(9))
            .Should().Be(first.AddMinutes(19));
        NotificationEmailGrouping.DueAt(NotificationEvent.ProgressPhotosSubmitted, first, first.AddMinutes(27))
            .Should().Be(first.AddMinutes(30));
    }

    [Theory]
    [InlineData(PlanPhotoCategory.Body, false, true)]
    [InlineData(PlanPhotoCategory.FreeForm, false, true)]
    [InlineData(PlanPhotoCategory.Food, false, false)]
    [InlineData(PlanPhotoCategory.Training, false, false)]
    [InlineData(PlanPhotoCategory.Body, true, false)]
    public void Qualifies_ByCategoryAndRequest_OnlyFreeFormProgressPhotosStartABurst(
        PlanPhotoCategory category, bool answersRequest, bool expected)
    {
        var photo = new PlanPhoto
        {
            Category = category,
            DiaryRequestId = answersRequest ? Guid.NewGuid() : null,
        };

        ProgressPhotoBurst.Qualifies(photo).Should().Be(expected);
    }
}
