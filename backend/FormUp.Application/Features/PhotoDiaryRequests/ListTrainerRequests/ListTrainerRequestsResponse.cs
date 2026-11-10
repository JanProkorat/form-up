using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Features.PhotoDiaryRequests.ListTrainerRequests;

/// <summary>
/// Summary DTO for a single photo diary request in a list.
/// </summary>
public class PhotoDiaryRequestSummary
{
    public Guid Id { get; set; }
    public Guid ProfessionalId { get; set; }
    public long? LinkId { get; set; }
    public long? PendingInviteId { get; set; }
    public Guid? PlanId { get; set; }
    public int DurationDays { get; set; }
    public PhotoDiaryMode? Mode { get; set; }

    /// <summary>The coach's optional note to the client.</summary>
    public string? Message { get; set; }
    public PhotoDiaryStatus Status { get; set; }
    public string? DismissReason { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>End of the open window (<c>CreatedAt + DurationDays</c>); null for a one-time (Bulk) request, which never expires.</summary>
    public DateTimeOffset? EndsAt { get; set; }

    /// <summary>True while the request is Pending/Accepted/InProgress and has not reached <see cref="EndsAt"/>.</summary>
    public bool IsOpen { get; set; }
}

/// <summary>
/// Paginated list of photo diary requests for a trainer.
/// </summary>
public class ListTrainerRequestsResponse
{
    public List<PhotoDiaryRequestSummary> Items { get; set; } = [];
}
