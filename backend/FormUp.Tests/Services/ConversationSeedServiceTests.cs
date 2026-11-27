using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Services;
using FormUp.Tests.Builders;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MockQueryable.NSubstitute;
using Npgsql;
using NSubstitute;

namespace FormUp.Tests.Services;

/// <summary>
/// Unit tests for <see cref="ConversationSeedService"/> — the shared
/// get-or-create-conversation + seed-first-message helper extracted for #768
/// (invite messages not surfacing as a chat conversation), plus its
/// <see cref="ConversationSeedService.AppendCooperationEventAsync"/> sibling for
/// system-generated cooperation events (#1100).
/// No Docker required — the Postgres DbSets are mocked.
/// </summary>
public class ConversationSeedServiceTests
{
    private readonly IRealtimeNotifier _notifier = Substitute.For<IRealtimeNotifier>();
    private readonly INotificationService _notificationService = Substitute.For<INotificationService>();

    /// <summary>Real <see cref="ChatMessageNotifier"/> over the substituted transports.</summary>
    private ConversationSeedService NewService(IApplicationDbContext db) =>
        new(db, new ChatMessageNotifier(
            _notifier, _notificationService, new PresenceTracker(), NullLogger<ChatMessageNotifier>.Instance));

    private static ApplicationUser MakeUser(Guid id, string firstName, string lastName, string? language = "en") =>
        new()
        {
            Id = id,
            FirstName = firstName,
            LastName = lastName,
            Language = language,
        };

    [Fact]
    public async Task NewConversation_WithMessage_CreatesConversationAndSeedsMessage()
    {
        var professionalId = Guid.NewGuid();
        var clientId = Guid.NewGuid();

        var db = new MockDbBuilder().Build();
        var service = NewService(db);

        var conversation = await service.GetOrSeedConversationAsync(
            professionalId, clientId, professionalId, "Coach Carl", "Welcome aboard!",
            seedIntoExisting: false, TestContext.Current.CancellationToken);

        conversation.ProfessionalUserId.Should().Be(professionalId);
        conversation.ClientUserId.Should().Be(clientId);
        conversation.LastMessageText.Should().Be("Welcome aboard!");
        conversation.LastMessageSenderId.Should().Be(professionalId);

        db.Conversations.Received(1).Add(Arg.Any<Conversation>());
        // FIX 1 regression guard: the message must be built via the Conversation
        // navigation property (ConversationId is only assigned by EF on save), not
        // a pre-save ConversationId — and both inserts happen in a single save.
        db.ChatMessages.Received(1).Add(Arg.Is<ChatMessage>(m =>
            m.SenderUserId == professionalId && m.Text == "Welcome aboard!" && m.Conversation == conversation));
        await db.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());

