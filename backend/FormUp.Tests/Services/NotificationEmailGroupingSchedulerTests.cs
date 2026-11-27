using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Services;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Services;

/// <summary>
/// The grouped new-message email: a client's burst of chat messages becomes one email to the coach, sent by
/// <see cref="NotificationEmailGroupingScheduler"/>. Tests drive <c>RunDueNowAsync</c> with explicit instants
/// taken from the stored burst, so nothing waits for the hosted loop.
/// </summary>
[Collection(TestCollection.Shard4)]
public class NotificationEmailGroupingSchedulerTests(FitnessApiFactory factory) : IAsyncLifetime
{
    private const string Marker = "ZQX-private-marker-7731";

    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

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

    private async Task<(Actor Coach, Actor Client, Guid ConversationId)> SeedConversationAsync(Actor? existingCoach = null)
    {
        var coach = existingCoach ?? await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).WithName("Petra", "Svobodova").CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).CreateAsync(Ct);

        var created = await client.Http.PostAsJsonAsync("/conversations", new { ParticipantId = coach.PublicId }, Ct);
        created.EnsureSuccessStatusCode();
        var conversation = await created.Content.ReadFromJsonAsync<ConversationResponse>(cancellationToken: Ct);

        return (coach, client, conversation!.Id);
    }

    private sealed record ConversationResponse(Guid Id);

    private static async Task SendAsync(Actor sender, Guid conversationId, string text)
    {
        var response = await sender.Http.PostAsJsonAsync($"/conversations/{conversationId}/messages", new { Text = text }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static async Task MarkReadAsync(Actor reader, Guid conversationId)
    {
        var response = await reader.Http.PostAsJsonAsync($"/conversations/{conversationId}/read", new { }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    private async Task<List<PendingNotificationEmail>> PendingAsync(Guid recipientUserId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.PendingNotificationEmails.AsNoTracking()
            .Where(p => p.RecipientUserId == recipientUserId)
            .ToListAsync(Ct);
    }

    private async Task<IReadOnlyList<NotificationEmailWorkItem>> SentToAsync(Guid recipientUserId) =>
        await factory.Services.GetRequiredService<FakeEmailService>()
            .NotificationsForAsync(factory.Services.GetRequiredService<IBackgroundEmailQueue>(), recipientUserId);

    [Fact]
    public async Task RunDueNowAsync_BurstOfMessages_SendsOneEmailWithTheCountAndTheInboxLink()
    {
        var (coach, client, conversationId) = await SeedConversationAsync();

        foreach (var number in Enumerable.Range(1, 4))
        {
            await SendAsync(client, conversationId, $"{Marker} message {number}");
        }

        var burst = await PendingAsync(coach.UserId);
        burst.Should().ContainSingle("a burst is one row, however many messages it holds");

        await Scheduler.RunDueNowAsync(burst[0].DueAt.AddSeconds(1), Ct);

        var sent = await SentToAsync(coach.UserId);
        sent.Should().ContainSingle();
        sent[0].Event.Should().Be(NotificationEvent.NewMessage);
        sent[0].Values[NotificationEmailWorkItem.MessageCountKey].Should().Be("4");
        sent[0].Values[NotificationEmailWorkItem.ClientNameKey].Should().Be("Petra Svobodova");
        sent[0].Values[NotificationEmailWorkItem.ActionUrlKey].Should().EndWith($"/inbox?client={client.PublicId}");
        (await PendingAsync(coach.UserId)).Should().BeEmpty();

        await Scheduler.RunDueNowAsync(burst[0].DueAt.AddDays(1), Ct);

        (await SentToAsync(coach.UserId)).Should().ContainSingle("a sent burst is never sent again");
    }

    [Fact]
    public async Task RunDueNowAsync_TwoChatsOfOneCoach_SendOneEmailEach()
    {
        var (coach, firstClient, firstConversation) = await SeedConversationAsync();
        var (_, secondClient, secondConversation) = await SeedConversationAsync(coach);

        await SendAsync(firstClient, firstConversation, "first chat");
        await SendAsync(secondClient, secondConversation, "second chat");

        var bursts = await PendingAsync(coach.UserId);
        bursts.Should().HaveCount(2);

        await Scheduler.RunDueNowAsync(bursts.Max(b => b.DueAt).AddSeconds(1), Ct);

        var sent = await SentToAsync(coach.UserId);
        sent.Should().HaveCount(2);
        sent.Select(item => item.Values[NotificationEmailWorkItem.ClientPublicIdKey])
            .Should().BeEquivalentTo([firstClient.PublicId.ToString(), secondClient.PublicId.ToString()]);
    }

    [Fact]
    public async Task RunDueNowAsync_BeforeTheDueTime_SendsNothingAndKeepsTheBurst()
    {
        var (coach, client, conversationId) = await SeedConversationAsync();
        await SendAsync(client, conversationId, "hello");

        var burst = (await PendingAsync(coach.UserId)).Single();

        await Scheduler.RunDueNowAsync(burst.DueAt.AddSeconds(-1), Ct);

        (await SentToAsync(coach.UserId)).Should().BeEmpty();
        (await PendingAsync(coach.UserId)).Should().ContainSingle();
    }

    [Fact]
    public async Task RunDueNowAsync_CoachReadEverythingBeforeTheDueTime_SendsNothingAndClearsTheBurst()
    {
        var (coach, client, conversationId) = await SeedConversationAsync();
        await SendAsync(client, conversationId, "one");
        await SendAsync(client, conversationId, "two");
        var burst = (await PendingAsync(coach.UserId)).Single();

        await MarkReadAsync(coach, conversationId);
        await Scheduler.RunDueNowAsync(burst.DueAt.AddSeconds(1), Ct);

        (await SentToAsync(coach.UserId)).Should().BeEmpty();
        (await PendingAsync(coach.UserId)).Should().BeEmpty();
    }

    [Fact]
    public async Task RunDueNowAsync_CoachReadMidBurst_CountsOnlyTheUnreadMessages()
    {
        var (coach, client, conversationId) = await SeedConversationAsync();
        await SendAsync(client, conversationId, "one");
        await SendAsync(client, conversationId, "two");
        await MarkReadAsync(coach, conversationId);
        await SendAsync(client, conversationId, "three");

        var burst = (await PendingAsync(coach.UserId)).Single();

        await Scheduler.RunDueNowAsync(burst.DueAt.AddSeconds(1), Ct);

        var sent = await SentToAsync(coach.UserId);
        sent.Should().ContainSingle();
        sent[0].Values[NotificationEmailWorkItem.MessageCountKey].Should().Be("1");
    }

    [Fact]
    public async Task RunDueNowAsync_EmailSwitchOff_SendsNothingAndClearsTheBurst()
    {
        var (coach, client, conversationId) = await SeedConversationAsync();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.NotificationPreferences.Add(new NotificationPreference
            {
                UserId = coach.UserId,
                Event = NotificationEvent.NewMessage,
                EmailEnabled = false,
                PushEnabled = true,
            });
            await db.SaveChangesAsync(Ct);
        }

        await SendAsync(client, conversationId, "hello");
        var burst = (await PendingAsync(coach.UserId)).Single();

        await Scheduler.RunDueNowAsync(burst.DueAt.AddSeconds(1), Ct);

        (await SentToAsync(coach.UserId)).Should().BeEmpty();
        (await PendingAsync(coach.UserId)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("en")]
    [InlineData("cs")]
    [InlineData("de")]
    public async Task RunDueNowAsync_EmailInEveryLanguage_ContainsNoMessageText(string language)
    {
        var coach = await TestActors.Trainer(factory).WithLanguage(language).CreateAsync(Ct);
        var (_, client, conversationId) = await SeedConversationAsync(coach);

        foreach (var number in Enumerable.Range(1, 4))
        {
            await SendAsync(client, conversationId, $"{Marker} secret {number}");
        }

        var burst = (await PendingAsync(coach.UserId)).Single();
        await Scheduler.RunDueNowAsync(burst.DueAt.AddSeconds(1), Ct);

        var item = (await SentToAsync(coach.UserId)).Should().ContainSingle().Subject;
        var renderer = new EmailTemplateRenderer(factory.Services.GetRequiredService<IConfiguration>());
        var (subject, html) = renderer.RenderNotification(item);

        item.Values.Values.Should().NotContain(value => value.Contains(Marker));
        subject.Should().NotContain(Marker);
        html.Should().NotContain(Marker).And.NotContain("secret");
        html.Should().Contain($"lang=\"{language}\"");
    }

    [Fact]
    public async Task Upsert_BurstSpreadOverTime_DueNeverLaterThanTheMaximumWait()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var group = Guid.NewGuid();
        var start = DateTime.UtcNow.AddHours(-1);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        async Task<PendingNotificationEmail> UpsertAsync(TimeSpan offset)
        {
            await db.UpsertPendingNotificationEmailAsync(
                coach.UserId, NotificationEvent.NewMessage, group, start + offset, Ct);

            return await db.PendingNotificationEmails.AsNoTracking().SingleAsync(p => p.RecipientUserId == coach.UserId, Ct);
        }

        var precision = TimeSpan.FromMilliseconds(1);

        (await UpsertAsync(TimeSpan.Zero)).DueAt.Should().BeCloseTo(start.AddMinutes(5), precision);
        (await UpsertAsync(TimeSpan.FromMinutes(4))).DueAt.Should().BeCloseTo(start.AddMinutes(9), precision);
        (await UpsertAsync(TimeSpan.FromMinutes(8))).DueAt.Should().BeCloseTo(start.AddMinutes(13), precision);
        (await UpsertAsync(TimeSpan.FromMinutes(12))).DueAt.Should().BeCloseTo(start.AddMinutes(15), precision);

        var late = await UpsertAsync(TimeSpan.FromMinutes(14));
        late.DueAt.Should().BeCloseTo(start.AddMinutes(15), precision);
        late.FirstEventAt.Should().BeCloseTo(start, precision);
        late.LastEventAt.Should().BeCloseTo(start.AddMinutes(14), precision);

        var older = await UpsertAsync(TimeSpan.FromMinutes(1));
        older.LastEventAt.Should().BeCloseTo(start.AddMinutes(14), precision, "an older event never moves the burst back");
        older.DueAt.Should().BeCloseTo(start.AddMinutes(15), precision);
    }
}
