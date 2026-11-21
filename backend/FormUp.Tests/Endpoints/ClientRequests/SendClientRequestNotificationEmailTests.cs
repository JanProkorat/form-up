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

namespace FormUp.Tests.Endpoints.ClientRequests;

/// <summary>
/// The coach's join request email from POST /client/requests.
/// </summary>
[Collection(TestCollection.Shard3)]
public class SendClientRequestNotificationEmailTests(FitnessApiFactory factory)
{
    private async Task<(Actor Coach, HttpStatusCode Status)> SendAsync(bool? emailEnabled, string message)
    {
        var ct = TestContext.Current.CancellationToken;
        var coach = await TestActors.Trainer(factory).CreateAsync(ct);
        var client = await TestActors.Client(factory).WithName("Petra", "Svobodova").CreateAsync(ct);

        if (emailEnabled is { } enabled)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.NotificationPreferences.Add(new NotificationPreference
            {
                UserId = coach.UserId,
                Event = NotificationEvent.JoinRequest,
                EmailEnabled = enabled,
                PushEnabled = false,
            });
            await db.SaveChangesAsync(ct);
        }

        var response = await client.Http.PostAsJsonAsync(
            "/client/requests", new { ProfessionalPublicId = coach.PublicId, Message = message }, ct);

        return (coach, response.StatusCode);
    }

    private Task<IReadOnlyList<NotificationEmailWorkItem>> SentToAsync(Guid coachId) =>
        factory.Services.GetRequiredService<FakeEmailService>()
            .NotificationsForAsync(factory.Services.GetRequiredService<IBackgroundEmailQueue>(), coachId);

    [Fact]
    public async Task SendRequest_WithDefaultPreference_EmailsTheCoachAndKeepsTheInAppNotice()
    {
        var (coach, status) = await SendAsync(emailEnabled: null, "Let's work together");

        status.Should().Be(HttpStatusCode.Created);
        var sent = await SentToAsync(coach.UserId);
        sent.Should().ContainSingle();
        sent[0].Event.Should().Be(NotificationEvent.JoinRequest);
        sent[0].Values[NotificationEmailWorkItem.ClientNameKey].Should().Be("Petra Svobodova");
        sent[0].Values[NotificationEmailWorkItem.MessageKey].Should().Be("Let's work together");
        sent[0].Values[NotificationEmailWorkItem.ActionUrlKey].Should().EndWith("/clients?tab=Pending");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.Notifications.AnyAsync(
            n => n.RecipientUserId == coach.UserId && n.Type == NotificationType.ClientRequestReceived,
            TestContext.Current.CancellationToken)).Should().BeTrue();
    }

    [Fact]
    public async Task SendRequest_WithEmailSwitchOff_SucceedsAndSendsNoEmail()
    {
        var (coach, status) = await SendAsync(emailEnabled: false, "Hello");

        status.Should().Be(HttpStatusCode.Created);
        (await SentToAsync(coach.UserId)).Should().BeEmpty();
    }
}
