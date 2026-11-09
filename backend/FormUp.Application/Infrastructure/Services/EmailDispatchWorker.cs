using FormUp.Application.Domain.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FormUp.Application.Infrastructure.Services;

/// <summary>
/// Background service that drains <see cref="IBackgroundEmailQueue"/> and performs the
/// actual SMTP/provider send (#702). Introduced so the anonymous resend-verification
/// endpoint can enqueue a send and return immediately instead of awaiting the network
/// round-trip in the request path — see <see cref="IBackgroundEmailQueue"/> for why.
///
/// <para>
/// Mirrors <see cref="SocialLoginNonceReaperService"/>'s scope-per-item pattern: this
/// worker never captures the request's <see cref="CancellationToken"/> (it would already
/// be cancelled by the time this runs, since the request has completed) and never
/// captures the request-scoped <see cref="IEmailService"/> (disposed along with the
/// request's DI scope). Instead every dequeued item is processed inside a fresh scope
/// created from <see cref="IServiceScopeFactory"/>.
/// </para>
///
/// <para>
/// Graceful shutdown drain (#705): on host shutdown, <c>stoppingToken</c> cancelling used
/// to make <c>ReadAllAsync</c> throw <see cref="OperationCanceledException"/> immediately,
/// dropping any items still buffered in the channel plus a send that was in flight. Now
/// <see cref="IBackgroundEmailQueue.Complete"/> closes the channel writer on shutdown (new
/// enqueues start failing cleanly — see <see cref="IBackgroundEmailQueue.Complete"/>), while
/// the read loop itself runs on <see cref="CancellationToken.None"/> so it keeps draining the
/// already-buffered items and completes naturally once they're exhausted, instead of being
/// torn down by cancellation. Each send also runs on <see cref="CancellationToken.None"/> so
/// an in-flight/drained send finishes rather than being cancelled mid-send.
/// </para>
///
/// <para>
/// Why the drain is owned by <see cref="StopAsync"/> and not by <c>ExecuteAsync</c> (#866):
/// <see cref="BackgroundService.StartAsync"/> does not call <c>ExecuteAsync</c> inline — it
/// schedules it as <c>Task.Run(() =&gt; ExecuteAsync(_stoppingCts.Token), _stoppingCts.Token)</c>.
/// Passing a cancellation token to <c>Task.Run</c> means that if the token is already cancelled
/// when the thread pool dequeues the work item, the delegate is <em>discarded</em> and
/// <c>ExecuteAsync</c> never runs at all — not even a <c>finally</c> inside it. A shutdown that
/// begins before the pool picks the worker up therefore drops every buffered item, which is
/// exactly the guarantee #705 set out to make. Placing the drain in <see cref="StopAsync"/>,
/// which the host always awaits, makes it independent of whether <c>ExecuteAsync</c> ever ran.
/// The drain still has no timer of its own: it stays bounded by <c>HostOptions.ShutdownTimeout</c>,
/// which cancels the token handed to <see cref="StopAsync"/> — at which point the drain stops
/// picking up new items but never cancels a send already under way.
/// </para>
/// </summary>
public class EmailDispatchWorker(
    IBackgroundEmailQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<EmailDispatchWorker> logger) : BackgroundService
{
    /// <summary>
    /// Closes the queue and guarantees the shutdown drain, regardless of whether
    /// <c>ExecuteAsync</c> ever ran — see the class remarks (#866) for why that is not a
    /// given.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Close the writer up front: new enqueues start failing cleanly from this moment, and
        // the steady-state pump (if it is running) ends by exhausting the channel rather than
        // having to observe cancellation at all.
        queue.Complete();

        await base.StopAsync(cancellationToken);

        // base.StopAsync has just awaited the ExecuteAsync task, so a completed — or never
        // started — task means nothing else is reading the channel. That check is what keeps
        // the queue's SingleReader contract intact: if the task is still running (the host's
        // shutdown timeout elapsed while it drained), it is already doing this work and the
        // host is out of budget either way.
        if (ExecuteTask is null or { IsCompleted: true })
        {
            await DrainAsync(cancellationToken);
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("EmailDispatchWorker: starting.");

        // Backstop for the teardown paths that never call StopAsync — BackgroundService.Dispose
        // cancels this token directly. Closing the writer is what lets the drain below end.
        using var completeOnShutdown = stoppingToken.Register(queue.Complete);

        await DrainAsync(CancellationToken.None);

        logger.LogInformation("EmailDispatchWorker: stopped.");
    }

    /// <summary>
    /// Reads the queue to exhaustion, sending each item in its own DI scope. Shared by the
    /// steady-state pump in <c>ExecuteAsync</c> (which passes
    /// <see cref="CancellationToken.None"/> and so runs until the queue is completed) and by
    /// the shutdown drain in <see cref="StopAsync"/>.
    /// </summary>
    /// <param name="stopBeforeNextSend">Checked only <em>between</em> items: once signalled,
    /// no further item is picked up, but a send already under way is never cancelled. On the
    /// shutdown path this is the host's <c>HostOptions.ShutdownTimeout</c> budget.</param>
    private async Task DrainAsync(CancellationToken stopBeforeNextSend)
    {
        try
        {
            // Deliberately CancellationToken.None, not the parameter: Complete() closes the
            // channel writer on shutdown, so Channel.Reader.ReadAllAsync finishes naturally
            // once every buffered item has been yielded rather than throwing
            // OperationCanceledException and abandoning them mid-drain.
            await foreach (var item in queue.ReadAllAsync(CancellationToken.None))
            {
                try
                {
                    // Fresh scope per item: the queued item carries only value-copied
                    // data (email, token, language, ...) — never a service reference — so
                    // resolving IEmailService here always gets a live instance, never
                    // one disposed with a long-gone request scope.
                    using var scope = scopeFactory.CreateScope();
                    var emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();

                    // CancellationToken.None: a send picked up during the shutdown drain
                    // (or already in flight when shutdown began) must be allowed to finish
                    // rather than being cancelled mid-send.
                    switch (item)
                    {
                        case VerificationEmailWorkItem verification:
                            await emailService.SendEmailVerificationAsync(
                                verification.Email, verification.Token, verification.Language, CancellationToken.None);
                            break;
                        case InvitationEmailWorkItem invitation:
                            await emailService.SendInvitationEmailAsync(
                                invitation.Email, invitation.TrainerName, invitation.Token, invitation.Language,
                                invitation.PersonalMessage, invitation.Package, CancellationToken.None,
                                invitation.CoachRole);
                            break;
                        case NotificationEmailWorkItem notification:
                            await emailService.SendNotificationEmailAsync(notification, CancellationToken.None);
                            break;
                        default:
                            throw new NotSupportedException(
                                $"EmailDispatchWorker does not know how to dispatch {item.GetType().Name}.");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && item is NotificationEmailWorkItem)
                {
                    // Notification items log the recipient's user id only, never the address.
                    var notification = (NotificationEmailWorkItem)item;

                    logger.LogError(ex,
                        "EmailDispatchWorker: failed to send {Event} notification email to user {UserId}.",
                        notification.Event, notification.UserId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Never surface a send failure anywhere the request path can see it —
                    // the client already received its generic 200 before this ran. The
                    // token/invite row was already persisted synchronously by the caller,
                    // so a failed send here does not leave that store inconsistent.
                    var itemKind = item switch
                    {
                        VerificationEmailWorkItem => "verification",
                        InvitationEmailWorkItem => "invitation",
                        _ => "unknown"
                    };

                    logger.LogError(ex,
                        "EmailDispatchWorker: failed to send background {ItemKind} email to {Email}.",
                        itemKind, item.Email);
                }
                finally
                {
                    queue.MarkProcessed();
                }

                if (stopBeforeNextSend.IsCancellationRequested)
                {
                    logger.LogWarning(
                        "EmailDispatchWorker: shutdown budget exhausted with {PendingCount} email(s) still buffered.",
                        queue.PendingCount);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Defensive backstop only: the read loop above runs on CancellationToken.None, so
            // it should never actually throw OCE. Logged rather than swallowed silently —
            // reaching here means buffered emails were abandoned, which is the whole class of
            // bug #705/#866 exist to prevent.
            logger.LogWarning(
                "EmailDispatchWorker: drain cancelled unexpectedly with {PendingCount} email(s) still buffered.",
                queue.PendingCount);
        }
    }
}
