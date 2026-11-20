using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FormUp.Application.Infrastructure.Services.Reporting;

/// <summary>
/// Runs <see cref="DailyReportSnapshotWriter"/> at start and then every hour, so the day's row always holds recent
/// figures and the first run after midnight creates the new day. The loop holds no state that needs shutdown work.
/// </summary>
/// <param name="writer">Writes the snapshot.</param>
/// <param name="timeProvider">Clock.</param>
/// <param name="logger">Logger.</param>
public class DailyReportSnapshotService(
    DailyReportSnapshotWriter writer,
    TimeProvider timeProvider,
    ILogger<DailyReportSnapshotService> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));

        do
        {
            try
            {
                await writer.WriteAsync(timeProvider.GetUtcNow(), stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "DailyReportSnapshotService: writing the daily snapshot failed.");
            }
        }
        while (await WaitForNextTickAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitForNextTickAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
