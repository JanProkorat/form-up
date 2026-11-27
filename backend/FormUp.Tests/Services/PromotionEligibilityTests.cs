using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Services;

/// <summary>
/// Tests for <see cref="PromotionEligibility"/>, the code lookup and rule order the checkout and the apply-code route
/// share. The rules are proven through the endpoints; this class pins what only the shared helper decides: the lookup and
/// the order the first failing rule is reported in.
/// </summary>
[Collection(TestCollection.Name)]
public class PromotionEligibilityTests(FitnessApiFactory factory) : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Leaves the promotions table empty for the next test class.</summary>
    public void Dispose() => PromotionBuilder.ClearAsync(factory).GetAwaiter().GetResult();

    private async Task<T> WithDbAsync<T>(Func<ApplicationDbContext, Task<T>> run)
    {
        using var scope = factory.Services.CreateScope();

        return await run(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
    }

    [Fact]
    public async Task FindByCode_ACodePromotionWithoutAStripePromotionCode_IsFound_IgnoringCase()
    {
        await PromotionBuilder.ClearAsync(factory);
        var promotion = await PromotionBuilder.CreateAsync(factory, ["pro"], configure: p => p.StripePromotionCodeId = null);

        var found = await WithDbAsync(db =>
            PromotionEligibility.FindByCodeAsync(db, promotion.Code!.ToLowerInvariant(), DateTimeOffset.UtcNow, Ct));

        found.Should().NotBeNull();
        found!.Id.Should().Be(promotion.Id);
    }

    [Fact]
    public async Task FindByCode_WhenSeveralPromotionsOnceSharedTheCode_TakesTheLatestClaimFrom_AndSkipsEndedOnes()
    {
        await PromotionBuilder.ClearAsync(factory);
        const string code = "SHAREDCODE";
        await PromotionBuilder.CreateAsync(factory, ["pro"], configure: p =>
        {
            p.CodeNormalized = code;
            p.ClaimFrom = DateTimeOffset.UtcNow.AddDays(-30);
        });
        var newer = await PromotionBuilder.CreateAsync(factory, ["pro"], configure: p =>
        {
            p.CodeNormalized = code;
            p.ClaimFrom = DateTimeOffset.UtcNow.AddDays(-5);
        });
        await PromotionBuilder.CreateAsync(factory, ["pro"], configure: p =>
        {
            p.CodeNormalized = code;
            p.ClaimFrom = DateTimeOffset.UtcNow.AddDays(-1);
            p.EndedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        });

        var found = await WithDbAsync(db => PromotionEligibility.FindByCodeAsync(db, code, DateTimeOffset.UtcNow, Ct));

        found!.Id.Should().Be(newer.Id);
    }

    [Fact]
    public async Task FindByCode_AutomaticPromotionsAreNeverFoundByCode()
    {
        await PromotionBuilder.ClearAsync(factory);
        var automatic = await PromotionBuilder.CreateAsync(factory, ["pro"], PromotionMode.Automatic, p => p.CodeNormalized = "AUTOCODE");

        var found = await WithDbAsync(db => PromotionEligibility.FindByCodeAsync(db, "AUTOCODE", DateTimeOffset.UtcNow, Ct));

        found.Should().BeNull();
        automatic.Mode.Should().Be(PromotionMode.Automatic);
    }

    [Fact]
    public async Task Check_AQualifyingCoach_GetsNoRefusal()
    {
        await PromotionBuilder.ClearAsync(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var promotion = await PromotionBuilder.CreateAsync(factory, ["pro"]);

        var refusal = await WithDbAsync(db => PromotionEligibility.CheckAsync(
            db, promotion, "pro", BillingInterval.Annual, coach.ProfileId, null, DateTimeOffset.UtcNow, Ct));

        refusal.Should().BeNull();
    }

    [Fact]
    public async Task Check_APromotionFailingSeveralRules_ReportsPlanBeforeIntervalBeforeUsedUp()
    {
        await PromotionBuilder.ClearAsync(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var promotion = await PromotionBuilder.CreateAsync(factory, ["pro"], configure: p =>
        {
            p.BillingPeriod = PromotionBillingPeriod.Monthly;
            p.MaxUses = 0;
            p.NewCoachesOnly = true;
        });

        var wrongPlan = await Check(promotion, "other", BillingInterval.Annual, coach.ProfileId);
        var wrongInterval = await Check(promotion, "pro", BillingInterval.Annual, coach.ProfileId);
        var usedUp = await Check(promotion, "pro", BillingInterval.Monthly, coach.ProfileId);

        wrongPlan!.ErrorCode.Should().Be(ErrorCodes.PromotionCodePlanNotEligible);
        wrongInterval!.ErrorCode.Should().Be(ErrorCodes.PromotionCodeIntervalNotEligible);
        usedUp!.ErrorCode.Should().Be(ErrorCodes.PromotionCodeUsedUp);
    }

    [Fact]
    public async Task Check_NewCoachesOnly_RefusesACoachWhoPaidBefore_ButNotOneWhoNeverDid()
    {
        await PromotionBuilder.ClearAsync(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var promotion = await PromotionBuilder.CreateAsync(factory, ["pro"], configure: p => p.NewCoachesOnly = true);

        var neverPaid = await WithDbAsync(db => PromotionEligibility.CheckAsync(
            db, promotion, "pro", BillingInterval.Monthly, coach.ProfileId, null, DateTimeOffset.UtcNow, Ct));
        var paidBefore = await WithDbAsync(db => PromotionEligibility.CheckAsync(
            db, promotion, "pro", BillingInterval.Monthly, coach.ProfileId, DateTimeOffset.UtcNow.AddDays(-40), DateTimeOffset.UtcNow, Ct));

        neverPaid.Should().BeNull();
        paidBefore!.ErrorCode.Should().Be(ErrorCodes.PromotionCodeNewCoachesOnly);
    }

    private Task<PromotionRefusal?> Check(
        FormUp.Application.Domain.Entities.Promotion promotion, string plan, BillingInterval interval, long profileId) =>
        WithDbAsync(db => PromotionEligibility.CheckAsync(
            db, promotion, plan, interval, profileId, null, DateTimeOffset.UtcNow, Ct));
}
