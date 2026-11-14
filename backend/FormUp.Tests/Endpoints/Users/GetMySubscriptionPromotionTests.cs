using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.Users.GetMySubscription;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Users;

/// <summary>
/// Integration tests for the current promotion on <c>GET /users/me/subscription</c>.
/// </summary>
[Collection(TestCollection.Name)]
public class GetMySubscriptionPromotionTests(FitnessApiFactory factory) : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Leaves the promotions table empty for the next test class.</summary>
    public void Dispose() => PromotionBuilder.ClearAsync(factory).GetAwaiter().GetResult();

    private static async Task<GetMySubscriptionResponse> GetAsync(Actor coach)
    {
        var response = await coach.Http.GetAsync("/users/me/subscription", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return (await response.Content.ReadFromJsonAsync<GetMySubscriptionResponse>(JsonOptions, Ct))!;
    }

    private async Task SetCurrentPromotionAsync(long profileId, long promotionId, DateTimeOffset? endsAt)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        await db.CoachSubscriptions
            .Where(cs => cs.ProfessionalProfileId == profileId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(cs => cs.CurrentPromotionId, promotionId)
                    .SetProperty(cs => cs.CurrentPromotionEndsAt, endsAt),
                Ct);
    }

    [Fact]
    public async Task Get_LiveSubscriptionWithACurrentPromotion_ReturnsItsNameValueDurationAndEnd()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, plan);
        var promotion = await PromotionBuilder.CreateAsync(factory, [plan], PromotionMode.Automatic, p =>
        {
            p.Value = 30;
            p.Duration = PromotionDuration.Months;
            p.DurationMonths = 3;
        });
        var endsAt = DateTimeOffset.UtcNow.AddMonths(2);
        await SetCurrentPromotionAsync(coach.ProfileId, promotion.Id, endsAt);

        var response = await GetAsync(coach);

        response.Promotion.Should().NotBeNull();
        response.Promotion!.Name.Should().Be(promotion.Name);
        response.Promotion.Kind.Should().Be(PromotionKind.Percent);
        response.Promotion.Value.Should().Be(30);
        response.Promotion.Duration.Should().Be(PromotionDuration.Months);
        response.Promotion.DurationMonths.Should().Be(3);
        response.Promotion.EndsAt.Should().BeCloseTo(endsAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Get_ForeverPromotion_HasNoEnd()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, plan);
        var promotion = await PromotionBuilder.CreateAsync(factory, [plan]);
        await SetCurrentPromotionAsync(coach.ProfileId, promotion.Id, null);

        (await GetAsync(coach)).Promotion!.EndsAt.Should().BeNull();
    }

    [Fact]
    public async Task Get_WithoutACurrentPromotion_ReturnsNone()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, plan);

        (await GetAsync(coach)).Promotion.Should().BeNull();
    }

    [Fact]
    public async Task Get_PromotionOnASubscriptionThatIsNoLongerLive_IsNotShown()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, plan, SubscriptionStatus.Canceled);
        var promotion = await PromotionBuilder.CreateAsync(factory, [plan]);
        await SetCurrentPromotionAsync(coach.ProfileId, promotion.Id, null);

        (await GetAsync(coach)).Promotion.Should().BeNull();
    }
}