        await _notifier.Received(1).NotifyAsync(
            clientId, "newmessage", Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NewConversation_WithoutMessage_CreatesEmptyConversationShellOnly()
    {
        var professionalId = Guid.NewGuid();
        var clientId = Guid.NewGuid();

        var db = new MockDbBuilder().Build();
        var service = NewService(db);

        var conversation = await service.GetOrSeedConversationAsync(
            professionalId, clientId, professionalId, "Coach Carl", null,
            seedIntoExisting: false, TestContext.Current.CancellationToken);

        conversation.ProfessionalUserId.Should().Be(professionalId);
        db.Conversations.Received(1).Add(Arg.Any<Conversation>());
        db.ChatMessages.DidNotReceive().Add(Arg.Any<ChatMessage>());
        await _notifier.DidNotReceiveWithAnyArgs().NotifyAsync(default, default!, default!, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Idempotency guard for invite-ACCEPT callers (#768, seedIntoExisting: false):
    /// if the conversation already exists — e.g. it was already seeded once at
    /// invite-creation time for an invitee who already had an account — a second
    /// call from the accept-time flow must NOT duplicate the message or
    /// re-broadcast "newmessage".
    /// </summary>
    [Fact]
    public async Task ExistingConversation_SeedIntoExistingFalse_DoesNotDuplicateSeedMessage()
    {
        var professionalId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var existingConversation = new Conversation
        {
            ProfessionalUserId = professionalId,
            ClientUserId = clientId,
            LastMessageText = "Already delivered",
        };

        var db = new MockDbBuilder().With(existingConversation).Build();
        var service = NewService(db);

        var conversation = await service.GetOrSeedConversationAsync(
            professionalId, clientId, professionalId, "Coach Carl", "Welcome aboard!",
            seedIntoExisting: false, TestContext.Current.CancellationToken);

        conversation.Should().BeSameAs(existingConversation);
        // Preview fields from the earlier seed must be untouched — no duplicate write.
        conversation.LastMessageText.Should().Be("Already delivered");

        db.Conversations.DidNotReceive().Add(Arg.Any<Conversation>());
        db.ChatMessages.DidNotReceive().Add(Arg.Any<ChatMessage>());
        await db.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        await _notifier.DidNotReceiveWithAnyArgs().NotifyAsync(default, default!, default!, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// FIX 3 (#768 review) — invite-CREATION callers (seedIntoExisting: true) must
    /// restore the pre-extraction behavior: append the message even when the two
    /// participants already have a conversation, so re-inviting an already-conversing
    /// contact with a personal message still delivers it instead of silently dropping it.
    /// </summary>
    [Fact]
    public async Task ExistingConversation_SeedIntoExistingTrue_AppendsMessage()
    {
        var professionalId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var existingConversation = new Conversation
        {
            ProfessionalUserId = professionalId,
            ClientUserId = clientId,
            LastMessageText = "Earlier chat",
        };

        var db = new MockDbBuilder().With(existingConversation).Build();
        var service = NewService(db);

        var conversation = await service.GetOrSeedConversationAsync(
            professionalId, clientId, professionalId, "Coach Carl", "Great to reconnect!",
            seedIntoExisting: true, TestContext.Current.CancellationToken);

        conversation.Should().BeSameAs(existingConversation);
        conversation.LastMessageText.Should().Be("Great to reconnect!");

        db.Conversations.DidNotReceive().Add(Arg.Any<Conversation>());
        db.ChatMessages.Received(1).Add(Arg.Is<ChatMessage>(m =>
            m.SenderUserId == professionalId && m.Text == "Great to reconnect!" && m.Conversation == existingConversation));
        await db.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _notifier.Received(1).NotifyAsync(
            clientId, "newmessage", Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// FIX 2 (#768 review) — a concurrent double-accept/double-invite race can make
    /// two requests both observe "no conversation yet" and both attempt the insert.
    /// The unique index on (ProfessionalUserId, ClientUserId) lets only one win; the
    /// loser must catch the resulting DbUpdateException/unique-violation, re-query the
    /// winner's row, and return it as a no-op — never an unhandled 500.
    /// </summary>
    [Fact]
    public async Task ConcurrentInsert_UniqueViolation_ReQueriesWinnerInsteadOfThrowing()
    {
        var professionalId = Guid.NewGuid();
        var clientId = Guid.NewGuid();

        // Build mock DbSets BEFORE configuring Returns (see MockDbBuilder's note on
        // NSubstitute's "substitute inside Returns()" pitfall — BuildMockDbSet()
        // itself creates a substitute, which resets NSubstitute's "last call" context
        // if invoked inline as a Returns() argument).
        var conversationsList = new List<Conversation>();
        var conversationsSet = conversationsList.BuildMockDbSet();
        var chatMessagesSet = new List<ChatMessage>().BuildMockDbSet();

        var db = Substitute.For<IApplicationDbContext>();
        db.Conversations.Returns(conversationsSet);
        db.ChatMessages.Returns(chatMessagesSet);

        db.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns<int>(_ =>
        {
            // Simulate the concurrent winner's transaction committing its row first,
            // then this request's own INSERT hitting the unique-index conflict.
            conversationsList.Add(new Conversation
            {
                ProfessionalUserId = professionalId,
                ClientUserId = clientId,
                LastMessageText = "Concurrent winner's message",
            });

            var pgEx = new PostgresException("duplicate key value violates unique constraint", "ERROR", "ERROR", "23505");
            throw new DbUpdateException("conflict", pgEx);
        });

        var service = NewService(db);

        var conversation = await service.GetOrSeedConversationAsync(
            professionalId, clientId, professionalId, "Coach Carl", "Welcome aboard!",
            seedIntoExisting: false, TestContext.Current.CancellationToken);

        // No exception propagated — the loser resolves to the winner's row.
        conversation.LastMessageText.Should().Be("Concurrent winner's message");
        await _notifier.DidNotReceiveWithAnyArgs().NotifyAsync(default, default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AppendCooperationEvent_NoExistingConversation_CreateIfMissingTrue_CreatesThreadWithEventAndMessage()
    {
        var professionalId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var professional = MakeUser(professionalId, "Coach", "Carl");
        var client = MakeUser(clientId, "Jane", "Client", "en");

        var db = new MockDbBuilder().With(professional).With(client).Build();
        var service = NewService(db);

        await service.AppendCooperationEventAsync(
            professionalId, clientId, professionalId, ChatEventType.Invited, sourceId,
            "Looking forward to working together!", createConversationIfMissing: true,
            TestContext.Current.CancellationToken);

        db.Conversations.Received(1).Add(Arg.Any<Conversation>());
        db.ChatMessages.Received(1).Add(Arg.Is<ChatMessage>(m =>
            m.Kind == ChatMessageKind.Event &&
            m.EventType == ChatEventType.Invited &&
            m.EventSourceId == sourceId &&
            m.SenderUserId == professionalId &&
            m.Text.Contains("Coach Carl")));
        db.ChatMessages.Received(1).Add(Arg.Is<ChatMessage>(m =>
            m.Kind == ChatMessageKind.Text &&
            m.EventType == null &&
            m.SenderUserId == professionalId &&
            m.Text == "Looking forward to working together!"));
        await db.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _notifier.Received(1).NotifyAsync(
            clientId, "newmessage", Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// RULING R3 — a neutral "withdrawn" event must never create a thread just to
    /// announce there is nothing to see: when the two participants have no
    /// conversation yet and <c>createConversationIfMissing</c> is false, the call is a
    /// complete no-op.
    /// </summary>
    [Fact]
    public async Task AppendCooperationEvent_NoExistingConversation_CreateIfMissingFalse_IsNoOp()
    {
        var professionalId = Guid.NewGuid();
        var clientId = Guid.NewGuid();

        var db = new MockDbBuilder().Build();
        var service = NewService(db);

        await service.AppendCooperationEventAsync(
            professionalId, clientId, professionalId, ChatEventType.Withdrawn, Guid.NewGuid(),
            messageText: null, createConversationIfMissing: false,
            TestContext.Current.CancellationToken);

        db.Conversations.DidNotReceive().Add(Arg.Any<Conversation>());
        db.ChatMessages.DidNotReceive().Add(Arg.Any<ChatMessage>());
        await db.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        await _notifier.DidNotReceiveWithAnyArgs().NotifyAsync(default, default!, default!, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The event row must be added to the change tracker before the message row — within
    /// a single <c>SaveChangesAsync</c> batch, Postgres assigns identity values in
    /// statement order, so this ordering is what makes the event deterministically sort
    /// before the message in <c>GetMessages</c>' (DateCreated, Id) order.
    /// </summary>
    [Fact]
    public async Task AppendCooperationEvent_EventAndMessage_EventAddedBeforeMessage()
    {
        var professionalId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var professional = MakeUser(professionalId, "Coach", "Carl");
        var client = MakeUser(clientId, "Jane", "Client");

        var db = new MockDbBuilder().With(professional).With(client).Build();
        var service = NewService(db);

        await service.AppendCooperationEventAsync(
            professionalId, clientId, professionalId, ChatEventType.Invited, Guid.NewGuid(),
            "Hi there!", createConversationIfMissing: true,
            TestContext.Current.CancellationToken);

        var addedKinds = db.ChatMessages.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(db.ChatMessages.Add))
            .Select(call => ((ChatMessage)call.GetArguments()[0]!).Kind)
            .ToList();

        addedKinds.Should().Equal(ChatMessageKind.Event, ChatMessageKind.Text);
    }

    [Fact]
    public async Task AppendCooperationEvent_BlankMessage_WritesEventRowOnly_AndSetsLastMessageEventType()
    {
        var professionalId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var professional = MakeUser(professionalId, "Coach", "Carl");
        var client = MakeUser(clientId, "Jane", "Client");

        var db = new MockDbBuilder().With(professional).With(client).Build();
        var service = NewService(db);

        await service.AppendCooperationEventAsync(
            professionalId, clientId, professionalId, ChatEventType.Withdrawn, Guid.NewGuid(),
            messageText: "   ", createConversationIfMissing: true,
            TestContext.Current.CancellationToken);

        db.ChatMessages.Received(1).Add(Arg.Any<ChatMessage>());
        db.ChatMessages.Received(1).Add(Arg.Is<ChatMessage>(m => m.Kind == ChatMessageKind.Event));
        await db.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _notifier.Received(1).NotifyAsync(
            clientId, "newmessage", Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// #1108 review — the pre-check added to avoid the 23505-driven EF error log on the
    /// happy-path no-op (VerifyEmail's seed, then CreatePendingInvite's or
    /// AcceptInvitation's own "ensure Invited" call, all targeting the same sourceId):
    /// a matching (conversation, eventType, sourceId) row already exists, so the call must
    /// return before adding anything, saving, or broadcasting.
    /// </summary>
    [Fact]
    public async Task AppendCooperationEvent_SourceIdAlreadyRecorded_IsNoOp_NoAddNoSaveNoBroadcast()
    {
        var professionalId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var professional = MakeUser(professionalId, "Coach", "Carl");
        var client = MakeUser(clientId, "Jane", "Client");
        var existingConversation = new Conversation
        {
            ProfessionalUserId = professionalId,
            ClientUserId = clientId,
            LastMessageText = "Already recorded",
        };
        var existingEvent = new ChatMessage
        {
            ConversationId = existingConversation.Id,
            SenderUserId = professionalId,
            Kind = ChatMessageKind.Event,
            EventType = ChatEventType.Invited,
            EventSourceId = sourceId,
            Text = "Already recorded",
        };

        var db = new MockDbBuilder()
            .With(professional)
            .With(client)
            .With(existingConversation)
            .With(existingEvent)
            .Build();
        var service = NewService(db);

        await service.AppendCooperationEventAsync(
            professionalId, clientId, professionalId, ChatEventType.Invited, sourceId,
            messageText: "Looking forward to working together!", createConversationIfMissing: true,
            TestContext.Current.CancellationToken);

        db.ChatMessages.DidNotReceive().Add(Arg.Any<ChatMessage>());
        await db.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        await _notifier.DidNotReceiveWithAnyArgs().NotifyAsync(default, default!, default!, TestContext.Current.CancellationToken);
        existingConversation.LastMessageText.Should().Be("Already recorded");
    }

    /// <summary>
    /// RULING (8) idempotency — a re-processed event for the same
    /// (conversation, eventType, sourceId) hits the partial unique index on
    /// chat_messages. The whole batch (event row, plus a brand-new conversation shell)
    /// rolls back together and no broadcast is sent.
    /// </summary>
    /// <summary>
    /// Fix-round regression guard (coordinator review after the initial C2 handoff): the
    /// catch block must untrack its losing Added rows — otherwise they survive in the
    /// change tracker and the CALLER's next unrelated SaveChangesAsync (e.g. the invite
    /// endpoint saving the accepted link right after this call) re-issues the same
    /// INSERTs and hits 23505 again, a 500 for an operation unrelated to this event. It
    /// must also revert the EXISTING conversation's in-memory LastMessage* mutations,
    /// which were never persisted (the whole batch rolled back) — otherwise a later
    /// unrelated save on this context silently commits a phantom preview.
    /// </summary>
    [Fact]
    public async Task AppendCooperationEvent_DuplicateSource_UniqueViolation_UntracksLosersAndRevertsLastMessage()
    {
        var professionalId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var professional = MakeUser(professionalId, "Coach", "Carl");
        var client = MakeUser(clientId, "Jane", "Client");
        var existingConversation = new Conversation
        {
            ProfessionalUserId = professionalId,
            ClientUserId = clientId,
            LastMessageText = "Earlier chat",
            LastMessageAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            LastMessageSenderId = clientId,
            LastMessageHasImage = true,
            LastMessageEventType = null,
        };

        // Build mock DbSets BEFORE configuring Returns (MockDbBuilder's own note on
        // NSubstitute's "substitute inside Returns()" pitfall).
        var usersSet = new List<ApplicationUser> { professional, client }.BuildMockDbSet();
        var conversationsSet = new List<Conversation> { existingConversation }.BuildMockDbSet();
        var chatMessagesSet = new List<ChatMessage>().BuildMockDbSet();

        var db = Substitute.For<IApplicationDbContext>();
        db.Users.Returns(usersSet);
        db.Conversations.Returns(conversationsSet);
        db.ChatMessages.Returns(chatMessagesSet);

        // Constraint name matches the chat_messages partial unique index — not the
        // conversations identity index — so this is the "duplicate event" branch, not
        // the "concurrent first-contact" retry branch.
        var pgEx = new PostgresException(
            "duplicate key value violates unique constraint", "ERROR", "ERROR", "23505",
            constraintName: "ix_chat_messages_conversation_id_event_type_event_source_id");
        db.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns<int>(_ => throw new DbUpdateException("conflict", pgEx));

        var service = NewService(db);

        await service.AppendCooperationEventAsync(
            professionalId, clientId, professionalId, ChatEventType.Accepted, Guid.NewGuid(),
            messageText: "Glad to have you!", createConversationIfMissing: true,
            TestContext.Current.CancellationToken);

        // No exception propagated, and no broadcast — the whole batch rolled back.
        await _notifier.DidNotReceiveWithAnyArgs().NotifyAsync(default, default!, default!, TestContext.Current.CancellationToken);

        // The losing event row AND its message row must be untracked so a later save
        // on this context can't re-attempt their INSERTs.
        db.ChatMessages.Received(1).Remove(Arg.Is<ChatMessage>(m => m.Kind == ChatMessageKind.Event));
        db.ChatMessages.Received(1).Remove(Arg.Is<ChatMessage>(m => m.Kind == ChatMessageKind.Text));

        // The existing conversation's LastMessage* must read exactly as it did before
        // this call ever touched it — not the phantom values this call computed.
        existingConversation.LastMessageText.Should().Be("Earlier chat");
        existingConversation.LastMessageAt.Should().Be(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        existingConversation.LastMessageSenderId.Should().Be(clientId);
        existingConversation.LastMessageHasImage.Should().BeTrue();
        existingConversation.LastMessageEventType.Should().BeNull();
    }

    [Fact]
    public async Task AppendCooperationEvent_ExistingConversation_SetsLastMessageFields_HasImageFalseAndEventTypeSet()
    {
        var professionalId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var professional = MakeUser(professionalId, "Coach", "Carl");
        var client = MakeUser(clientId, "Jane", "Client");
        var existingConversation = new Conversation
        {
            ProfessionalUserId = professionalId,
            ClientUserId = clientId,
            LastMessageHasImage = true,
            LastMessageText = "Earlier chat",
        };

        var db = new MockDbBuilder().With(professional).With(client).With(existingConversation).Build();
        var service = NewService(db);

        await service.AppendCooperationEventAsync(
            professionalId, clientId, clientId, ChatEventType.Requested, Guid.NewGuid(),
            messageText: null, createConversationIfMissing: true,
            TestContext.Current.CancellationToken);

        existingConversation.LastMessageHasImage.Should().BeFalse();
        existingConversation.LastMessageEventType.Should().Be(ChatEventType.Requested);
        existingConversation.LastMessageSenderId.Should().Be(clientId);
        db.Conversations.DidNotReceive().Add(Arg.Any<Conversation>());
        await _notifier.Received(1).NotifyAsync(
            professionalId, "newmessage", Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Wire-shape regression guard: the SignalR hub's JSON protocol does not share the
    /// REST pipeline's <c>JsonStringEnumConverter</c> (Program.cs), so an unconverted enum
    /// serializes as an integer over the hub while REST always sends the string name. The
    /// web client's <c>NewMessagePayload</c> (InboxPage.tsx) types both fields as strings —
    /// this must hold even though the payload itself is an untyped anonymous object.
    /// </summary>
    [Fact]
    public async Task AppendCooperationEvent_Broadcast_SerializesKindAndEventTypeAsStrings()
    {
        var professionalId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var professional = MakeUser(professionalId, "Coach", "Carl");
        var client = MakeUser(clientId, "Jane", "Client");

        var db = new MockDbBuilder().With(professional).With(client).Build();
        var service = NewService(db);

        await service.AppendCooperationEventAsync(
            professionalId, clientId, professionalId, ChatEventType.Declined, Guid.NewGuid(),
            messageText: null, createConversationIfMissing: true,
            TestContext.Current.CancellationToken);

        var call = _notifier.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(_notifier.NotifyAsync));
        var payload = call.GetArguments()[2]!;
        var payloadType = payload.GetType();

        var kind = payloadType.GetProperty("kind")!.GetValue(payload);
        var eventType = payloadType.GetProperty("eventType")!.GetValue(payload);

        kind.Should().BeOfType<string>();
        kind.Should().Be(nameof(ChatMessageKind.Event));
        eventType.Should().BeOfType<string>();
        eventType.Should().Be(nameof(ChatEventType.Declined));
    }

    /// <summary>
    /// Pins the default: every caller except Accept/Reject keeps the companion text SignalR-only.
    /// </summary>
    [Fact]
    public async Task AppendCooperationEvent_CompanionTextWithoutPushFlag_SendsNoPush()
    {
        var professionalId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var db = new MockDbBuilder().With(MakeUser(professionalId, "Coach", "Carl")).With(MakeUser(clientId, "Jane", "Client")).Build();

        await NewService(db).AppendCooperationEventAsync(
            professionalId, clientId, professionalId, ChatEventType.Invited, Guid.NewGuid(),
            "Welcome!", createConversationIfMissing: true,
            TestContext.Current.CancellationToken);

        await _notifier.Received(1).NotifyAsync(
            clientId, "newmessage", Arg.Any<object>(), Arg.Any<CancellationToken>());
        await _notificationService.DidNotReceiveWithAnyArgs().SendPushOnlyAsync(default, default, default, default, default);
    }

    [Fact]
    public async Task AppendCooperationEvent_CompanionTextWithPushFlag_PushesTheTextOnce()
    {
        var professionalId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var db = new MockDbBuilder().With(MakeUser(professionalId, "Coach", "Carl")).With(MakeUser(clientId, "Jane", "Client")).Build();

        await NewService(db).AppendCooperationEventAsync(
            professionalId, clientId, professionalId, ChatEventType.Accepted, Guid.NewGuid(),
            "Glad to have you", createConversationIfMissing: true,
            TestContext.Current.CancellationToken, pushCompanionText: true);

        await _notificationService.Received(1).SendPushOnlyAsync(
            clientId, NotificationType.NewMessage,
            Arg.Is<IReadOnlyDictionary<string, string>>(p => p["messageText"] == "Glad to have you"),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AppendCooperationEvent_EventOnlyWithPushFlag_NeverPushes()
    {
        var professionalId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var db = new MockDbBuilder().With(MakeUser(professionalId, "Coach", "Carl")).With(MakeUser(clientId, "Jane", "Client")).Build();

        await NewService(db).AppendCooperationEventAsync(
            professionalId, clientId, professionalId, ChatEventType.Accepted, Guid.NewGuid(),
            messageText: null, createConversationIfMissing: true,
            TestContext.Current.CancellationToken, pushCompanionText: true);

        await _notifier.Received(1).NotifyAsync(
            clientId, "newmessage", Arg.Any<object>(), Arg.Any<CancellationToken>());
        await _notificationService.DidNotReceiveWithAnyArgs().SendPushOnlyAsync(default, default, default, default, default);
    }

    [Fact]
    public async Task GetOrSeedConversation_SeededText_PushesToOfflineRecipient()
    {
        var professionalId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var db = new MockDbBuilder().Build();

        await NewService(db).GetOrSeedConversationAsync(
            professionalId, clientId, professionalId, "Coach Carl", "Welcome aboard!",
            seedIntoExisting: false, TestContext.Current.CancellationToken);

        await _notificationService.Received(1).SendPushOnlyAsync(
            clientId, NotificationType.NewMessage, Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }
}
