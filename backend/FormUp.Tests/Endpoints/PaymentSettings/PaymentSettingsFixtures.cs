using System.Text.Json;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.PaymentSettings;

/// <summary>
/// Shared seed and read helpers for the payment settings endpoint tests.
/// </summary>
internal static class PaymentSettingsFixtures
{
    /// <summary>A valid Czech IBAN.</summary>
    public const string ValidIban = "CZ6508000000192000145399";

    /// <summary>A second valid IBAN, from another country.</summary>
    public const string OtherIban = "SK3112000000198742637541";

    /// <summary>A third valid IBAN.</summary>
    public const string ThirdIban = "GB82WEST12345698765432";

    /// <summary>Password given to coaches built with a password.</summary>
    public const string Password = "CoachPass1!";

    /// <summary>
    /// Subscribes the coach to a fresh plan whose payments flag and subscription status are as given. The packages
    /// flag follows the payments flag unless <paramref name="canUsePackages"/> is set.
    /// </summary>
    public static async Task SubscribeAsync(
        FitnessApiFactory factory,
        Actor coach,
        bool canUsePayments,
        SubscriptionStatus status = SubscriptionStatus.Active,
        bool? canUsePackages = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var plan = new SubscriptionPlan
        {
            Code = $"payments-{Guid.NewGuid():N}",
            NameCs = "Plán",
            NameEn = "Plan",
            NameDe = "Plan",
            BothRolesIncluded = true,
            CanUsePayments = canUsePayments,
            CanUsePackages = canUsePackages ?? canUsePayments,
            CanUsePlanTemplates = true,
            CanUseRecipes = true,
            CanUseIngredients = true,
            CanUseProgressPhotos = true,
            CanUseWeightAndMeasurements = true,
            CanUseAutomations = true,
            PriceMinorUnits = 99900,
            Currency = "CZK",
            BillingInterval = BillingInterval.Monthly,
            IsActive = true
        };
        var price = SubscriptionPlanPriceBuilder.OpenPriceFor(plan);
        db.SubscriptionPlans.Add(plan);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.CoachSubscriptions.Add(new CoachSubscription
        {
            ProfessionalProfileId = coach.ProfileId,
            SubscriptionPlanId = plan.Id,
            SubscriptionPlanPriceId = price.Id,
            Status = status
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Writes a bank account straight onto the coach's settings row, creating the row if needed.
    /// </summary>
    public static async Task SetBankAccountAsync(
        FitnessApiFactory factory, Actor coach, string iban, string accountHolder = "Martin Král")
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var settings = await db.CoachPaymentSettings.FirstOrDefaultAsync(
            s => s.ProfessionalProfileId == coach.ProfileId, TestContext.Current.CancellationToken);

        if (settings is null)
        {
            settings = new CoachPaymentSettings { ProfessionalProfileId = coach.ProfileId };
            db.CoachPaymentSettings.Add(settings);
        }

        settings.Iban = iban;
        settings.AccountHolder = accountHolder;
        settings.BankAccountChangedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Reads the coach's settings row, or null when none exists.
    /// </summary>
    public static async Task<CoachPaymentSettings?> ReadSettingsAsync(FitnessApiFactory factory, Actor coach)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.CoachPaymentSettings.AsNoTracking().FirstOrDefaultAsync(
            s => s.ProfessionalProfileId == coach.ProfileId, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Reads every bank account change recorded for the coach.
    /// </summary>
    public static async Task<List<CoachBankAccountChange>> ReadChangesAsync(FitnessApiFactory factory, Actor coach)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.CoachBankAccountChanges.AsNoTracking()
            .Where(c => c.ProfessionalProfileId == coach.ProfileId)
            .OrderBy(c => c.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Reads the <c>errors[].code</c> values of a coded 400 response.
    /// </summary>
    public static async Task<List<string?>> ErrorCodesAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("errors", out var errors))
        {
            return [];
        }

        return errors.EnumerateArray()
            .Select(e => e.TryGetProperty("code", out var code) ? code.GetString() : null)
            .ToList();
    }

    /// <summary>
    /// Reads the top-level <c>errorCode</c> of a problem-details response.
    /// </summary>
    public static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);

        return document.RootElement.TryGetProperty("errorCode", out var code) ? code.GetString() : null;
    }
}
