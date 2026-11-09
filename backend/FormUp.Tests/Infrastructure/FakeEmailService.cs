using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;

namespace FormUp.Tests.Infrastructure;

/// <summary>
/// No-op email service for integration tests. Records sent emails for assertion.
/// </summary>
/// <remarks>
/// Thread safety (#702): the anonymous resend-verification endpoint dispatches its
/// send from a background worker thread (<c>EmailDispatchWorker</c>), concurrently with
/// the test's own assertions and possibly with other tests' requests still in flight. A
/// plain <c>List&lt;T&gt;.Add</c> racing a <c>foreach</c>/<c>Where</c> enumeration on the
/// same list is undefined behavior, so every read and write below goes through a single
/// lock. Reads return a point-in-time snapshot (<c>ToArray()</c>) rather than the live
/// list, so a caller enumerating the result can never observe a concurrent mutation.
///
/// Per-instance store (#726 refinement): this used to hold its three send lists in
/// `static` fields so any test could read `FakeEmailService.SentVerifications` without
/// resolving an instance from DI. That worked as long as `EmailDispatchWorker` never
/// auto-started in a test host — with no worker running, nothing but the test's own
/// request ever wrote to the store. Once `EmailDispatchWorker` is kept running (see
/// `TestHostedServiceExtensions`) so `AnonymousResendVerificationEndpointTests` can
/// observe its fire-and-forget sends land, a *different* Testcontainers-backed
/// factory's now-zombie worker (the six factories in this suite intentionally never
/// fully dispose their host — see the #296 comment on `FitnessApiFactory`) could still
/// be draining its queue and writing into the SAME static store a completely unrelated
/// collection's tests are asserting against, corrupting counts across collections. Each
/// factory now registers this class as a per-host `AddSingleton&lt;FakeEmailService&gt;()`
/// (mirroring the existing `FakeRealtimeNotifier`/`FakePushNotificationService` pattern)
/// so every host's worker and every test resolving that host's DI container share one
/// instance — and no two hosts ever share a store.
/// </remarks>
public class FakeEmailService : IEmailService
{
    private readonly object _sync = new();

    private readonly List<(string Email, string TrainerName, string Token, string Language, string? PersonalMessage, InvitationPackageInfo? Package, InvitationCoachRole? CoachRole)> _invitationsSent = [];
    private readonly List<(string Email, string Token, string Language)> _passwordResetsSent = [];
    private readonly List<(string Email, string Token, string Language)> _verificationsSent = [];
    private readonly List<(string Email, string Code, string Language)> _reauthCodesSent = [];
    private readonly List<(string Email, string MaskedNewIban, string MaskedPreviousIban, string UndoToken, string Language)> _bankAccountChangesSent = [];
    private readonly HashSet<string> _invitationSendFailuresByEmail = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PaymentRequestEmail> _paymentRequestsSent = [];
    private readonly HashSet<string> _paymentRequestFailuresByEmail = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Snapshot of payment request emails sent during the test, with the QR PNG bytes and content id.
    /// </summary>
    public IReadOnlyList<PaymentRequestEmail> SentPaymentRequests
    {
        get
        {
            lock (_sync)
            {
                return _paymentRequestsSent.ToArray();
            }
        }
    }

    /// <summary>
    /// Makes <see cref="SendPaymentRequestEmailAsync"/> throw for the given recipient only, simulating a
    /// mail provider outage without poisoning other tests that share this per-host instance.
    /// </summary>
    public void FailPaymentRequestsFor(string email)
    {
        lock (_sync)
        {
            _paymentRequestFailuresByEmail.Add(email);
        }
    }

    /// <inheritdoc />
    public Task SendPaymentRequestEmailAsync(PaymentRequestEmail email, CancellationToken ct)
    {
        lock (_sync)
        {
            if (_paymentRequestFailuresByEmail.Contains(email.ToEmail))
            {
                throw new InvalidOperationException("Simulated mail provider failure for a payment request.");
            }

            _paymentRequestsSent.Add(email);
        }

        return Task.CompletedTask;
    }

    private readonly List<NotificationEmailWorkItem> _notificationsSent = [];
    private readonly HashSet<Guid> _notificationFailuresByUser = [];

    /// <summary>
    /// Snapshot of coach notification emails sent during the test.
    /// </summary>
    public IReadOnlyList<NotificationEmailWorkItem> SentNotifications
    {
        get
        {
            lock (_sync)
            {
                return _notificationsSent.ToArray();
            }
        }
    }

    /// <summary>
    /// Notification emails sent to one recipient, after the background queue has gone idle. Waiting for
    /// the queue to drain first makes a "nothing was sent" assertion mean the worker had its chance.
    /// </summary>
    /// <param name="queue">The host's background email queue.</param>
    /// <param name="userId">The recipient's user id.</param>
    public async Task<IReadOnlyList<NotificationEmailWorkItem>> NotificationsForAsync(IBackgroundEmailQueue queue, Guid userId)
    {
        await WaitForAsync(() => queue.PendingCount == 0);

        return SentNotifications.Where(item => item.UserId == userId).ToList();
    }

    /// <summary>
    /// Makes <see cref="SendNotificationEmailAsync"/> throw for the given recipient only.
    /// </summary>
    public void FailNotificationsFor(Guid userId)
    {
        lock (_sync)
        {
            _notificationFailuresByUser.Add(userId);
        }
    }

