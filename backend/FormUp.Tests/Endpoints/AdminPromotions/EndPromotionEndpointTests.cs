using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Endpoints.Billing;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.AdminPromotions;

/// <summary>
/// Integration tests for <c>POST /admin/promotions/{PromotionId}/end</c> (<c>EndPromotionEndpoint</c>) against the fake
/// payment provider.
/// </summary>
[Collection(TestCollection.Name)]
public class EndPromotionEndpointTests(FitnessApiFactory factory) : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Leaves the shared fake provider and the promotions table in their defaults for the next test class.</summary>
    public void Dispose()
    {
        BillingTestData.ResetProvider(factory);
        PromotionBuilder.ClearAsync(factory).GetAwaiter().GetResult();
    }

    private static string RouteFor(Guid promotionId) => $"/admin/promotions/{promotionId}/end";

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var raw = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(status, raw);
        raw.Should().Contain(code);
    }

    private async Task<Promotion> ReadPromotionAsync(Guid publicId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.Promotions.AsNoTracking().SingleAsync(p => p.PublicId == publicId, Ct);
    }

    private async Task<List<AuditLog>> AuditRowsAsync(Guid promotionId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityId == promotionId && a.Action == "AdminEndPromotion")
            .ToListAsync(Ct);
    }

    [Fact]
    public async Task End_CodePromotion_DeactivatesTheStripeCode_SetsEndedAt_Audits_AndReturnsItEnded()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var promotion = await PromotionBuilder.CreateAsync(factory, ["pro"]);

        var response = await admin.Http.PostAsJsonAsync(RouteFor(promotion.PublicId), new { }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        fake.DeactivatedPromotionCodes.Should().ContainSingle().Which.Should().Be(promotion.StripePromotionCodeId);
        fake.DeletedCoupons.Should().BeEmpty("coaches keep the discount, so the coupon stays");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(BillingTestData.Json, Ct);
        body.GetProperty("promotionId").GetGuid().Should().Be(promotion.PublicId);
        body.GetProperty("status").GetString().Should().Be("Ended");

        (await ReadPromotionAsync(promotion.PublicId)).EndedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));

        var audit = (await AuditRowsAsync(promotion.PublicId)).Should().ContainSingle().Subject;
        audit.UserId.Should().Be(admin.UserId);
        audit.EntityType.Should().Be(nameof(Promotion));
        audit.NewValues.Should().NotContain(promotion.Code!, "the code text is not written to the audit log");
    }

    [Fact]
    public async Task End_AutomaticPromotion_EndsLocallyWithoutAStripeCall_EvenWhenStripeIsNotConfigured()
    {
        var fake = BillingTestData.ResetProvider(factory);
        fake.IsConfigured = false;
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var promotion = await PromotionBuilder.CreateAsync(factory, ["pro"], PromotionMode.Automatic);

        var response = await admin.Http.PostAsJsonAsync(RouteFor(promotion.PublicId), new { }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        fake.DeactivatedPromotionCodes.Should().BeEmpty();
        (await ReadPromotionAsync(promotion.PublicId)).EndedAt.Should().NotBeNull();
        (await AuditRowsAsync(promotion.PublicId)).Should().ContainSingle();
    }

    [Fact]
    public async Task End_UnknownPromotion_Returns404_AndDoesNotCallStripe()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);

        var response = await admin.Http.PostAsJsonAsync(RouteFor(Guid.NewGuid()), new { }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        fake.DeactivatedPromotionCodes.Should().BeEmpty();
    }

    [Fact]
    public async Task End_PromotionEndedByAnAdminAlready_Returns409_AndDoesNotCallStripeAgain()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var promotion = await PromotionBuilder.CreateAsync(factory, ["pro"]);
        (await admin.Http.PostAsJsonAsync(RouteFor(promotion.PublicId), new { }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await admin.Http.PostAsJsonAsync(RouteFor(promotion.PublicId), new { }, Ct);

        await AssertErrorAsync(second, HttpStatusCode.Conflict, "PROMOTION_ALREADY_ENDED");
        fake.DeactivatedPromotionCodes.Should().ContainSingle();
        (await AuditRowsAsync(promotion.PublicId)).Should().ContainSingle();
    }

    [Fact]
    public async Task End_PromotionWhoseWindowClosed_Returns409_AndDoesNotCallStripe()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var promotion = await PromotionBuilder.CreateAsync(factory, ["pro"], configure: p =>
        {
            p.ClaimFrom = DateTimeOffset.UtcNow.AddDays(-10);
            p.ClaimUntil = DateTimeOffset.UtcNow.AddDays(-1);
        });

        await AssertErrorAsync(
            await admin.Http.PostAsJsonAsync(RouteFor(promotion.PublicId), new { }, Ct), HttpStatusCode.Conflict, "PROMOTION_ALREADY_ENDED");
        fake.DeactivatedPromotionCodes.Should().BeEmpty();
    }

    [Fact]
    public async Task End_CodePromotionWhenStripeIsNotConfigured_Returns503_AndSavesNothing()
    {
        var fake = BillingTestData.ResetProvider(factory);
        fake.IsConfigured = false;
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var promotion = await PromotionBuilder.CreateAsync(factory, ["pro"]);

        await AssertErrorAsync(
            await admin.Http.PostAsJsonAsync(RouteFor(promotion.PublicId), new { }, Ct),
            HttpStatusCode.ServiceUnavailable,
            "BILLING_NOT_CONFIGURED");
        (await ReadPromotionAsync(promotion.PublicId)).EndedAt.Should().BeNull();
        (await AuditRowsAsync(promotion.PublicId)).Should().BeEmpty();
    }

    [Fact]
    public async Task End_WhenStripeFails_Returns502_AndSavesNothing()
    {
        var fake = BillingTestData.ResetProvider(factory);
        fake.Fail = true;
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var promotion = await PromotionBuilder.CreateAsync(factory, ["pro"]);

        await AssertErrorAsync(
            await admin.Http.PostAsJsonAsync(RouteFor(promotion.PublicId), new { }, Ct), HttpStatusCode.BadGateway, "BILLING_PROVIDER_ERROR");
        (await ReadPromotionAsync(promotion.PublicId)).EndedAt.Should().BeNull();
        (await AuditRowsAsync(promotion.PublicId)).Should().BeEmpty();
    }

    [Fact]
    public async Task End_WhenTwoAdminsEndItAtOnce_OneSucceeds_OneGets409_AndOneAuditRowIsWritten()
    {
        BillingTestData.ResetProvider(factory);
        var first = await TestActors.Admin(factory).CreateAsync(Ct);
        var second = await TestActors.Admin(factory).CreateAsync(Ct);
        var promotion = await PromotionBuilder.CreateAsync(factory, ["pro"]);

        var responses = await Task.WhenAll(
            first.Http.PostAsJsonAsync(RouteFor(promotion.PublicId), new { }, Ct),
            second.Http.PostAsJsonAsync(RouteFor(promotion.PublicId), new { }, Ct));

        responses.Select(response => response.StatusCode).Should().BeEquivalentTo([HttpStatusCode.OK, HttpStatusCode.Conflict]);
        (await AuditRowsAsync(promotion.PublicId)).Should().ContainSingle();
    }

    [Fact]
    public async Task End_FreesTheCodeForANewPromotion()
    {
        BillingTestData.ResetProvider(factory);
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var promotion = await PromotionBuilder.CreateAsync(factory, ["pro"]);
        (await admin.Http.PostAsJsonAsync(RouteFor(promotion.PublicId), new { }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var (plan, _) = await BillingTestData.StripePlanAsync(factory);
        await BillingTestData.StripePlanAsync(factory);

        var response = await admin.Http.PostAsJsonAsync(
            "/admin/promotions",
            new
            {
                Name = $"Again {Guid.NewGuid():N}"[..20],
                Kind = "Percent",
                Value = 20,
                Mode = "Code",
                Code = promotion.Code,
                Duration = "Forever",
                ClaimFrom = DateTimeOffset.UtcNow.AddDays(-1),
                PlanCodes = new[] { plan },
            },
            Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
    }
}
