namespace FormUp.Application.Infrastructure.Services.Billing;

/// <summary>
/// The outcome of cancelling a subscription now and refunding its unused days.
/// </summary>
/// <param name="RefundedMinorUnits">What went back to the card in this call, in the smallest currency unit; 0 when nothing was due or nothing could be refunded.</param>
/// <param name="Currency">ISO 4217 currency of the refund, or null when no payment was found.</param>
public sealed record CancelRefundResult(long RefundedMinorUnits, string? Currency)
{
    /// <summary>No refund was made.</summary>
    public static CancelRefundResult None { get; } = new(0, null);
}
