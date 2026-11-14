using FormUp.Application.Domain.Common;

namespace FormUp.Application.Domain.Entities;

/// <summary>
/// A payment-provider webhook event that was applied. Written in the same save as the change it caused, so a repeated
/// delivery finds the id and does nothing, and a failed one leaves no row and is retried by the provider.
/// </summary>
public class ProcessedWebhookEvent : TimestampableEntity
{
    /// <summary>
    /// The provider's event id. Unique.
    /// </summary>
    public required string EventId { get; set; }

    /// <summary>
    /// The provider's event type, for diagnostics.
    /// </summary>
    public required string EventType { get; set; }

    /// <summary>
    /// When the provider created the event (UTC).
    /// </summary>
    public DateTimeOffset EventCreatedAt { get; set; }
}
