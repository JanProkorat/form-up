using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Services.Billing;

namespace FormUp.Application.Features.Billing.PreviewSecondRoleAddOn;

/// <summary>
/// What adding the second-role add-on would cost: the new bill and today's prorated charge.
/// </summary>
public class PreviewSecondRoleAddOnResponse
{
    /// <summary>The plan line of the bill, per billing interval, in the smallest currency unit.</summary>
    public long PlanAmountMinorUnits { get; set; }

    /// <summary>The add-on line of the bill, per billing interval.</summary>
    public long AddOnAmountMinorUnits { get; set; }

    /// <summary>The recurring total after the add-on: plan plus add-on, before any tax.</summary>
    public long NewTotalMinorUnits { get; set; }

    /// <summary>How often the bill recurs.</summary>
    public BillingInterval BillingInterval { get; set; }

    /// <summary>ISO 4217 currency of every amount.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>The prorated amount due today including tax.</summary>
    public long TodayAmountMinorUnits { get; set; }

    /// <summary>The tax part of <see cref="TodayAmountMinorUnits"/>; zero when no VAT is charged.</summary>
    public long TodayTaxMinorUnits { get; set; }

    /// <summary>When the current billing period ends and the new total starts to recur; null when unknown.</summary>
    public DateTimeOffset? PeriodEndsAt { get; set; }

    /// <summary>VAT handling: with <see cref="BillingTaxMode.None"/> there is no VAT line.</summary>
    public BillingTaxMode TaxMode { get; set; }
}
