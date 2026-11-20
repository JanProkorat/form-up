using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Npgsql;
using Constants = FormUp.Application.Domain.Constants;

namespace FormUp.Application.Infrastructure.Services.Reporting;

/// <summary>
/// Computes the day's totals and upserts the one <see cref="DailyReportSnapshot"/> row of that UTC day. Running it again
/// the same day overwrites the row with the latest figures; two writers racing on a new day end with one row.
/// </summary>
/// <param name="scopeFactory">Creates one scope per attempt, so a lost race starts from a clean context.</param>
public class DailyReportSnapshotWriter(IServiceScopeFactory scopeFactory)
{
    private const int ActiveCoachWindowDays = 30;
    private const int ActiveClientWindowDays = 7;
    private const int PublishedWeekWindowDays = 7;
    private const string UniqueViolation = "23505";

    /// <summary>
    /// Writes the snapshot of the UTC day containing <paramref name="now"/>.
    /// </summary>
    /// <param name="now">Current time; the 7 and 30 day windows end here.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task WriteAsync(DateTimeOffset now, CancellationToken ct)
    {
        var day = DateOnly.FromDateTime(now.UtcDateTime);

        try
        {
            await UpsertAsync(day, now, ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            // A concurrent writer inserted the day's row first; the retry finds it and updates it.
            await UpsertAsync(day, now, ct);
        }
    }

    private async Task UpsertAsync(DateOnly day, DateTimeOffset now, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        var revenue = scope.ServiceProvider.GetRequiredService<ICoachRevenueService>();

        var summary = await revenue.GetSummaryAsync(now, ct);
        var snapshot = await db.DailyReportSnapshots.FirstOrDefaultAsync(s => s.Day == day, ct);

        if (snapshot is null)
        {
            snapshot = new DailyReportSnapshot { Day = day };
            db.DailyReportSnapshots.Add(snapshot);
        }

        snapshot.TotalCoachCount = summary.AllCount;
        snapshot.PayingCoachCount = summary.PayingCount;
        snapshot.PayingCoachesByPlan = summary.PayingByPlan
            .Select(plan => new ReportPlanCount { PlanCode = plan.Code, Count = plan.Count })
            .ToList();
        snapshot.FreeCoachCount = summary.FreeCount;
        snapshot.GivenFreeCoachCount = summary.GivenFreeCount;
        snapshot.MonthlyRevenueFull = ToAmounts(summary.FullMonthlyRevenue);
        snapshot.MonthlyRevenueAfterPromotions = ToAmounts(summary.DiscountedMonthlyRevenue);

        var since30Days = now.UtcDateTime.AddDays(-ActiveCoachWindowDays);
        var since7Days = now.UtcDateTime.AddDays(-ActiveClientWindowDays);

        snapshot.ActiveCoachesLast30Days = await db.RefreshTokens
            .AsNoTracking()
            .Where(token => token.DateCreated >= since30Days
                            && db.ProfessionalProfiles.Any(profile => profile.UserId == token.UserId))
            .Select(token => token.UserId)
            .Distinct()
            .CountAsync(ct);

        snapshot.ActiveClientsLast7Days = await db.RefreshTokens
            .AsNoTracking()
            .Where(token => token.DateCreated >= since7Days
                            && db.ClientProfiles.Any(profile => profile.UserId == token.UserId))
            .Select(token => token.UserId)
            .Distinct()
            .CountAsync(ct);

        snapshot.PlanWeeksPublishedLast7Days =
            await CountPublishedWeeksAsync(mongo.NutritionPlans, now.UtcDateTime.AddDays(-PublishedWeekWindowDays), ct)
            + await CountPublishedWeeksAsync(mongo.TrainingPlans, now.UtcDateTime.AddDays(-PublishedWeekWindowDays), ct);

        await FillClientFiguresAsync(db, snapshot, now, ct);

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Free coaches at the Free plan's own client limit, and the median live clients per coach over all coach profiles
    /// (those with none count as zero). Live clients are distinct clients with a link that is not Ended.
    /// </summary>
    private static async Task FillClientFiguresAsync(
        IApplicationDbContext db, DailyReportSnapshot snapshot, DateTimeOffset now, CancellationToken ct)
    {
        var clientCounts = await db.ClientProfessionalLinks
            .AsNoTracking()
            .Where(link => link.ConnectionState != ConnectionState.Ended)
            .GroupBy(link => link.ProfessionalProfileId)
            .Select(group => new { ProfileId = group.Key, Count = group.Select(link => link.ClientProfileId).Distinct().Count() })
            .ToDictionaryAsync(row => row.ProfileId, row => row.Count, ct);

        var allProfileIds = await db.ProfessionalProfiles.AsNoTracking().Select(profile => profile.Id).ToListAsync(ct);
        var freeProfileIds = await db.ProfessionalProfiles
            .AsNoTracking()
            .Where(CoachSubscriptionStatusClassifier.Matches(AdminCoachStatusFilter.Free, now))
            .Select(profile => profile.Id)
            .ToListAsync(ct);

        var freeCap = await db.SubscriptionPlans
            .AsNoTracking()
            .Where(plan => plan.Code == Constants.SubscriptionPlanCodes.Free)
            .Select(plan => plan.MaxActiveClients)
            .FirstOrDefaultAsync(ct);

        snapshot.FreeCoachesAtClientCap = freeCap is { } cap
            ? freeProfileIds.Count(id => clientCounts.GetValueOrDefault(id) >= cap)
            : 0;
        snapshot.MedianClientsPerCoach = Median(allProfileIds.Select(id => clientCounts.GetValueOrDefault(id)).ToList());
    }

    /// <summary>
    /// The median of the counts: the middle one, or the mean of the two middle ones for an even number of values.
    /// Zero for no values. Sorts the list in place.
    /// </summary>
    /// <param name="values">The counts.</param>
    public static decimal Median(List<int> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        values.Sort();
        var middle = values.Count / 2;

        return values.Count % 2 == 1 ? values[middle] : (values[middle - 1] + values[middle]) / 2m;
    }

    /// <summary>
    /// Counts embedded weeks published since the cutoff. Reads the week-level publish date: the plan-level one is never
    /// written.
    /// </summary>
    private static async Task<int> CountPublishedWeeksAsync<TDocument>(
        IMongoCollection<TDocument> collection, DateTime since, CancellationToken ct)
    {
        var publishedSince = new BsonDocument("weeks.datePublished", new BsonDocument("$gte", since));
        var pipeline = new[]
        {
            new BsonDocument("$match", publishedSince),
            new BsonDocument("$unwind", "$weeks"),
            new BsonDocument("$match", publishedSince),
            new BsonDocument("$count", "published"),
        };

        var result = await collection.Aggregate<BsonDocument>(pipeline).FirstOrDefaultAsync(ct);

        return result is null ? 0 : result["published"].ToInt32();
    }

    private static List<ReportCurrencyAmount> ToAmounts(IReadOnlyList<CoachRevenueCurrencyTotal> totals) =>
        totals
            .Select(total => new ReportCurrencyAmount { Currency = total.Currency, AmountMinorUnits = total.AmountMinorUnits })
            .ToList();
}
