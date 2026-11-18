using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Services.Billing;

namespace FormUp.Application.Features.Billing.PreviewPlanChange;

/// <summary>
/// What moving to another paid plan would do: when it starts, what is charged today and what the regular bill becomes.
/// </summary>
public class PreviewPlanChangeResponse
{
    /// <summary>Code of the plan the coach asked about.</summary>
    public string PlanCode { get; set; } = string.Empty;

    /// <summary>Whether the change would apply now (upgrade) or at the next renewal (downgrade).</summary>
    public PlanChangeTiming Timing { get; set; }

    /// <summary>How often the target plan bills.</summary>
    public BillingInterval BillingInterval { get; set; }

    /// <summary>ISO 4217 currency of every amount.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>The prorated amount charged now including tax; zero when the change is scheduled for renewal.</summary>
    public long TodayAmountMinorUnits { get; set; }

    /// <summary>The tax part of <see cref="TodayAmountMinorUnits"/>; zero when no VAT is charged.</summary>
    public long TodayTaxMinorUnits { get; set; }

    /// <summary>
    /// The regular bill per billing interval from <see cref="NextChargeAt"/> on: the target plan, plus the second-role
    /// add-on when the coach would pay it. List price before tax and before any promotion.
    /// </summary>
    public long NextAmountMinorUnits { get; set; }

    /// <summary>When the current billing period ends and the regular bill starts; null when unknown.</summary>
    public DateTimeOffset? NextChargeAt { get; set; }

    /// <summary>True when the upgrade also drops the switch to Free that is scheduled for the period end.</summary>
    public bool CancelsScheduledSwitchToFree { get; set; }

    /// <summary>VAT handling: with <see cref="BillingTaxMode.None"/> there is no VAT line.</summary>
    public BillingTaxMode TaxMode { get; set; }
}