    /// <inheritdoc />
    public Task SendNotificationEmailAsync(NotificationEmailWorkItem item, CancellationToken ct)
    {
        lock (_sync)
        {
            if (_notificationFailuresByUser.Contains(item.UserId))
            {
                throw new InvalidOperationException("Simulated mail provider failure for a notification email.");
            }

            _notificationsSent.Add(item);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Snapshot of bank account re-authentication code emails sent during the test.
    /// </summary>
    public IReadOnlyList<(string Email, string Code, string Language)> SentReauthCodes
    {
        get
        {
            lock (_sync)
            {
                return _reauthCodesSent.ToArray();
            }
        }
    }

    /// <summary>
    /// Snapshot of bank account changed emails sent during the test.
    /// </summary>
    public IReadOnlyList<(string Email, string MaskedNewIban, string MaskedPreviousIban, string UndoToken, string Language)> SentBankAccountChanges
    {
        get
        {
            lock (_sync)
            {
                return _bankAccountChangesSent.ToArray();
            }
        }
    }

    /// <summary>
    /// Snapshot of invitation emails sent during the test.
    /// </summary>
    public IReadOnlyList<(string Email, string TrainerName, string Token, string Language, string? PersonalMessage, InvitationPackageInfo? Package, InvitationCoachRole? CoachRole)> SentInvitations
    {
        get
        {
            lock (_sync)
            {
                return _invitationsSent.ToArray();
            }
        }
    }

    /// <summary>
    /// Snapshot of password reset emails sent during the test.
    /// </summary>
    public IReadOnlyList<(string Email, string Token, string Language)> SentPasswordResets
    {
        get
        {
            lock (_sync)
            {
                return _passwordResetsSent.ToArray();
            }
        }
    }

    /// <summary>
    /// Snapshot of email verification emails sent during the test. See the type-level
    /// remarks for why this is a defensive copy rather than the live list, and
    /// <see cref="WaitForAsync"/> for the paired deterministic-drain seam that tests
    /// asserting on a background-dispatched send (#702) must use instead of a fixed
    /// sleep.
    /// </summary>
    public IReadOnlyList<(string Email, string Token, string Language)> SentVerifications
    {
        get
        {
            lock (_sync)
            {
                return _verificationsSent.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public Task SendInvitationEmailAsync(string toEmail, string trainerName, string invitationToken, string language, string? personalMessage, InvitationPackageInfo? package, CancellationToken ct, InvitationCoachRole? coachRole = null)
    {
        lock (_sync)
        {
            if (_invitationSendFailuresByEmail.Contains(toEmail))
            {
                throw new InvalidOperationException(
                    $"Simulated SMTP failure for {toEmail} (test-configured via FailInvitationSendFor).");
            }

            _invitationsSent.Add((toEmail, trainerName, invitationToken, language, personalMessage, package, coachRole));
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Configures <see cref="SendInvitationEmailAsync"/> to throw for the given recipient only
    /// (#1109) — asserting the background worker's failure-and-keep-running path without
    /// poisoning other tests that share this per-host singleton instance (see the type-level
    /// remarks) and invite a DIFFERENT email within the same collection.
    /// </summary>
    public void FailInvitationSendFor(string email)
    {
        lock (_sync)
        {
            _invitationSendFailuresByEmail.Add(email);
        }
    }

    /// <inheritdoc />
    public Task SendPasswordResetEmailAsync(string toEmail, string resetToken, string language, CancellationToken ct)
    {
        lock (_sync)
        {
            _passwordResetsSent.Add((toEmail, resetToken, language));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SendEmailVerificationAsync(string toEmail, string verificationToken, string language, CancellationToken ct)
    {
        lock (_sync)
        {
            _verificationsSent.Add((toEmail, verificationToken, language));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SendReauthCodeEmailAsync(string toEmail, string code, string language, CancellationToken ct)
    {
        lock (_sync)
        {
            _reauthCodesSent.Add((toEmail, code, language));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SendBankAccountChangedEmailAsync(
        string toEmail, string maskedNewIban, string maskedPreviousIban, string undoToken, string language, CancellationToken ct)
    {
        lock (_sync)
        {
            _bankAccountChangesSent.Add((toEmail, maskedNewIban, maskedPreviousIban, undoToken, language));
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Clears all recorded emails.
    /// </summary>
    public void Reset()
    {
        lock (_sync)
        {
            _reauthCodesSent.Clear();
            _bankAccountChangesSent.Clear();
            _invitationsSent.Clear();
            _passwordResetsSent.Clear();
            _verificationsSent.Clear();
            _paymentRequestsSent.Clear();
            _paymentRequestFailuresByEmail.Clear();
            _notificationsSent.Clear();
            _notificationFailuresByUser.Clear();
        }
    }

    /// <summary>
    /// Deterministic drain/idle seam for tests asserting on a background-dispatched send
    /// (#702) — e.g. the anonymous resend-verification endpoint's fire-and-forget SMTP
    /// send, which may not have landed in <see cref="SentVerifications"/> yet by the time
    /// the HTTP response returns. Polls <paramref name="predicate"/> in short, bounded
    /// increments — never a single fixed-duration sleep — returning as soon as it is
    /// satisfied. On timeout the loop simply exits; the caller's own assertion (not this
    /// helper) is what should fail, so the test's failure message stays meaningful.
    ///
    /// Kept `static` even though the store itself is now per-instance: this helper never
    /// touches instance state directly — the caller's <paramref name="predicate"/> closure
    /// is what reads a specific instance's snapshot property, so there is nothing here
    /// that needs to move off the type.
    /// </summary>
    /// <param name="predicate">Re-evaluated against live state on every poll.</param>
    /// <param name="timeout">Defaults to 5 seconds — generous for an in-process
    /// background worker, short enough to fail fast in CI if something regresses.</param>
    public static async Task WaitForAsync(Func<bool> predicate, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));

        while (!predicate() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }
    }
}
