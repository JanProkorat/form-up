using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FormUp.Application.Infrastructure.Services;

/// <summary>
/// Background worker that runs coaches' automation rules. Each tick it runs the schedule rows that
/// are due, brings the rows in step with the rules, sends the sends that fell due for the polled
/// events, stamps answered requests, and sends the check-in reminders and missed-week notices. Every claimed row is its own committed transaction, so
/// stopping the host only cancels the loop; a half-run row rolls back and is claimed again later.
/// </summary>
/// <param name="scopeFactory">Opens a scope per unit of work.</param>
/// <param name="timeProvider">Clock.</param>
/// <param name="configuration">Configuration; <c>AutomationTickIntervalSeconds</c> sets the tick.</param>
/// <param name="logger">Logger.</param>
public class AutomationScheduler(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    IConfiguration configuration,
    ILogger<AutomationScheduler> logger) : BackgroundService
{
    /// <summary>Default tick interval in seconds.</summary>
    internal const int DefaultTickIntervalSeconds = 60;

    private const int MaxClaimsPerTick = 500;

    /// <summary>
    /// Runs everything that is due right now. The hosted loop calls this every tick; tests call it
    /// directly instead of waiting for <see cref="ExecuteAsync"/>, which the host may never start.
    /// </summary>
    /// <param name="nowUtc">The instant to run as; null reads the clock.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RunDueNowAsync(DateTime? nowUtc = null, CancellationToken ct = default)
    {
        var now = DateTime.SpecifyKind(nowUtc ?? timeProvider.GetUtcNow().UtcDateTime, DateTimeKind.Utc);

        await RunPhaseAsync("claim", () => ProcessDueAsync(now, ct), ct);
        await RunPhaseAsync("coaches", () => ForEachCoachAsync(now, ct), ct);
        await RunPhaseAsync("sweep", () => SweepAnswersAsync(now, ct), ct);
        await RunPhaseAsync("check-ins", () => SweepCheckInsAsync(now, ct), ct);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(
            configuration.GetValue("AutomationTickIntervalSeconds", DefaultTickIntervalSeconds)));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunDueNowAsync(null, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // The host is stopping.
        }
    }

    private async Task RunPhaseAsync(string phase, Func<Task> run, CancellationToken ct)
    {
        try
        {
            await run();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An escaped exception would fault ExecuteAsync and stop the host, so a failing phase is
            // logged and the next one still runs.
            logger.LogError(ex, "AutomationScheduler: the {Phase} phase failed.", phase);
        }

        ct.ThrowIfCancellationRequested();
    }

    private async Task ProcessDueAsync(DateTime now, CancellationToken ct)
    {
        for (var claimed = 0; claimed < MaxClaimsPerTick; claimed++)
        {
            AutomationClaimResult result;

            using (var scope = scopeFactory.CreateScope())
            {
                result = await scope.ServiceProvider
                    .GetRequiredService<AutomationClaimProcessor>()
                    .ProcessNextAsync(now, ct);
            }

            switch (result.State)
            {
                case AutomationClaimState.Idle:
                    return;

                case AutomationClaimState.Done:
                    break;

                default:
                    // The row is still due. Move it on in a fresh scope, or the next claim returns it again.
                    using (var recoveryScope = scopeFactory.CreateScope())
                    {
                        await recoveryScope.ServiceProvider
                            .GetRequiredService<AutomationClaimProcessor>()
                            .RecoverAsync(result.ScheduleId, result.State == AutomationClaimState.Failed, now, ct);
                    }

                    break;
            }
        }
    }

    private async Task ForEachCoachAsync(DateTime now, CancellationToken ct)
    {
        List<long> coachProfileIds;

        using (var scope = scopeFactory.CreateScope())
        {
            coachProfileIds = await scope.ServiceProvider
                .GetRequiredService<IApplicationDbContext>()
                .AutomationRules
                .AsNoTracking()
                .Select(r => r.OwnerProfessionalProfileId)
                .Distinct()
                .ToListAsync(ct);
        }

        foreach (var coachProfileId in coachProfileIds)
        {
            try
            {
                // A scope per coach: a failed save leaves the change tracker undefined, so it is
                // never reused for the next coach.
                using var scope = scopeFactory.CreateScope();
                var services = scope.ServiceProvider;

                var snapshot = await AutomationCoachSnapshot.LoadAsync(
                    services.GetRequiredService<IApplicationDbContext>(),
                    services.GetRequiredService<IMongoContext>(),
                    services.GetRequiredService<EntitlementService>(),
                    coachProfileId,
                    ct);

                if (snapshot is null)
                {
                    continue;
                }

                await services.GetRequiredService<AutomationReconciler>().ReconcileAsync(snapshot, now, ct);
                await services.GetRequiredService<AutomationEventPoller>().PollAsync(snapshot, now, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "AutomationScheduler: coach {CoachProfileId} failed; the other coaches still run.", coachProfileId);
            }
        }
    }

    private async Task SweepAnswersAsync(DateTime now, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AutomationAnswerSweeper>().SweepAsync(now, ct);
    }

    private async Task SweepCheckInsAsync(DateTime now, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AutomationCheckInSweeper>().SweepAsync(now, ct);
    }
}
