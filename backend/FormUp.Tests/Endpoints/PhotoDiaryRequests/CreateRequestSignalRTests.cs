using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.PhotoDiaryRequests;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.PhotoDiaryRequests;

/// <summary>
/// Integration tests verifying that POST /trainer/photo-diary-requests emits
/// the <c>photoDiaryRequested</c> SignalR event to the correct client and that
/// broadcast failures do not roll back the mutation.
///
/// Uses <see cref="FitnessApiFactory"/> (Testcontainers-backed PostgreSQL + MongoDB)
/// and the shared <see cref="FakeRealtimeNotifier"/> singleton so that
/// <c>Send.CreatedAtAsync</c> (which requires a real <c>LinkGenerator</c>) works
/// without any stub wiring.
/// </summary>
[Collection(TestCollection.Name)]
public class CreateRequestSignalRTests(FitnessApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string UniqueEmail(string tag = "signalr") =>
        $"{Guid.NewGuid():N}@{tag}-diary-signalr.com";

    // ── Setup helpers ──────────────────────────────────────────────────────────

    private async Task<(HttpClient Http, Guid UserId, string FirstName, string LastName)>
        SetupProfessionalAsync(string firstName = "Jana", string lastName = "Novakova")
    {
        var actor = await TestActors.Nutritionist(factory)
            .WithEmail(UniqueEmail("prof"))
            .WithName(firstName, lastName)
            .CreateAsync(TestContext.Current.CancellationToken);

        return (actor.Http, actor.UserId, firstName, lastName);
    }

    private async Task<(HttpClient Http, Guid UserId)> SetupClientAsync()
    {
        var actor = await TestActors.Client(factory)
            .WithEmail(UniqueEmail("client"))
            .WithName("Petr", "Novak")
            .CreateAsync(TestContext.Current.CancellationToken);

        return (actor.Http, actor.UserId);
    }

    private async Task<Guid> InsertLinkAsync(Guid clientUserId, Guid professionalUserId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var profProfile = await db.ProfessionalProfiles
            .FirstAsync(p => p.UserId == professionalUserId, TestContext.Current.CancellationToken);
        var clientProfile = await db.ClientProfiles
            .FirstAsync(p => p.UserId == clientUserId, TestContext.Current.CancellationToken);

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
        return clientProfile.PublicId;
    }

    private async Task<long> InsertPendingInviteAsync(Guid professionalUserId, string inviteeEmail)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var profProfile = await db.ProfessionalProfiles
            .FirstAsync(p => p.UserId == professionalUserId, TestContext.Current.CancellationToken);

        var invite = new PendingInvite
        {
            ProfessionalProfileId = profProfile.Id,
            Email = inviteeEmail,
            SentAt = DateTime.UtcNow,
            Status = PendingInviteStatus.Pending,
            PublicId = Guid.NewGuid(),
            DateCreated = DateTime.UtcNow,
        };
        db.PendingInvites.Add(invite);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return invite.Id;
    }

    private FakeRealtimeNotifier GetNotifier() =>
        factory.Services.GetRequiredService<FakeRealtimeNotifier>();

    // ── Tests ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateRequest_LinkBased_EmitsPhotoDiaryRequested_ToClient()
    {
        var notifier = GetNotifier();
        notifier.Reset();

        var (profHttp, profId, firstName, lastName) = await SetupProfessionalAsync();
        var (_, clientId) = await SetupClientAsync();
        var clientPublicId = await InsertLinkAsync(clientId, profId);

        var response = await profHttp.PostAsJsonAsync(
            "/trainer/photo-diary-requests",
            new { ClientId = clientPublicId, Mode = "Workflow", DurationDays = 7 },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var calls = notifier.Calls
            .Where(c => c.EventType == "photodiaryrequested")
            .ToList();

        calls.Should().HaveCount(1, "exactly one photoDiaryRequested event must be emitted");
        calls[0].UserId.Should().Be(clientId, "the event must be addressed to the client");

        var evt = calls[0].Payload.Should().BeOfType<PhotoDiaryRequestedEvent>().Subject;
        evt.DurationDays.Should().Be(7);
        evt.ProfessionalName.Should().Be($"{firstName} {lastName}");
        evt.ProfessionalRole.Should().Be("Nutritionist");
    }

    [Fact]
    public async Task CreateRequest_WithModeAndMessage_ClientNotificationCarriesBoth()
    {
        var notifier = GetNotifier();
        notifier.Reset();

        var (profHttp, profId, _, _) = await SetupProfessionalAsync();
        var (_, clientId) = await SetupClientAsync();
        var clientPublicId = await InsertLinkAsync(clientId, profId);

        var response = await profHttp.PostAsJsonAsync(
            "/trainer/photo-diary-requests",
            new { ClientId = clientPublicId, Mode = "Bulk", Message = "Please send a photo of your breakfast" },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var evt = notifier.Calls
            .Single(c => c.EventType == "photodiaryrequested")
            .Payload.Should().BeOfType<PhotoDiaryRequestedEvent>().Subject;
        evt.Mode.Should().Be("Bulk");
        evt.Message.Should().Be("Please send a photo of your breakfast");
        evt.DurationDays.Should().Be(1);
    }

    [Fact]
    public async Task CreateRequest_LinkBased_EventContainsCorrectRequestId()
    {
        var notifier = GetNotifier();
        notifier.Reset();

        var (profHttp, profId, _, _) = await SetupProfessionalAsync();
        var (_, clientId) = await SetupClientAsync();
        var clientPublicId = await InsertLinkAsync(clientId, profId);

        var response = await profHttp.PostAsJsonAsync(
            "/trainer/photo-diary-requests",
            new { ClientId = clientPublicId, Mode = "Workflow", DurationDays = 14 },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<CreateResponseBody>(
            JsonOptions, TestContext.Current.CancellationToken);
        body.Should().NotBeNull();

        var calls = notifier.Calls
            .Where(c => c.EventType == "photodiaryrequested")
            .ToList();

        calls.Should().HaveCount(1);
        var evt = calls[0].Payload.Should().BeOfType<PhotoDiaryRequestedEvent>().Subject;
        evt.RequestId.Should().Be(body!.Id, "the RequestId in the event must match the response body Id");
    }

    [Fact]
    public async Task CreateRequest_InviteBased_ExistingUser_EmitsPhotoDiaryRequested_ToClient()
    {
        var notifier = GetNotifier();
        notifier.Reset();

        var (profHttp, profId, _, _) = await SetupProfessionalAsync();
        var (_, clientId) = await SetupClientAsync();

        // Get the client's email so we can create an invite for it
        string clientEmail;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.FirstAsync(
                u => u.Id == clientId, TestContext.Current.CancellationToken);
            clientEmail = user.Email!;
        }

        var inviteId = await InsertPendingInviteAsync(profId, clientEmail);

        var response = await profHttp.PostAsJsonAsync(
            "/trainer/photo-diary-requests",
            new { PendingInviteId = inviteId, Mode = "Workflow", DurationDays = 7 },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var calls = notifier.Calls
            .Where(c => c.EventType == "photodiaryrequested")
            .ToList();

        calls.Should().HaveCount(1, "the registered client must receive exactly one photoDiaryRequested event");
        calls[0].UserId.Should().Be(clientId, "the event must be addressed to the registered client's userId");
    }

    [Fact]
    public async Task CreateRequest_InviteBased_NoExistingUser_NoNotification()
    {
        var notifier = GetNotifier();
        notifier.Reset();

        var (profHttp, profId, _, _) = await SetupProfessionalAsync();

        // Use an e-mail address that has no registered account
        var unregisteredEmail = $"unregistered-{Guid.NewGuid():N}@example.com";
        var inviteId = await InsertPendingInviteAsync(profId, unregisteredEmail);

        var response = await profHttp.PostAsJsonAsync(
            "/trainer/photo-diary-requests",
            new { PendingInviteId = inviteId, Mode = "Workflow", DurationDays = 7 },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var calls = notifier.Calls
            .Where(c => c.EventType == "photodiaryrequested")
            .ToList();

        calls.Should().BeEmpty(
            "no notification should be sent when the invitee has not registered yet");
    }

    [Fact]
    public async Task CreateRequest_BroadcastThrows_MutationStillSucceeds()
    {
        var notifier = GetNotifier();
        notifier.Reset();
        notifier.SimulateThrowOnNextCall();

        var (profHttp, profId, _, _) = await SetupProfessionalAsync();
        var (_, clientId) = await SetupClientAsync();
        var clientPublicId = await InsertLinkAsync(clientId, profId);

        var response = await profHttp.PostAsJsonAsync(
            "/trainer/photo-diary-requests",
            new { ClientId = clientPublicId, Mode = "Workflow", DurationDays = 7 },
            TestContext.Current.CancellationToken);

        // The HTTP response must still be 200 even though the notifier threw
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<CreateResponseBody>(
            JsonOptions, TestContext.Current.CancellationToken);
        body.Should().NotBeNull();

        // The entity must have been persisted despite the broadcast failure
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var persisted = await db.PhotoDiaryRequests
            .FirstOrDefaultAsync(r => r.Id == body!.Id, TestContext.Current.CancellationToken);
        persisted.Should().NotBeNull("the mutation must persist even when the broadcast throws");
    }

    // ── Response shape helper ──────────────────────────────────────────────────

    private record CreateResponseBody(
        Guid Id,
        Guid ProfessionalId,
        long? LinkId,
        long? PendingInviteId,
        Guid? PlanId,
        int DurationDays,
        PhotoDiaryStatus Status,
        DateTimeOffset CreatedAt);
}
