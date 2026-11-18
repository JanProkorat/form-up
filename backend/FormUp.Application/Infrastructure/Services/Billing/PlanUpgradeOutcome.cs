namespace FormUp.Application.Infrastructure.Services.Billing;

/// <summary>
/// How an immediate plan upgrade ended at the payment provider: charged and applied, or waiting for the coach to
/// authenticate the payment on the provider's hosted page.
/// </summary>
/// <param name="PaymentUrl">The hosted payment page when the upgrade awaits payment; null when it was applied.</param>
public sealed record PlanUpgradeOutcome(string? PaymentUrl)
{
    /// <summary>The card was charged and the upgrade applied at once.</summary>
    public static PlanUpgradeOutcome Applied { get; } = new((string?)null);

    /// <summary>True when the coach must still pay on <see cref="PaymentUrl"/> before the provider applies the upgrade.</summary>
    public bool IsAwaitingPayment => PaymentUrl is not null;

    /// <summary>The upgrade is held until the coach pays on the hosted page.</summary>
    /// <param name="paymentUrl">The hosted payment page. A bearer link, so never logged.</param>
    public static PlanUpgradeOutcome AwaitingPayment(string paymentUrl) => new(paymentUrl);
}
