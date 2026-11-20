using FormUp.Application.Domain.Interfaces;

namespace FormUp.Tests.Infrastructure;

/// <summary>
/// Thread-safe fake implementation of <see cref="IRealtimeNotifier"/> for integration tests.
/// Records all <c>NotifyAsync</c> calls so tests can assert on event names, recipients, and payloads.
/// </summary>
public class FakeRealtimeNotifier : IRealtimeNotifier
{
    private readonly List<NotifyCall> _calls = [];
    private readonly List<AdminNotifyCall> _adminCalls = [];
    private readonly Lock _lock = new();
    private bool _throwOnNextCall;

    /// <summary>
    /// Returns a snapshot of all recorded <c>NotifyAsync</c> invocations.
    /// </summary>
    public IReadOnlyList<NotifyCall> Calls
    {
        get
        {
            lock (_lock) return _calls.ToList();
        }
    }

    /// <inheritdoc />
    public Task NotifyAsync(Guid userId, string eventType, object payload, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_throwOnNextCall)
            {
                _throwOnNextCall = false;
                throw new InvalidOperationException("hub unavailable (simulated)");
            }

            _calls.Add(new NotifyCall(userId, eventType, payload));
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns a snapshot of all recorded <c>NotifyAdminsAsync</c> invocations.
    /// </summary>
    public IReadOnlyList<AdminNotifyCall> AdminCalls
    {
        get
        {
            lock (_lock) return _adminCalls.ToList();
        }
    }

    /// <inheritdoc />
    public Task NotifyAdminsAsync(string eventType, object payload, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_throwOnNextCall)
            {
                _throwOnNextCall = false;
                throw new InvalidOperationException("hub unavailable (simulated)");
            }

            _adminCalls.Add(new AdminNotifyCall(eventType, payload));
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Clears all recorded calls. Call between tests if the notifier is shared.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _calls.Clear();
            _adminCalls.Clear();
            _throwOnNextCall = false;
        }
    }

    /// <summary>
    /// Configures the notifier to throw <see cref="InvalidOperationException"/> on the next
    /// <c>NotifyAsync</c> call. Use in tests that verify broadcast failures are non-fatal.
    /// After the single throw the notifier returns to normal recording behaviour.
    /// </summary>
    public void SimulateThrowOnNextCall()
    {
        lock (_lock) _throwOnNextCall = true;
    }

    /// <summary>
    /// A single recorded invocation of <c>NotifyAsync</c>.
    /// </summary>
    public record NotifyCall(Guid UserId, string EventType, object Payload);

    /// <summary>
    /// A single recorded invocation of <c>NotifyAdminsAsync</c>.
    /// </summary>
    public record AdminNotifyCall(string EventType, object Payload);
}
