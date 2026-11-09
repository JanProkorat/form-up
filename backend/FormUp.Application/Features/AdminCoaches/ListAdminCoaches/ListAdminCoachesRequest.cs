using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Features.AdminCoaches.ListAdminCoaches;

/// <summary>
/// Query for the admin coaches list.
/// </summary>
public class ListAdminCoachesRequest
{
    /// <summary>Page number (1-based). Defaults to 1.</summary>
    public int Page { get; set; } = 1;

    /// <summary>Number of rows per page, 1 to 100. Defaults to 20.</summary>
    public int PageSize { get; set; } = 20;

    /// <summary>Status bucket to list. Defaults to every coach.</summary>
    public AdminCoachStatusFilter Status { get; set; } = AdminCoachStatusFilter.All;

    /// <summary>Optional search by coach name or email.</summary>
    public string? Search { get; set; }
}
