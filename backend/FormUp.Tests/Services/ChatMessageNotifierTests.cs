using FluentAssertions;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FormUp.Tests.Services;

/// <summary>
/// Unit tests for <see cref="ChatMessageNotifier"/>: the SignalR event always fires, the push
/// only for an offline recipient of a text message, and a push failure never escapes (#1067).
/// </summary>
public class ChatMessageNotifierTests
{
    private readonly IRealtimeNotifier _realtime = Substitute.For<IRealtimeNotifier>();
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();
    private readonly PresenceTracker _presence = new();
    private readonly ILogger<ChatMessageNotifier> _logger = Substitute.For<ILogger<ChatMessageNotifier>>();
    private readonly Guid _coachId = Guid.NewGuid();
    private readonly Guid _clientId = Guid.NewGuid();
    private readonly Guid _conversationId = Guid.NewGuid();

    private ChatMessageNotifier BuildSut() => new(_realtime, _notifications, _presence, _logger);

    private ChatMessage Message(Guid senderId, string text = "Hello", ChatMessageKind kind = ChatMessageKind.Text) =>
        new() { SenderUserId = senderId, Text = text, Kind = kind };

    private Task NotifyAsync(ChatMessage message, Guid recipientId, bool sendPush = true, CancellationToken ct = default) =>
        BuildSut().NotifyNewMessageAsync(message, _conversationId, recipientId, "Sender Name", null, sendPush, ct);

    [Fact]
    public async Task Notify_CoachSendsToClient_PushesToTheClient()
    {
        await NotifyAsync(Message(_coachId), _clientId, ct: TestContext.Current.CancellationToken);

        await _realtime.Received(1).NotifyAsync(_clientId, "newmessage", Arg.Any<object>(), Arg.Any<CancellationToken>());
        await _notifications.Received(1).SendPushOnlyAsync(
            _clientId, NotificationType.NewMessage,
            Arg.Is<IReadOnlyDictionary<string, string>>(p =>
                p["senderName"] == "Sender Name"
                && p["messageText"] == "Hello"
                && p["conversationId"] == _conversationId.ToString()),
            null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Notify_ClientSendsToCoach_PushesToTheCoach()
    {
        await NotifyAsync(Message(_clientId), _coachId, ct: TestContext.Current.CancellationToken);

        await _notifications.Received(1).SendPushOnlyAsync(
            _coachId, NotificationType.NewMessage, Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Notify_RecipientOnline_SendsEventButNoPush()
    {
        _presence.UserConnected(_clientId.ToString());

        await NotifyAsync(Message(_coachId), _clientId, ct: TestContext.Current.CancellationToken);

        await _realtime.Received(1).NotifyAsync(_clientId, "newmessage", Arg.Any<object>(), Arg.Any<CancellationToken>());
        await _notifications.DidNotReceiveWithAnyArgs().SendPushOnlyAsync(default, default, default, default, default);
    }

    [Fact]
    public async Task Notify_RecipientIsTheSender_NeverPushes()
    {
        await NotifyAsync(Message(_coachId), _coachId, ct: TestContext.Current.CancellationToken);

        await _notifications.DidNotReceiveWithAnyArgs().SendPushOnlyAsync(default, default, default, default, default);
    }

    [Fact]
    public async Task Notify_EventKind_RaisesEventButNeverPushes()
    {
        var message = Message(_coachId, "Invitation sent", ChatMessageKind.Event);
        message.EventType = ChatEventType.Invited;

        await NotifyAsync(message, _clientId, ct: TestContext.Current.CancellationToken);

        await _realtime.Received(1).NotifyAsync(_clientId, "newmessage", Arg.Any<object>(), Arg.Any<CancellationToken>());
        await _notifications.DidNotReceiveWithAnyArgs().SendPushOnlyAsync(default, default, default, default, default);
    }

    [Fact]
    public async Task Notify_PushDisabledByCaller_SendsNoPush()
    {
        await NotifyAsync(Message(_coachId), _clientId, sendPush: false, ct: TestContext.Current.CancellationToken);

        await _notifications.DidNotReceiveWithAnyArgs().SendPushOnlyAsync(default, default, default, default, default);
    }

    [Fact]
    public async Task Notify_PushServiceThrows_IsSwallowedAndLogged()
    {
        _notifications
            .SendPushOnlyAsync(default, default, default, default, default)
            .ReturnsForAnyArgs(Task.FromException(new InvalidOperationException("dead token")));

        var act = () => NotifyAsync(Message(_coachId), _clientId, ct: TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
        _logger.ReceivedCalls().Should().Contain(call =>
            call.GetMethodInfo().Name == nameof(ILogger.Log)
            && (LogLevel)call.GetArguments()[0]! == LogLevel.Warning
            && call.GetArguments()[3] is InvalidOperationException);
    }

    [Fact]
    public async Task Notify_PushCancelled_PropagatesTheCancellation()
    {
        _notifications
            .SendPushOnlyAsync(default, default, default, default, default)
            .ReturnsForAnyArgs(Task.FromException(new OperationCanceledException()));

        var act = () => NotifyAsync(Message(_coachId), _clientId, ct: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Notify_ImageOnlyMessage_UsesImageVariantAndSendsNoText()
    {
        await NotifyAsync(Message(_coachId, text: string.Empty), _clientId, ct: TestContext.Current.CancellationToken);

        await _notifications.Received(1).SendPushOnlyAsync(
            _clientId, NotificationType.NewMessage,
            Arg.Is<IReadOnlyDictionary<string, string>>(p => !p.ContainsKey("messageText")),
            NotificationTemplates.NewMessageImageOnly, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Notify_LongText_PushesOnlyTheTruncatedText()
    {
        await NotifyAsync(Message(_coachId, new string('x', 1000)), _clientId, ct: TestContext.Current.CancellationToken);

        await _notifications.Received(1).SendPushOnlyAsync(
            _clientId, NotificationType.NewMessage,
            Arg.Is<IReadOnlyDictionary<string, string>>(p =>
                p["messageText"].Length == ChatMessageNotifier.MaxPushTextLength && p["messageText"].EndsWith("…")),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }
}
