using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.PhotoDiaryRequests;

/// <summary>
/// The coach's in-app notice and email when a client submits a photo request
/// (POST /client/photo-diary-requests/{id}/submit).
/// </summary>
[Collection(TestCollection.Shard4)]
public class SubmitRequestNotificationTests(FitnessApiFactory factory)
{
    private async Task<(Actor Coach, Actor Client, Guid RequestId)> SeedAsync(
        bool? emailEnabled, PhotoDiaryStatus status = PhotoDiaryStatus.InProgress)
    {
        var ct = TestContext.Current.CancellationToken;
        var coach = await TestActors.Nutritionist(factory).CreateAsync(ct);
        var client = await TestActors.Client(factory).WithName("Petra", "Svobodova").CreateAsync(ct);
        var linkId = await TestActors.Link(factory, coach, client).AsRole(UserRole.Nutritionist).CreateAsync(ct);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var request = new PhotoDiaryRequest
        {
            Id = Guid.NewGuid(),
            ProfessionalId = coach.UserId,
            LinkId = linkId,
            DurationDays = 7,
            Status = status,
            Mode = PhotoDiaryMode.Workflow,
            AcceptedAt = DateTimeOffset.UtcNow,
            CompletedAt = status == PhotoDiaryStatus.Completed ? DateTimeOffset.UtcNow : null,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.PhotoDiaryRequests.Add(request);

        if (emailEnabled is { } enabled)
        {
            db.NotificationPreferences.Add(new NotificationPreference
            {
                UserId = coach.UserId,
                Event = NotificationEvent.ProgressPhotosSubmitted,
                EmailEnabled = enabled,
                PushEnabled = true,
            });
        }

        await db.SaveChangesAsync(ct);

        return (coach, client, request.Id);
    }

    private Task<HttpResponseMessage> SubmitAsync(Actor client, Guid requestId) =>
        client.Http.PostAsJsonAsync(
            $"/client/photo-diary-requests/{requestId}/submit", new { }, TestContext.Current.CancellationToken);

    private Task<IReadOnlyList<NotificationEmailWorkItem>> EmailsToAsync(Guid coachId) =>
        factory.Services.GetRequiredService<FakeEmailService>()
            .NotificationsForAsync(factory.Services.GetRequiredService<IBackgroundEmailQueue>(), coachId);

    private async Task<int> NoticeCountAsync(Guid coachId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.Notifications.CountAsync(
            n => n.RecipientUserId == coachId && n.Type == NotificationType.ProgressPhotosSubmitted,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Submit_WithEmailSwitchOn_CreatesTheNoticeAndEmailsTheCoach()
    {
        var (coach, client, requestId) = await SeedAsync(emailEnabled: true);

        (await SubmitAsync(client, requestId)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await NoticeCountAsync(coach.UserId)).Should().Be(1);
        var sent = await EmailsToAsync(coach.UserId);
        sent.Should().ContainSingle();
        sent[0].Event.Should().Be(NotificationEvent.ProgressPhotosSubmitted);
        sent[0].Values[NotificationEmailWorkItem.ClientNameKey].Should().Be("Petra Svobodova");
        sent[0].Values[NotificationEmailWorkItem.ActionUrlKey].Should().EndWith($"/clients/{client.PublicId}/photos");
    }

    [Fact]
    public async Task Submit_WithDefaultPreference_CreatesTheNoticeButSendsNoEmail()
    {
        var (coach, client, requestId) = await SeedAsync(emailEnabled: null);

        (await SubmitAsync(client, requestId)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await NoticeCountAsync(coach.UserId)).Should().Be(1);
        (await EmailsToAsync(coach.UserId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Submit_AlreadyCompleted_Returns409WithoutAnotherNoticeOrEmail()
    {
        var (coach, client, requestId) = await SeedAsync(emailEnabled: true, PhotoDiaryStatus.Completed);

        (await SubmitAsync(client, requestId)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        (await NoticeCountAsync(coach.UserId)).Should().Be(0);
        (await EmailsToAsync(coach.UserId)).Should().BeEmpty();
    }
}
