using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Features.Trainers.GetClients;

/// <summary>
/// Request model for retrieving a trainer's client list with pagination.
/// </summary>
public class GetClientsRequest
{
    /// <summary>
    /// Page number (1-based). Defaults to 1.
    /// </summary>
    public int Page { get; set; } = 1;

    /// <summary>
    /// Number of items per page. Defaults to 20.
    /// </summary>
    public int PageSize { get; set; } = 20;

    /// <summary>
    /// Optional search filter by client name or email.
    /// </summary>
    public string? Search { get; set; }

    /// <summary>
    /// Optional tab selector. Omitted means every live link (<c>IsActive == true</c>) —
    /// today's exact pre-existing behaviour, Active and Paused combined. Archived is only
    /// returned when explicitly requested.
    /// </summary>
    public ClientListStatus? Status { get; set; }

    /// <summary>
    /// Optional set of the caller's own <c>ClientTag.PublicId</c> values.
    /// A client matches if it carries ANY of the requested tag ids via the caller's own link —
    /// selecting more tags widens the result set, as a filter dropdown normally does. An unknown
    /// or a foreign (another coach's) tag id is never distinguished from a real one — both simply
    /// match nothing, never a 404, so tag ids stay non-enumerable from the outside.
    /// </summary>
    public List<Guid> TagIds { get; set; } = [];

    /// <summary>
    /// Optional filter chip narrowing the current tab further. Its own count is reported in
    /// <see cref="GetClientsResponse.FilterCounts"/> without this filter applied.
    /// </summary>
    public ClientListFilter? Filter { get; set; }

    /// <summary>
    /// Optional clients-page tab. Cannot be combined with <see cref="Status"/>. Omitted means
    /// the legacy <see cref="Status"/> selection.
    /// </summary>
    public ClientListTab? Tab { get; set; }

    /// <summary>
    /// Optional plan filter, applied after the tab. Archived rows never match.
    /// </summary>
    public ClientPlanFilter? Plan { get; set; }
}
