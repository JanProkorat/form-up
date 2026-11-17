using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Infrastructure.Services.Billing;

/// <summary>
/// How an immediate second-role add-on charge ended at the payment provider.
/// </summary>
/// <param name="Outcome">The outcome.</param>
/// <param name="PaymentUrl">The hosted payment page when the add-on awaits payment; null otherwise. A bearer link, so never logged.</param>
public sealed record AddOnChargeResult(AddOnChargeOutcome Outcome, string? PaymentUrl = null)
{
    /// <summary>The add-on was added and paid.</summary>
    public static AddOnChargeResult Charged { get; } = new(AddOnChargeOutcome.Charged);

    /// <summary>The card was declined.</summary>
    public static AddOnChargeResult Declined { get; } = new(AddOnChargeOutcome.Declined);

    /// <summary>Another pending provider update blocks the charge.</summary>
    public static AddOnChargeResult PlanChangePending { get; } = new(AddOnChargeOutcome.PlanChangePending);

    /// <summary>The add-on is held until the coach pays on the hosted page.</summary>
    /// <param name="paymentUrl">The hosted payment page.</param>
    public static AddOnChargeResult AwaitingPayment(string paymentUrl) => new(AddOnChargeOutcome.AwaitingPayment, paymentUrl);
}
