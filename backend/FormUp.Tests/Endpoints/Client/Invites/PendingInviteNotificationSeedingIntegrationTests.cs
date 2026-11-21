using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Client.Invites;

/// <summary>
/// End-to-end coverage for the verification-time InvitationReceived notification (#1676): an
/// invitee who had no account when the invite was sent still gets the in-app notification once
/// their email is verified.
/// </summary>
[Collection(TestCollection.Shard3)]
public class PendingInviteNotificationSeedingIntegrationTests(FitnessApiFactory factory)
{
    private FakeEmailService EmailService => factory.Services.GetRequiredService<FakeEmailService>();

    private static string UniqueEmail() => $"invitee-{Guid.NewGuid():N}@test.com";

    private async Task<Guid> InviteAsync(Actor coach, string email)
    {
        var response = await coach.Http.PostAsJsonAsync(
            "/trainer/pending-invites", new { Email = email },
            cancellationToken: TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<InviteResult>(
            cancellationToken: TestContext.Current.CancellationToken);
        return body!.PublicId;
    }

    private async Task RegisterAndVerifyAsync(string email)
    {
        var http = factory.CreateClient();
        var register = await TestHelpers.RegisterAsync(http, email, "TestPass1!", "Pat", "Invitee", "Client");
        register.StatusCode.Should().Be(HttpStatusCode.Created);

        var verification = EmailService.SentVerifications.Should().ContainSingle(v => v.Email == email).Subject;
        var verify = await http.PostAsJsonAsync(
            "/auth/verify-email", new { Token = verification.Token },
            cancellationToken: TestContext.Current.CancellationToken);
        verify.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<List<string?>> GetInviteNotificationPayloadsAsync(string email)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userId = await db.Users
            .Where(u => u.Email == email)
            .Select(u => u.Id)
            .SingleAsync(TestContext.Current.CancellationToken);

        return await db.Notifications
            .Where(n => n.RecipientUserId == userId && n.Type == NotificationType.InvitationReceived)
            .Select(n => n.Data)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task VerifyEmail_WithPendingInvite_CreatesOneNotificationWithInvitePayload_AndReSeedIsNoOp()
    {
        var coach = await TestActors.Trainer(factory).WithName("Coach", "Carl").CreateAsync(TestContext.Current.CancellationToken);
        var email = UniqueEmail();
        var inviteId = await InviteAsync(coach, email);

        await RegisterAndVerifyAsync(email);

        var payloads = await GetInviteNotificationPayloadsAsync(email);
        payloads.Should().ContainSingle();
        var data = JsonSerializer.Deserialize<Dictionary<string, string>>(payloads[0]!)!;
        data["trainerName"].Should().Be("Coach Carl");
        data["inviteId"].Should().Be(inviteId.ToString());

        // A second pass for the same account (e.g. a repeated verification) must not duplicate it.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.SingleAsync(u => u.Email == email, TestContext.Current.CancellationToken);
            await scope.ServiceProvider.GetRequiredService<IPendingInviteNotificationSeeder>()
                .SeedForNewUserAsync(user, TestContext.Current.CancellationToken);
        }

        (await GetInviteNotificationPayloadsAsync(email)).Should().ContainSingle();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task VerifyEmail_WithExpiredOrDeclinedInvite_CreatesNoNotification(bool expired)
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);
        var email = UniqueEmail();
        var inviteId = await InviteAsync(coach, email);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var invite = await db.PendingInvites.SingleAsync(pi => pi.PublicId == inviteId, TestContext.Current.CancellationToken);

            if (expired)
            {
                invite.SentAt = DateTime.UtcNow.AddDays(-8);
            }
            else
            {
                invite.Status = PendingInviteStatus.Declined;
            }

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await RegisterAndVerifyAsync(email);

        (await GetInviteNotificationPayloadsAsync(email)).Should().BeEmpty();
    }

    private record InviteResult(Guid PublicId);
}
