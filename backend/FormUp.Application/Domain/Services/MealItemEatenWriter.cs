using FormUp.Application.Domain.Documents;
using FormUp.Application.Infrastructure.Data.MongoDb;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FormUp.Application.Domain.Services;

/// <summary>
/// Atomic building blocks shared by the per-item eaten endpoints. Every write is a conditional update on
/// the stored row, so two requests acting on one meal at the same moment cannot overwrite each other.
/// </summary>
public static class MealItemEatenWriter
{
    /// <summary>
    /// Turns a legacy row (eaten, written before item ticks existed, so with no stored item ids) into one
    /// that lists every item as eaten, so a later tick or untick starts from the right set. Does nothing for
    /// any other row, and nothing when a concurrent request already materialised it.
    /// </summary>
    public static async Task MaterialiseLegacyRowAsync(
        IMongoContext mongo, MealLog row, IReadOnlyCollection<Guid> items, CancellationToken ct)
    {
        if (row.EatenAt is null || row.EatenItemIds.Count > 0 || items.Count == 0)
        {
            return;
        }

        var filter = Builders<MealLog>.Filter.And(
            Builders<MealLog>.Filter.Eq(l => l.Id, row.Id),
            Builders<MealLog>.Filter.Size(l => l.EatenItemIds, 0),
            Builders<MealLog>.Filter.Exists(l => l.EatenAt, true));

        await mongo.MealLogs.UpdateOneAsync(
            filter, Builders<MealLog>.Update.Set(l => l.EatenItemIds, items.ToList()), cancellationToken: ct);
    }

    /// <summary>
    /// Reads one row by its id; null when it no longer exists.
    /// </summary>
    public static async Task<MealLog?> FindByIdAsync(IMongoContext mongo, ObjectId id, CancellationToken ct)
    {
        using var cursor = await mongo.MealLogs.FindAsync(
            Builders<MealLog>.Filter.Eq(l => l.Id, id), cancellationToken: ct);

        return await cursor.FirstOrDefaultAsync(ct);
    }
}
