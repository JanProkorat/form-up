using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FormUp.Application.Infrastructure.Services;

/// <summary>
/// Claims one Failed automation run whose retry is due and sends it again, all in one committed
/// transaction. The run is updated in place (Sent, Skipped or a final Failed), never duplicated. Two
/// workers never retry the same run: the claim skips rows another worker holds.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="sender">Retries one occurrence.</param>
/// <param name="afterCommit">Holds the pushes and realtime events until the run has committed.</param>
/// <param name="scopeFactory">Opens the fresh scope that records a failed retry.</param>
/// <param name="logger">Logger.</param>
public sealed class AutomationRetryProcessor(
    IApplicationDbContext db,
    AutomationOccurrenceSender sender,
    AutomationAfterCommitQueue afterCommit,
    IServiceScopeFactory scopeFactory,
    ILogger<AutomationRetryProcessor> logger)
{
    /// <summary>
    /// Claims and retries the earliest run whose retry is due. A retry that throws is rolled back and
    /// counted as another failed attempt in a fresh scope.
    /// </summary>
    /// <param name="nowUtc">The current instant.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when a run was claimed; false when no retry is due.</returns>
    public async Task<bool> ProcessNextAsync(DateTime nowUtc, CancellationToken ct)
    {
        await using var transaction = await db.BeginTransactionAsync(ct);

        var run = await db.ClaimDueAutomationRetryAsync(nowUtc, ct);

        if (run is null)
        {
            return false;
        }

        var runId = run.Id;
        var attemptCount = run.AttemptCount;

        try
        {
            // One client at a time, like the poller, so the open-request checks cannot race a send.
            await db.LockClientProfileAsync(run.ClientProfileId, ct);

            await sender.RetryAsync(run, nowUtc, ct);
            await transaction.CommitAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            afterCommit.Discard();
            logger.LogError(ex, "AutomationScheduler: retrying automation run {RunId} failed; rolling it back.", runId);

            await RollBackAsync(transaction, runId, ct);
            await RecordFailedAttemptAsync(runId, attemptCount, nowUtc, ct);

            return true;
        }

        // The run is committed: tell the client now, and never fail the run over it.
        await afterCommit.FlushAsync(ct);

        return true;
    }

    private async Task RollBackAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction, long runId, CancellationToken ct)
    {
        try
        {
            // The claim's row lock must be gone before the attempt is counted from another scope.
            await transaction.RollbackAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "AutomationScheduler: could not roll back the retry of automation run {RunId}.", runId);
        }
    }

    /// <summary>
    /// Counts one more failed attempt on the run and schedules the next retry, or ends the retries.
    /// Compare-and-set on the attempt count read at claim, so one failure never advances two attempts.
    /// </summary>
    private async Task RecordFailedAttemptAsync(long runId, int attemptCount, DateTime nowUtc, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var scopedDb = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();

            var nextAttempt = attemptCount + 1;
            var nextRetryAt = AutomationRetryPolicy.NextRetryAt(nextAttempt, nowUtc);

            await scopedDb.AutomationRuns
                .Where(r => r.Id == runId
                            && r.Status == AutomationRunStatus.Failed
                            && r.AttemptCount == attemptCount)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(r => r.AttemptCount, nextAttempt)
                        .SetProperty(r => r.NextRetryAt, nextRetryAt),
                    ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "AutomationScheduler: could not record the failed retry of automation run {RunId}.", runId);
        }
    }
}
