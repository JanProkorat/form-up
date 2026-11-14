using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using FormUp.Application.Domain.Enums;
using FormUp.Tests.Builders;
using FormUp.Tests.Endpoints.Billing;
using FormUp.Tests.Infrastructure;

namespace FormUp.Tests.Endpoints.AdminPromotions;

/// <summary>
/// Integration tests for <c>GET /admin/promotions</c> (<c>ListPromotionsEndpoint</c>): the derived status, the filter
/// tabs and the used count.
/// </summary>
[Collection(TestCollection.Name)]
public class ListPromotionsEndpointTests(FitnessApiFactory factory) : IDisposable
{
    private const string Route = "/admin/promotions";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Leaves the promotions table empty for the next test class.</summary>
    public void Dispose() => PromotionBuilder.ClearAsync(factory).GetAwaiter().GetResult();

    private sealed record Listed(Guid PromotionId, string Status, int UsedCount);

    private async Task<List<Listed>> ListAsync(Actor admin, string? status = null)
    {
        var response = await admin.Http.GetAsync(status is null ? Route : $"{Route}?status={status}", Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(BillingTestData.Json, Ct);

        return body.GetProperty("items").EnumerateArray()
            .Select(item => new Listed(
                item.GetProperty("promotionId").GetGuid(),
                item.GetProperty("status").GetString()!,
                item.GetProperty("usedCount").GetInt32()))
            .ToList();
    }

    /// <summary>Five promotions: one active, one active with a use, one scheduled, one past its window, one used up.</summary>
    private async Task<Dictionary<string, Guid>> ArrangeAsync()
    {
        await PromotionBuilder.ClearAsync(factory);
        var coachA = await TestActors.Trainer(factory).CreateAsync(Ct);
        var coachB = await TestActors.Trainer(factory).CreateAsync(Ct);

        var active = await PromotionBuilder.CreateAsync(factory, ["pro"]);
        var used = await PromotionBuilder.CreateAsync(factory, ["pro"], configure: p => p.MaxUses = 5);
        var scheduled = await PromotionBuilder.CreateAsync(factory, ["pro"], configure: p => p.ClaimFrom = DateTimeOffset.UtcNow.AddDays(5));
        var closed = await PromotionBuilder.CreateAsync(factory, ["pro"], configure: p =>
        {
            p.ClaimFrom = DateTimeOffset.UtcNow.AddDays(-20);
            p.ClaimUntil = DateTimeOffset.UtcNow.AddDays(-2);
        });
        var usedUp = await PromotionBuilder.CreateAsync(factory, ["pro"], PromotionMode.Automatic, p => p.MaxUses = 2);

        await PromotionBuilder.AddRedemptionAsync(factory, used.Id, coachA.ProfileId);
        await PromotionBuilder.AddRedemptionAsync(factory, usedUp.Id, coachA.ProfileId);
        await PromotionBuilder.AddRedemptionAsync(factory, usedUp.Id, coachB.ProfileId);

        return new Dictionary<string, Guid>
        {
            ["active"] = active.PublicId,
            ["used"] = used.PublicId,
            ["scheduled"] = scheduled.PublicId,
            ["closed"] = closed.PublicId,
            ["usedUp"] = usedUp.PublicId,
        };
    }

    [Fact]
    public async Task List_WithoutAFilter_ReturnsEveryPromotionWithItsDerivedStatusAndUsedCount()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var ids = await ArrangeAsync();

        var all = (await ListAsync(admin)).ToDictionary(item => item.PromotionId);

        all.Should().HaveCount(5);
        all[ids["active"]].Status.Should().Be("Active");
        all[ids["used"]].Should().Match<Listed>(item => item.Status == "Active" && item.UsedCount == 1);
        all[ids["scheduled"]].Status.Should().Be("Scheduled");
        all[ids["closed"]].Status.Should().Be("Ended");
        all[ids["usedUp"]].Should().Match<Listed>(item => item.Status == "Ended" && item.UsedCount == 2);
    }

    [Theory]
    [InlineData("Active", new[] { "active", "used" })]
    [InlineData("Scheduled", new[] { "scheduled" })]
    [InlineData("Ended", new[] { "closed", "usedUp" })]
    [InlineData("All", new[] { "active", "used", "scheduled", "closed", "usedUp" })]
    public async Task List_WithAStatusFilter_ReturnsOnlyThatTab(string status, string[] expected)
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var ids = await ArrangeAsync();

        var listed = await ListAsync(admin, status);

        listed.Select(item => item.PromotionId).Should().BeEquivalentTo(expected.Select(key => ids[key]));
    }

    [Fact]
    public async Task List_WithAnUnknownStatus_Returns400()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);

        var response = await admin.Http.GetAsync($"{Route}?status=Nonsense", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
