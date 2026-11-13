using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Domain.Services;

/// <summary>
/// Decides which live clients a lapsed coach keeps on the Free plan.
/// </summary>
/// <remarks>
/// Clients whose link is marked <see cref="ConnectionState.FreePlanKept"/> are kept first, up to the cap.
/// Remaining slots go to clients with a <see cref="ConnectionState.Connected"/> link, longest-linked first
/// (activation time, falling back to the row's creation time, then link id).
/// <see cref="ConnectionState.FreePlanReleased"/> links are never kept automatically. Every other live
/// client is over the cap.
/// </remarks>
public static class CoachKeepClientsResolver
{
    /// <summary>
    /// Splits the coach's live clients into kept and over-cap.
    /// </summary>
    /// <param name="liveLinks">The coach's links that are not <see cref="ConnectionState.Ended"/>.</param>
    /// <param name="cap">The Free plan's active-client cap; null keeps everyone.</param>
    public static KeepClientsState Resolve(IReadOnlyCollection<KeepLinkRow> liveLinks, int? cap)
    {
        var clients = liveLinks
            .GroupBy(link => link.ClientProfileId)
            .Select(group => new ClientRank(
                group.Key,
                group.Any(link => link.State == ConnectionState.FreePlanKept),
                group.Any(link => link.State == ConnectionState.Connected),
                RankKey(group.Where(link => link.State == ConnectionState.FreePlanKept).ToList()),
                RankKey(group.Where(link => link.State == ConnectionState.Connected).ToList())))
            .ToList();

        if (cap is null)
        {
            return new KeepClientsState(
                true, null, clients.OrderBy(client => client.Id).Select(client => client.Id).ToList(), []);
        }

        var kept = clients
            .Where(client => client.MarkedKept)
            .OrderBy(client => client.KeptKey)
            .ThenBy(client => client.Id)
            .Take(cap.Value)
            .ToList();

        var fill = clients
            .Where(client => client.HasConnected && kept.All(chosen => chosen.Id != client.Id))
            .OrderBy(client => client.ConnectedKey)
            .ThenBy(client => client.Id)
            .Take(Math.Max(0, cap.Value - kept.Count));

        var keptIds = kept.Concat(fill).Select(client => client.Id).ToHashSet();

        var overCap = clients.Where(client => !keptIds.Contains(client.Id)).ToList();

        return new KeepClientsState(
            true,
            cap,
            keptIds.OrderBy(id => id).ToList(),
            overCap.Select(client => client.Id).OrderBy(id => id).ToList(),
            IsChoicePending: overCap.Any(client => client.HasConnected));
    }

    private static (DateTime When, long LinkId) RankKey(List<KeepLinkRow> links) =>
        links.Count == 0
            ? (DateTime.MaxValue, long.MaxValue)
            : links.Select(link => (link.ActivatedAtUtc ?? link.DateCreated, link.Id)).Min();

    private sealed record ClientRank(
        long Id,
        bool MarkedKept,
        bool HasConnected,
        (DateTime When, long LinkId) KeptKey,
        (DateTime When, long LinkId) ConnectedKey);
}
