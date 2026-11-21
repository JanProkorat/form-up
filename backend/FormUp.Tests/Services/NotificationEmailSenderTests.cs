using FluentAssertions;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Services;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FormUp.Tests.Services;

/// <summary>
/// <see cref="NotificationEmailSender"/>: the email switch (saved row, then default), the queue hand-off
/// and the rule that a failed email never throws.
/// </summary>
[Collection(TestCollection.Shard2)]
public class NotificationEmailSenderTests(FitnessApiFactory factory)
{
    private static readonly IReadOnlyDictionary<string, string> Values = new Dictionary<string, string>
    {
        [NotificationEmailWorkItem.ClientNameKey] = "Petra Svobodova",
        [NotificationEmailWorkItem.ClientPublicIdKey] = Guid.NewGuid().ToString(),
    };

    private async Task<Actor> SeedCoachAsync(string language, NotificationEvent? savedEvent = null, bool emailEnabled = false)
    {
        var ct = TestContext.Current.CancellationToken;
        var coach = await TestActors.Trainer(factory).WithLanguage(language).CreateAsync(ct);

        if (savedEvent is { } notificationEvent)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.NotificationPreferences.Add(new NotificationPreference
            {
                UserId = coach.UserId,
                Event = notificationEvent,
                EmailEnabled = emailEnabled,
                PushEnabled = false,
            });
            await db.SaveChangesAsync(ct);
        }

        return coach;
    }

    private async Task<IReadOnlyList<NotificationEmailWorkItem>> SendAsync(
        Guid coachId, NotificationEvent notificationEvent, IReadOnlyDictionary<string, string>? values = null)
    {
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<INotificationEmailSender>()
                .SendAsync(coachId, notificationEvent, values ?? Values, TestContext.Current.CancellationToken);
        }

        return await factory.Services.GetRequiredService<FakeEmailService>()
            .NotificationsForAsync(factory.Services.GetRequiredService<IBackgroundEmailQueue>(), coachId);
    }

    [Theory]
    [InlineData(NotificationEvent.NewMessage, true)]
    [InlineData(NotificationEvent.WeeklyCheckInSubmitted, true)]
    [InlineData(NotificationEvent.JoinRequest, true)]
    [InlineData(NotificationEvent.ProgressPhotosSubmitted, false)]
    [InlineData(NotificationEvent.WorkoutFinished, false)]
    public async Task SendAsync_NoSavedPreference_FollowsTheDefault(NotificationEvent notificationEvent, bool expectEmail)
    {
        var coach = await SeedCoachAsync("en");

        var sent = await SendAsync(coach.UserId, notificationEvent);

        sent.Count.Should().Be(expectEmail ? 1 : 0);
    }

    [Theory]
    [InlineData(NotificationEvent.WeeklyCheckInSubmitted, false)]
    [InlineData(NotificationEvent.WorkoutFinished, true)]
    public async Task SendAsync_SavedPreference_OverridesTheDefault(NotificationEvent notificationEvent, bool emailEnabled)
    {
        var coach = await SeedCoachAsync("en", notificationEvent, emailEnabled);

        var sent = await SendAsync(coach.UserId, notificationEvent);

        sent.Count.Should().Be(emailEnabled ? 1 : 0);
    }

    [Fact]
    public async Task SendAsync_RecipientLanguageCs_QueuesItInCzechWithAPortalLink()
    {
        var coach = await SeedCoachAsync("cs");

        var sent = await SendAsync(coach.UserId, NotificationEvent.WeeklyCheckInSubmitted);

        sent.Should().ContainSingle();
        sent[0].Language.Should().Be("cs");
        sent[0].Values[NotificationEmailWorkItem.ActionUrlKey]
            .Should().EndWith($"/clients/{Values[NotificationEmailWorkItem.ClientPublicIdKey]}/forms");
    }

    [Fact]
    public async Task SendAsync_NewMessageWithClient_LinksTheInboxConversation()
    {
        var coach = await SeedCoachAsync("en");

        var sent = await SendAsync(coach.UserId, NotificationEvent.NewMessage);

        sent.Should().ContainSingle();
        sent[0].Values[NotificationEmailWorkItem.ActionUrlKey]
            .Should().EndWith($"/inbox?client={Values[NotificationEmailWorkItem.ClientPublicIdKey]}");
    }

    [Fact]
    public async Task SendAsync_NewMessageWithoutClientProfile_LinksTheInbox()
    {
        var coach = await SeedCoachAsync("en");
        var withoutProfile = new Dictionary<string, string>
        {
            [NotificationEmailWorkItem.ClientNameKey] = "Petra Svobodova",
        };

        var sent = await SendAsync(coach.UserId, NotificationEvent.NewMessage, withoutProfile);

        sent.Should().ContainSingle();
        sent[0].Values[NotificationEmailWorkItem.ActionUrlKey].Should().EndWith("/inbox");
    }

    [Fact]
    public async Task SendAsync_QueueFull_DoesNotThrow()
    {
        var coach = await SeedCoachAsync("en");
        var queue = Substitute.For<IBackgroundEmailQueue>();
        queue.TryEnqueue(Arg.Any<EmailDispatchWorkItem>()).Returns(false);

        using var scope = factory.Services.CreateScope();
        var sender = new NotificationEmailSender(
            scope.ServiceProvider.GetRequiredService<IApplicationDbContext>(),
            queue,
            new ConfigurationBuilder().Build(),
            NullLogger<NotificationEmailSender>.Instance);

        var act = () => sender.SendAsync(
            coach.UserId, NotificationEvent.WeeklyCheckInSubmitted, Values, TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
        queue.Received(1).TryEnqueue(Arg.Any<NotificationEmailWorkItem>());
    }
}
