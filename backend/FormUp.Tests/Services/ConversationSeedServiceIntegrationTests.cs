using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Services;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace FormUp.Tests.Services;

/// <summary>
/// Real-Postgres coverage for <see cref="ConversationSeedService.AppendCooperationEventAsync"/>
/// (#1100 fix round) — behaviour that mocked <c>DbSet</c>s cannot prove: cross-row
/// <c>DateCreated</c> ordering within one <c>SaveChangesAsync</c> batch, the partial unique
/// index's actual enforcement (and its <c>WHERE event_source_id IS NOT NULL</c> scope), and
/// that a rolled-back duplicate leaves the context's change tracker clean enough for the
/// caller's NEXT unrelated save to succeed.
/// </summary>
[Collection(TestCollection.Shard3)]
public class ConversationSeedServiceIntegrationTests(FitnessApiFactory factory)
{
    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.com";

    /// <summary>
    /// Builds a <see cref="ConversationSeedService"/> against the real <see cref="ApplicationDbContext"/>
    /// resolved from <paramref name="scope"/>, with a substituted (never a real SignalR hub)
    /// <see cref="IRealtimeNotifier"/> — this suite's subject is persistence, not broadcast.
    /// </summary>
    private static (ApplicationDbContext Db, ConversationSeedService Service) CreateService(IServiceScope scope)
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var service = new ConversationSeedService(db, Substitute.For<IRealtimeNotifier>());
        return (db, service);
    }

    [Fact]
    public async Task AppendCooperationEvent_EventAndMessage_EventSortsBeforeMessageByDateCreatedThenId()
    {
        var ct = TestContext.Current.CancellationToken;

        var trainer = await TestActors.Trainer(factory).WithEmail(UniqueEmail("order-trainer")).CreateAsync(ct);
        var client = await TestActors.Client(factory).WithEmail(UniqueEmail("order-client")).CreateAsync(ct);

        using var scope = factory.Services.CreateScope();
        var (db, service) = CreateService(scope);

        await service.AppendCooperationEventAsync(
            trainer.UserId, client.UserId, trainer.UserId, ChatEventType.Invited, Guid.NewGuid(),
            "Welcome!", createConversationIfMissing: true, ct);

        // Same sort key GetMessagesEndpoint queries by — (DateCreated, Id) — applied ascending
        // (the chronological reading direction; GetMessagesEndpoint itself pages newest-first
        // for cursor pagination, but the underlying ordering key is identical). "Sorts before"
        // means chronologically earlier: the event was created first, so it comes first here.
        var rows = await db.ChatMessages
            .AsNoTracking()
            .Where(m => m.Conversation.ProfessionalUserId == trainer.UserId
                        && m.Conversation.ClientUserId == client.UserId)
            .OrderBy(m => m.DateCreated)
            .ThenBy(m => m.Id)
            .Select(m => m.Kind)
            .ToListAsync(ct);

        rows.Should().Equal([ChatMessageKind.Event, ChatMessageKind.Text],
            "the event row must deterministically precede the message row in (DateCreated, Id) " +
            "order — ApplicationDbContext.ApplyTimestamps guarantees a strictly increasing " +
            "DateCreated across rows added within one SaveChangesAsync batch specifically so this " +
            "does not depend on the system clock's resolution");

        // Strict DateCreated comparison, re-read fresh from Postgres via AsNoTracking (not the
        // in-memory tracked entities, which would still carry the pre-truncation tick-bumped
        // value and mask a bug where Postgres' microsecond column precision rounds a smaller
        // bump away on write). The (Kind, Id) tiebreak above proves ordering; this proves the
        // ordering is genuinely carried by DateCreated itself, not falling back to Id.
        var eventDateCreated = await db.ChatMessages
            .AsNoTracking()
            .Where(m => m.Conversation.ProfessionalUserId == trainer.UserId
                        && m.Conversation.ClientUserId == client.UserId
                        && m.Kind == ChatMessageKind.Event)
            .Select(m => m.DateCreated)
            .SingleAsync(ct);

        var messageDateCreated = await db.ChatMessages
            .AsNoTracking()
            .Where(m => m.Conversation.ProfessionalUserId == trainer.UserId
                        && m.Conversation.ClientUserId == client.UserId
                        && m.Kind == ChatMessageKind.Text)
            .Select(m => m.DateCreated)
            .SingleAsync(ct);

        eventDateCreated.Should().BeBefore(messageDateCreated,
            "ApplyTimestamps bumps the tie by a whole microsecond (Postgres' timestamp column " +
            "precision) — a single-tick bump would round away on write and the two rows would " +
            "come back with an IDENTICAL DateCreated, ordering resting on Id alone");
    }

    /// <summary>
    /// #1108 review: the second call is now caught by the existence pre-check (a plain
    /// SELECT, no exception, no error log) rather than the 23505-driven rollback path —
    /// that path stays covered at the mock layer
    /// (<c>ConversationSeedServiceTests.AppendCooperationEvent_DuplicateSource_UniqueViolation_UntracksLosersAndRevertsLastMessage</c>),
    /// where a forced throw can exercise it deterministically. This test still proves the
    /// real Postgres round trip is idempotent end to end.
    /// </summary>
    [Fact]
    public async Task AppendCooperationEvent_DuplicateSource_ExactlyOneRowPair_SubsequentSaveSucceeds_LastMessageUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;

        var trainer = await TestActors.Trainer(factory).WithEmail(UniqueEmail("dup-trainer")).CreateAsync(ct);
        var client = await TestActors.Client(factory).WithEmail(UniqueEmail("dup-client")).CreateAsync(ct);
        var sourceId = Guid.NewGuid();

        using var scope = factory.Services.CreateScope();
        var (db, service) = CreateService(scope);

        await service.AppendCooperationEventAsync(
            trainer.UserId, client.UserId, trainer.UserId, ChatEventType.Invited, sourceId,
            "First message", createConversationIfMissing: true, ct);

        var conversationAfterFirst = await db.Conversations
            .AsNoTracking()
            .FirstAsync(c => c.ProfessionalUserId == trainer.UserId && c.ClientUserId == client.UserId, ct);

        // Re-processed: same conversation, same eventType, same sourceId. Must be a no-op —
        // now via the pre-check, not the unique-index exception.
        await service.AppendCooperationEventAsync(
            trainer.UserId, client.UserId, trainer.UserId, ChatEventType.Invited, sourceId,
            "First message", createConversationIfMissing: true, ct);

        var messageCount = await db.ChatMessages
            .AsNoTracking()
            .CountAsync(m => m.Conversation.ProfessionalUserId == trainer.UserId
                              && m.Conversation.ClientUserId == client.UserId, ct);
        messageCount.Should().Be(2, "exactly one event row and one text row — the duplicate append must not add a second pair");

        // Proves the untrack fix: the SAME context's next SaveChangesAsync (the position the
        // real caller is in — e.g. AcceptClientInviteEndpoint saving the accepted link right
        // after this call) must succeed, not re-throw the same 23505 for the rows the duplicate
        // attempt left half-tracked.
        var subsequentSave = async () => await db.SaveChangesAsync(ct);
        await subsequentSave.Should().NotThrowAsync(
            "the duplicate attempt's losing rows must be untracked, not left Added for this " +
            "context's next save to re-INSERT");

        var conversationAfterDuplicate = await db.Conversations
            .AsNoTracking()
            .FirstAsync(c => c.ProfessionalUserId == trainer.UserId && c.ClientUserId == client.UserId, ct);

        conversationAfterDuplicate.LastMessageText.Should().Be(conversationAfterFirst.LastMessageText);
        conversationAfterDuplicate.LastMessageAt.Should().Be(conversationAfterFirst.LastMessageAt);
        conversationAfterDuplicate.LastMessageSenderId.Should().Be(conversationAfterFirst.LastMessageSenderId);
        conversationAfterDuplicate.LastMessageHasImage.Should().Be(conversationAfterFirst.LastMessageHasImage);
        conversationAfterDuplicate.LastMessageEventType.Should().Be(conversationAfterFirst.LastMessageEventType);
    }

    [Fact]
    public async Task AppendCooperationEvent_NullSourceId_PartialIndexDoesNotApply_BothEventsPersist()
    {
        var ct = TestContext.Current.CancellationToken;

        var trainer = await TestActors.Trainer(factory).WithEmail(UniqueEmail("nullsrc-trainer")).CreateAsync(ct);
        var client = await TestActors.Client(factory).WithEmail(UniqueEmail("nullsrc-client")).CreateAsync(ct);

        using var scope = factory.Services.CreateScope();
        var (db, service) = CreateService(scope);

        // Withdrawn is written with sourceId: null for token-only invites — see RULING (9).
        await service.AppendCooperationEventAsync(
            trainer.UserId, client.UserId, trainer.UserId, ChatEventType.Withdrawn, sourceId: null,
            messageText: null, createConversationIfMissing: true, ct);

        await service.AppendCooperationEventAsync(
            trainer.UserId, client.UserId, trainer.UserId, ChatEventType.Withdrawn, sourceId: null,
            messageText: null, createConversationIfMissing: true, ct);

        var nullSourceEventCount = await db.ChatMessages
            .AsNoTracking()
            .CountAsync(m => m.Conversation.ProfessionalUserId == trainer.UserId
                              && m.Conversation.ClientUserId == client.UserId
                              && m.Kind == ChatMessageKind.Event
                              && m.EventType == ChatEventType.Withdrawn
                              && m.EventSourceId == null, ct);

        nullSourceEventCount.Should().Be(2,
            "the partial unique index has a WHERE event_source_id IS NOT NULL filter, so two " +
            "NULL-source events of the same type on the same conversation must NOT collide");
    }
}
