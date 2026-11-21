using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Trainers.ClientRequests;

/// <summary>
/// End-to-end (real Postgres via Testcontainers) coverage for the cooperation-event write
/// seam on the client-request flow (#1100 C5): <c>SendClientRequestEndpoint</c>,
/// <c>AcceptClientRequestEndpoint</c> (including its sibling auto-cancel), and
/// <c>RejectClientRequestEndpoint</c>/<c>CancelClientRequestEndpoint</c>.
/// </summary>
/// <remarks>
/// Also proves the transaction-safety question raised in the #1100 C5 dispatch:
/// <c>AcceptClientRequestEndpoint</c>'s <c>AppendCooperationEventAsync</c> call runs AFTER
/// <c>transaction.CommitAsync(ct)</c> (the #1009 profession-slot-lock transaction only wraps
/// the slot check and the link save) — so a 23505 the seam swallows internally cannot abort
/// any enclosing transaction there isn't one. <see cref="Accept_EventAppendNoOps_StillReturns204_AndCreatesLink"/>
/// forces exactly that no-op (by writing the Accepted row through the seam directly before the
/// HTTP accept call) and proves the endpoint still returns 204 and still creates the link.
/// </remarks>
[Collection(TestCollection.Shard2)]
public class RequestCooperationEventFlowIntegrationTests(FitnessApiFactory factory)
{
    // The API serializes enums as strings (JsonStringEnumConverter globally), so use matching
    // client-side options wherever a response includes Kind/EventType.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private async Task<Guid> SendRequestAsync(HttpClient clientHttp, Guid professionalPublicId, string? message)
    {
        var response = await clientHttp.PostAsJsonAsync(
            "/client/requests",
            new { ProfessionalPublicId = professionalPublicId, Message = message },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<SendRequestResult>(
            JsonOptions, TestContext.Current.CancellationToken);
        return body!.PublicId;
    }

    private async Task<Guid?> FindConversationIdAsync(HttpClient http, Guid otherUserId)
    {
        var response = await http.GetAsync("/conversations", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var conversations = await response.Content.ReadFromJsonAsync<List<ConversationResult>>(
            JsonOptions, TestContext.Current.CancellationToken);
        return conversations!.FirstOrDefault(c => c.Participant.Id == otherUserId)?.Id;
    }

    /// <summary>
    /// Messages ordered chronologically (oldest first) — <c>GetMessagesEndpoint</c> returns them
    /// newest-first for pagination, so the test reverses before asserting write order.
    /// </summary>
    private async Task<List<MessageResult>> GetMessagesChronologicalAsync(HttpClient http, Guid conversationId)
    {
        var response = await http.GetAsync(
            $"/conversations/{conversationId}/messages", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<GetMessagesResult>(
            JsonOptions, TestContext.Current.CancellationToken);
        body!.Items.Reverse();
        return body.Items;
    }

    private async Task<bool> ConversationExistsAsync(Guid professionalUserId, Guid clientUserId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Conversations.AnyAsync(
            c => c.ProfessionalUserId == professionalUserId && c.ClientUserId == clientUserId,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SendRequest_WithMessage_WritesRequestedEvent_ThenTextMessage_BothFromClient()
    {
        var coach = await TestActors.Trainer(factory).WithName("Coach", "Carl").CreateAsync();
        var client = await TestActors.Client(factory).WithName("Jane", "Doe").CreateAsync();

        var requestPublicId = await SendRequestAsync(client.Http, coach.PublicId, "Would love to work with you!");
        var conversationId = await FindConversationIdAsync(client.Http, coach.UserId);
        conversationId.Should().NotBeNull("SendClientRequestEndpoint must get-or-create the thread server-side (R2=A)");

        var messages = await GetMessagesChronologicalAsync(client.Http, conversationId!.Value);

        messages.Should().HaveCount(2, "one Requested event and one Text row beneath it");
        messages[0].Kind.Should().Be(ChatMessageKind.Event);
        messages[0].EventType.Should().Be(ChatEventType.Requested);
        messages[0].SenderId.Should().Be(client.UserId);
        messages[1].Kind.Should().Be(ChatMessageKind.Text);
        messages[1].SenderId.Should().Be(client.UserId);
        messages[1].Text.Should().Be("Would love to work with you!");

        _ = requestPublicId;
    }

    [Fact]
    public async Task Accept_BlankStatement_WritesOnlyAcceptedEvent_NoTextRow()
    {
        var coach = await TestActors.Trainer(factory).WithName("Coach", "Carl").CreateAsync();
        var client = await TestActors.Client(factory).WithName("Jane", "Doe").CreateAsync();

        var requestPublicId = await SendRequestAsync(client.Http, coach.PublicId, message: null);
        var conversationId = await FindConversationIdAsync(client.Http, coach.UserId);

        var acceptResponse = await coach.Http.PostAsJsonAsync(
            $"/trainer/client-requests/{requestPublicId}/accept",
            new { }, TestContext.Current.CancellationToken);
        acceptResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var messages = await GetMessagesChronologicalAsync(client.Http, conversationId!.Value);

        messages.Should().HaveCount(2, "one Requested event, one Accepted event — no Text row for a blank statement");
        messages[1].Kind.Should().Be(ChatMessageKind.Event);
        messages[1].EventType.Should().Be(ChatEventType.Accepted);
        messages[1].SenderId.Should().Be(coach.UserId);
        messages.Should().NotContain(m => m.Kind == ChatMessageKind.Text);
    }

    [Fact]
    public async Task Accept_WithStatement_WritesAcceptedEvent_ThenTextFromCoach()
    {
        var coach = await TestActors.Trainer(factory).WithName("Coach", "Carl").CreateAsync();
        var client = await TestActors.Client(factory).WithName("Jane", "Doe").CreateAsync();

        var requestPublicId = await SendRequestAsync(client.Http, coach.PublicId, message: null);
        var conversationId = await FindConversationIdAsync(client.Http, coach.UserId);

        var acceptResponse = await coach.Http.PostAsJsonAsync(
            $"/trainer/client-requests/{requestPublicId}/accept",
            new { Statement = "Excited to get started!" }, TestContext.Current.CancellationToken);
        acceptResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var messages = await GetMessagesChronologicalAsync(client.Http, conversationId!.Value);

        messages.Should().HaveCount(3, "Requested event, Accepted event, and the statement's Text row");
        messages[1].Kind.Should().Be(ChatMessageKind.Event);
        messages[1].EventType.Should().Be(ChatEventType.Accepted);
        messages[1].SenderId.Should().Be(coach.UserId);
        messages[2].Kind.Should().Be(ChatMessageKind.Text);
        messages[2].SenderId.Should().Be(coach.UserId);
        messages[2].Text.Should().Be("Excited to get started!");
    }

    [Fact]
    public async Task Decline_BlankStatement_WritesExactlyOneDeclinedRow_NoTextRow()
    {
        var coach = await TestActors.Trainer(factory).WithName("Coach", "Carl").CreateAsync();
        var client = await TestActors.Client(factory).WithName("Jane", "Doe").CreateAsync();

        var requestPublicId = await SendRequestAsync(client.Http, coach.PublicId, message: null);
        var conversationId = await FindConversationIdAsync(client.Http, coach.UserId);

        var rejectResponse = await coach.Http.PostAsJsonAsync(
            $"/trainer/client-requests/{requestPublicId}/reject",
            new { }, TestContext.Current.CancellationToken);
        rejectResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var messages = await GetMessagesChronologicalAsync(client.Http, conversationId!.Value);

        messages.Should().HaveCount(2, "one Requested event, one Declined event — no Text row for a blank statement");
        messages.Count(m => m is { Kind: ChatMessageKind.Event, EventType: ChatEventType.Declined }).Should().Be(1);
        messages.Should().NotContain(m => m.Kind == ChatMessageKind.Text);
    }

    [Fact]
    public async Task Cancel_WithExistingThread_WritesWithdrawnEvent()
    {
        var coach = await TestActors.Trainer(factory).WithName("Coach", "Carl").CreateAsync();
        var client = await TestActors.Client(factory).WithName("Jane", "Doe").CreateAsync();

        var requestPublicId = await SendRequestAsync(client.Http, coach.PublicId, "Hello there!");
        var conversationId = await FindConversationIdAsync(client.Http, coach.UserId);

        var cancelResponse = await client.Http.DeleteAsync(
            $"/client/requests/{requestPublicId}", TestContext.Current.CancellationToken);
        cancelResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var messages = await GetMessagesChronologicalAsync(client.Http, conversationId!.Value);

        messages.Should().HaveCount(3, "Requested event, its Text row, and the Withdrawn event");
        messages[2].Kind.Should().Be(ChatMessageKind.Event);
        messages[2].EventType.Should().Be(ChatEventType.Withdrawn);
        messages[2].SenderId.Should().Be(client.UserId);
    }

    /// <summary>
    /// A <see cref="ClientRequest"/> row written directly (bypassing <c>SendClientRequestEndpoint</c>,
    /// which always creates the thread) so no conversation exists yet — proves
    /// <c>CancelClientRequestEndpoint</c>'s Withdrawn append (<c>createConversationIfMissing: false</c>)
    /// never creates a thread just to announce there is nothing to see (R3).
    /// </summary>
    [Fact]
    public async Task Cancel_NoExistingThread_NoConversationCreated()
    {
        var coach = await TestActors.Trainer(factory).WithName("Coach", "Carl").CreateAsync();
        var client = await TestActors.Client(factory).WithName("Jane", "Doe").CreateAsync();

        Guid requestPublicId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var request = new ClientRequest
            {
                ClientProfileId = client.ProfileId,
                ProfessionalProfileId = coach.ProfileId,
                Status = ClientRequestStatus.Pending,
                SentAt = DateTime.UtcNow,
            };
            db.ClientRequests.Add(request);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            requestPublicId = request.PublicId;
        }

        (await ConversationExistsAsync(coach.UserId, client.UserId)).Should().BeFalse(
            "no thread exists yet — the request was inserted directly, bypassing SendClientRequestEndpoint");

        var cancelResponse = await client.Http.DeleteAsync(
            $"/client/requests/{requestPublicId}", TestContext.Current.CancellationToken);
        cancelResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await ConversationExistsAsync(coach.UserId, client.UserId)).Should().BeFalse(
            "a Withdrawn event must never create a thread just to announce there is nothing to see (R3)");
    }

    /// <summary>
    /// A third coach's <see cref="ClientRequest"/> is inserted directly (no thread), covering the
    /// "none where no thread" half of the sibling-auto-cancel requirement alongside the
    /// "Withdrawn in THAT thread" half for the second (API-created) sibling.
    /// </summary>
    [Fact]
    public async Task Accept_SiblingPendingRequestWithThread_WritesWithdrawn_SiblingWithNoThread_StaysThreadless()
    {
        var coachA = await TestActors.Trainer(factory).WithName("Coach", "A").CreateAsync();
        var coachB = await TestActors.Trainer(factory).WithName("Coach", "B").CreateAsync();
        var coachC = await TestActors.Trainer(factory).WithName("Coach", "C").CreateAsync();
        var client = await TestActors.Client(factory).WithName("Jane", "Doe").CreateAsync();

        var requestToA = await SendRequestAsync(client.Http, coachA.PublicId, message: null);
        await SendRequestAsync(client.Http, coachB.PublicId, message: null);
        var conversationWithB = await FindConversationIdAsync(client.Http, coachB.UserId);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var clientProfile = await db.ClientProfiles.FirstAsync(
                cp => cp.UserId == client.UserId, TestContext.Current.CancellationToken);
            var coachCProfile = await db.ProfessionalProfiles.FirstAsync(
                pp => pp.UserId == coachC.UserId, TestContext.Current.CancellationToken);
            db.ClientRequests.Add(new ClientRequest
            {
                ClientProfileId = clientProfile.Id,
                ProfessionalProfileId = coachCProfile.Id,
                Status = ClientRequestStatus.Pending,
                SentAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        (await ConversationExistsAsync(coachC.UserId, client.UserId)).Should().BeFalse(
            "coach C's request was inserted directly — no thread exists yet");

        var acceptResponse = await coachA.Http.PostAsJsonAsync(
            $"/trainer/client-requests/{requestToA}/accept",
            new { }, TestContext.Current.CancellationToken);
        acceptResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var messagesWithB = await GetMessagesChronologicalAsync(client.Http, conversationWithB!.Value);
        messagesWithB.Should().HaveCount(2, "Requested event plus the sibling-auto-cancel Withdrawn event");
        messagesWithB[1].Kind.Should().Be(ChatMessageKind.Event);
        messagesWithB[1].EventType.Should().Be(ChatEventType.Withdrawn);
        messagesWithB[1].SenderId.Should().Be(client.UserId);

        (await ConversationExistsAsync(coachC.UserId, client.UserId)).Should().BeFalse(
            "coach C never had a thread — the sibling-auto-cancel Withdrawn append must not create one (R3)");
    }

    [Fact]
    public async Task Accept_AlreadyProcessedRequest_Returns400_WritesNoNewRows()
    {
        var coach = await TestActors.Trainer(factory).WithName("Coach", "Carl").CreateAsync();
        var client = await TestActors.Client(factory).WithName("Jane", "Doe").CreateAsync();

        var requestPublicId = await SendRequestAsync(client.Http, coach.PublicId, message: null);
        var conversationId = await FindConversationIdAsync(client.Http, coach.UserId);

        var firstAccept = await coach.Http.PostAsJsonAsync(
            $"/trainer/client-requests/{requestPublicId}/accept",
            new { }, TestContext.Current.CancellationToken);
        firstAccept.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var retryAccept = await coach.Http.PostAsJsonAsync(
            $"/trainer/client-requests/{requestPublicId}/accept",
            new { }, TestContext.Current.CancellationToken);
        retryAccept.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "the request's own Status != Pending guard rejects a re-processed accept before the cooperation-event seam");

        var messages = await GetMessagesChronologicalAsync(client.Http, conversationId!.Value);
        messages.Should().HaveCount(2, "the retried accept must not add a second Accepted row");
    }

    [Fact]
    public async Task Reject_AlreadyProcessedRequest_Returns400_WritesNoNewRows()
    {
        var coach = await TestActors.Trainer(factory).WithName("Coach", "Carl").CreateAsync();
        var client = await TestActors.Client(factory).WithName("Jane", "Doe").CreateAsync();

        var requestPublicId = await SendRequestAsync(client.Http, coach.PublicId, message: null);
        var conversationId = await FindConversationIdAsync(client.Http, coach.UserId);

        var firstReject = await coach.Http.PostAsJsonAsync(
            $"/trainer/client-requests/{requestPublicId}/reject",
            new { }, TestContext.Current.CancellationToken);
        firstReject.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var retryReject = await coach.Http.PostAsJsonAsync(
            $"/trainer/client-requests/{requestPublicId}/reject",
            new { }, TestContext.Current.CancellationToken);
        retryReject.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "the request's own Status != Pending guard rejects a re-processed reject before the cooperation-event seam");

        var messages = await GetMessagesChronologicalAsync(client.Http, conversationId!.Value);
        messages.Should().HaveCount(2, "the retried reject must not add a second Declined row");
    }

    [Fact]
    public async Task SendMessage_AfterDecline_Returns200_ForBothParticipants()
    {
        var coach = await TestActors.Trainer(factory).WithName("Coach", "Carl").CreateAsync();
        var client = await TestActors.Client(factory).WithName("Jane", "Doe").CreateAsync();

        var requestPublicId = await SendRequestAsync(client.Http, coach.PublicId, message: null);
        var conversationId = await FindConversationIdAsync(client.Http, coach.UserId);

        var rejectResponse = await coach.Http.PostAsJsonAsync(
            $"/trainer/client-requests/{requestPublicId}/reject",
            new { }, TestContext.Current.CancellationToken);
        rejectResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var clientSend = await client.Http.PostAsJsonAsync(
            $"/conversations/{conversationId}/messages",
            new { ConversationId = conversationId, Text = "Still there?" },
            TestContext.Current.CancellationToken);
        clientSend.StatusCode.Should().Be(HttpStatusCode.OK,
            "a declined thread must still accept plain messages from the client");

        var coachSend = await coach.Http.PostAsJsonAsync(
            $"/conversations/{conversationId}/messages",
            new { ConversationId = conversationId, Text = "Sorry, not right now." },
            TestContext.Current.CancellationToken);
        coachSend.StatusCode.Should().Be(HttpStatusCode.OK,
            "a declined thread must still accept plain messages from the coach");
    }

    /// <summary>
    /// Forces <c>AcceptClientRequestEndpoint</c>'s own <c>AppendCooperationEventAsync(Accepted, …)</c>
    /// call to hit the real partial-unique-index 23505 no-op, by writing that exact row through the
    /// seam directly beforehand. Proves the endpoint still returns 204 and still creates the link —
    /// see the class remarks for why the transaction the #1009 slot lock opens cannot be aborted by
    /// this: the seam call runs after that transaction's own commit.
    /// </summary>
    [Fact]
    public async Task Accept_EventAppendNoOps_StillReturns204_AndCreatesLink()
    {
        var coach = await TestActors.Trainer(factory).WithName("Coach", "Carl").CreateAsync();
        var client = await TestActors.Client(factory).WithName("Jane", "Doe").CreateAsync();

        var requestPublicId = await SendRequestAsync(client.Http, coach.PublicId, message: null);

        using (var scope = factory.Services.CreateScope())
        {
            var seedService = scope.ServiceProvider.GetRequiredService<IConversationSeedService>();
            await seedService.AppendCooperationEventAsync(
                coach.UserId, client.UserId, coach.UserId, ChatEventType.Accepted,
                requestPublicId, messageText: null, createConversationIfMissing: true,
                TestContext.Current.CancellationToken);
        }

        var acceptResponse = await coach.Http.PostAsJsonAsync(
            $"/trainer/client-requests/{requestPublicId}/accept",
            new { }, TestContext.Current.CancellationToken);
        acceptResponse.StatusCode.Should().Be(HttpStatusCode.NoContent,
            "the endpoint's own seam call no-ops on the real 23505, but the link save already committed beforehand");

        using var verifyScope = factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var clientProfile = await db.ClientProfiles.FirstAsync(
            cp => cp.UserId == client.UserId, TestContext.Current.CancellationToken);
        var coachProfile = await db.ProfessionalProfiles.FirstAsync(
            pp => pp.UserId == coach.UserId, TestContext.Current.CancellationToken);
        var linkExists = await db.ClientProfessionalLinks.AnyAsync(
            l => l.ClientProfileId == clientProfile.Id && l.ProfessionalProfileId == coachProfile.Id,
            TestContext.Current.CancellationToken);
        linkExists.Should().BeTrue("the link save is unaffected by the seam's internal no-op");

        var conversation = await db.Conversations.FirstAsync(
            c => c.ProfessionalUserId == coach.UserId && c.ClientUserId == client.UserId,
            TestContext.Current.CancellationToken);
        var acceptedRowCount = await db.ChatMessages.CountAsync(
            m => m.ConversationId == conversation.Id && m.EventType == ChatEventType.Accepted,
            TestContext.Current.CancellationToken);
        acceptedRowCount.Should().Be(1, "the endpoint's own append must not duplicate the pre-existing Accepted row");
    }

    private record SendRequestResult(Guid PublicId);
    private record ConversationResult(Guid? Id, ParticipantResult Participant);
    private record ParticipantResult(Guid Id, string Name);
    private record GetMessagesResult(List<MessageResult> Items, Guid? Cursor);

    private record MessageResult(
        Guid Id, Guid SenderId, string Text, DateTime Timestamp, bool IsRead,
        ChatMessageKind Kind, ChatEventType? EventType);
}
