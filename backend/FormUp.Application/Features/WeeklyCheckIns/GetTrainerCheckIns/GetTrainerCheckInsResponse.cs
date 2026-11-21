using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Features.WeeklyCheckIns.GetTrainerCheckIns;


/// <summary>
/// Response for GET /trainer/weekly-check-ins.
/// </summary>
public class GetTrainerCheckInsResponse
{
    /// <summary>Check-ins for the requested week, filtered to the caller's clients.</summary>
    public List<TrainerCheckInDto> CheckIns { get; set; } = [];
}

/// <summary>One check-in row as seen by the trainer.</summary>
public class TrainerCheckInDto
{
    /// <summary>Check-in identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Client's ApplicationUser.Id.</summary>
    public Guid ClientUserId { get; set; }

    /// <summary>
    /// Client's ClientProfile.PublicId — the id every client-detail link in the app uses
    /// (e.g. /clients/{clientPublicId}). Defaults to Guid.Empty if the client has no
    /// ClientProfile row (should not happen in practice, but the join is null-safe).
    /// </summary>
    public Guid ClientPublicId { get; set; }

    /// <summary>Client's display name.</summary>
    public string ClientName { get; set; } = string.Empty;

    /// <summary>Profession context.</summary>
    public string Profession { get; set; } = string.Empty;

    /// <summary>ISO-week Monday.</summary>
    public DateOnly WeekStartDate { get; set; }

    /// <summary>Flags selected by the client. Empty until responded.</summary>
    public List<CheckInFlag> Flags { get; set; } = [];

    /// <summary>Client's note. Null until responded.</summary>
    public string? Note { get; set; }

    /// <summary>When the scheduler sent this check-in.</summary>
    public DateTime SentAt { get; set; }

    /// <summary>When the client responded. Null if not yet responded.</summary>
    public DateTime? RespondedAt { get; set; }

    /// <summary>When the client dismissed. Null if not dismissed.</summary>
    public DateTime? DismissedByClientAt { get; set; }

    /// <summary>When the trainer marked this reviewed. Null if not reviewed.</summary>
    public DateTime? ReviewedByTrainerAt { get; set; }

    /// <summary>Lifecycle status of the check-in.</summary>
    public string Status { get; set; } = WeeklyCheckInStatus.Pending.ToString();

    /// <summary>UTC deadline by which the client must respond. Null for rows created before v1 deadline feature.</summary>
    public DateTime? DueAt { get; set; }

    /// <summary>Public id of the check-in form this row was answered on.</summary>
    public Guid FormId { get; set; }

    /// <summary>Title of the check-in form this row was answered on.</summary>
    public string FormName { get; set; } = string.Empty;

    /// <summary>Client's IANA time zone id.</summary>
    public string ClientTimeZone { get; set; } = string.Empty;

    /// <summary>True when the client is over the cap of the coach's plan, as on the clients list.</summary>
    public bool ClientIsOutsidePlan { get; set; }
}
