using System.ComponentModel.DataAnnotations;
using FormUp.Application.Domain.Common;
using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Domain.Entities;

/// <summary>
/// Log of one occurrence of a rule for one client. Unique per (rule, client, occurrence) so an
/// occurrence can never be sent twice.
/// </summary>
public class AutomationRun : TimestampableEntity
{
    /// <summary>Foreign key to the rule.</summary>
    public long AutomationRuleId { get; set; }

    /// <summary>Foreign key to the client.</summary>
    public long ClientProfileId { get; set; }

    /// <summary>Opaque key of the occurrence, e.g. the scheduled instant or an event and plan id.</summary>
    [MaxLength(100)]
    public required string Occurrence { get; set; }

    /// <summary>Outcome of the occurrence.</summary>
    public AutomationRunStatus Status { get; set; }

    /// <summary>Why the occurrence was skipped; set only when skipped.</summary>
    public AutomationSkipReason? SkipReason { get; set; }

    /// <summary>When the request was sent (UTC).</summary>
    public DateTime? SentAt { get; set; }

    /// <summary>When the client answered (UTC).</summary>
    public DateTime? AnsweredAt { get; set; }

    /// <summary>
    /// Public id of what the run delivered: the pending questionnaire response, the photo request or
    /// the in-app notification of a measurement request. Null on skipped and failed runs.
    /// </summary>
    public Guid? DeliveredEntityId { get; set; }

    /// <summary>How many sends of this occurrence have failed so far; 0 until the first failure.</summary>
    public int AttemptCount { get; set; }

    /// <summary>
    /// When the worker retries a failed send (UTC). A Failed run with a value is waiting for a
    /// retry; a Failed run without one is final.
    /// </summary>
    public DateTime? NextRetryAt { get; set; }

    /// <summary>Navigation to the rule.</summary>
    public AutomationRule AutomationRule { get; set; } = null!;

    /// <summary>Navigation to the client.</summary>
    public ClientProfile ClientProfile { get; set; } = null!;
}
