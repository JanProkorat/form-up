using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.CoachSubscriptions.GetCoachPlans;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.CoachSubscriptions;

/// <summary>
/// Integration tests for <c>GET /users/me/subscription/plans</c>.
/// </summary>
[Collection(TestCollection.Name)]
public class GetCoachPlansEndpointTests(FitnessApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<GetCoachPlansResponse> GetAsync(Actor coach)
    {
        var response = await coach.Http.GetAsync("/users/me/subscription/plans", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return (await response.Content.ReadFromJsonAsync<GetCoachPlansResponse>(JsonOptions, Ct))!;
    }

    [Fact]
    public async Task Get_ReturnsActivePlansWithNamesPricesCapAndFlags_AndSkipsInactiveOnes()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var inactiveCode = $"inactive-{Guid.NewGuid():N}"[..30];

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var plan = new SubscriptionPlan
            {
                Code = inactiveCode,
                NameCs = "Skrytý",
                NameEn = "Hidden",
                NameDe = "Versteckt",
                Currency = "CZK",
                IsActive = false,
            };
            SubscriptionPlanPriceBuilder.OpenPriceFor(plan);
            db.SubscriptionPlans.Add(plan);
            await db.SaveChangesAsync(Ct);
        }

        var response = await GetAsync(coach);

        response.Plans.Should().NotContain(plan => plan.Code == inactiveCode);
        response.Plans.Should().Contain(plan => plan.Code == SubscriptionPlanCodes.Free);
        response.Plans.Should().BeInAscendingOrder(plan => plan.PriceMinorUnits);

        using var verifyScope = factory.Services.CreateScope();
        var pro = await verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>().SubscriptionPlans
            .AsNoTracking().SingleAsync(plan => plan.Code == "pro", Ct);
        response.Plans.Single(plan => plan.Code == "pro").Should().BeEquivalentTo(new CoachPlanDto
        {
            Code = pro.Code,
            NameCs = pro.NameCs,
            NameEn = pro.NameEn,
            NameDe = pro.NameDe,
            PriceMinorUnits = pro.PriceMinorUnits,
            SecondRoleAddOnPriceMinorUnits = pro.SecondRoleAddOnPriceMinorUnits,
            Currency = pro.Currency,
            BillingInterval = pro.BillingInterval,
            MaxActiveClients = pro.MaxActiveClients,
            BothRolesIncluded = pro.BothRolesIncluded,
            CanCreatePlans = pro.CanCreatePlans,
            CanMessage = pro.CanMessage,
            CanSendQuestionnaires = pro.CanSendQuestionnaires,
            CanUseWeeklyCheckIns = pro.CanUseWeeklyCheckIns,
            CanUsePerClientCheckInConfig = pro.CanUsePerClientCheckInConfig,
            CanUsePayments = pro.CanUsePayments,
        });
    }

    [Fact]
    public async Task Get_CoachWhoseGivenPlanEnded_IsAllowedToRead()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeWithEndedGiftAsync(factory, coach);

        (await GetAsync(coach)).Plans.Should().NotBeEmpty();
    }
}
