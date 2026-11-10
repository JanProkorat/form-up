using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Features.PhotoDiaryRequests.Shared;

/// <summary>
/// Summary DTO for a photo diary request in the client-facing views (list and get-by-id).
/// </summary>
public class ClientPhotoDiaryRequestSummary
{
    public Guid Id { get; set; }
    public Guid ProfessionalId { get; set; }

    /// <summary>Public id of the coach's professional profile, as in the collaborations list.</summary>
    public Guid ProfessionalPublicId { get; set; }

    /// <summary>The coach's full name.</summary>
    public string ProfessionalName { get; set; } = string.Empty;

    /// <summary>The role the coach holds in the link; null when the request was sent with an invite.</summary>
    public UserRole? ProfessionalRole { get; set; }
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
}
