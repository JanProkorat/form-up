using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Trainers;

/// <summary>
/// Integration tests for the nutrition-profile fields (dislikes, habits, fibre) on
/// GET /trainer/clients/{clientId}, which are gated on the nutrition link capability.
/// </summary>
[Collection(TestCollection.Shard4)]
public class GetClientDashboardNutritionProfileTests(FitnessApiFactory factory)
{
    private async Task SeedProfileAsync(Actor client)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var onboarding = EntityBuilder.ClientOnboardingData
            .WithGeneratedId()
            .WithClientProfileId(client.ProfileId)
            .WithDislikes("Mushrooms")
            .WithHabits("Cooks on Sundays")
            .Build();
        db.ClientOnboardingData.Add(onboarding);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.ClientNutritionTargets.Add(EntityBuilder.ClientNutritionTargets
            .WithGeneratedId()
            .WithClientOnboardingDataId(onboarding.Id)
            .WithFibreGrams(28m)
            .Build());
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<OnboardingBody> GetOnboardingAsync(Actor trainer, Actor client)
    {
        var response = await trainer.Http.GetAsync(
            $"/trainer/clients/{client.PublicId}", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<DashboardBody>(
            cancellationToken: TestContext.Current.CancellationToken);
        return body!.Onboarding!;
    }

    [Fact]
    public async Task GetDashboard_NutritionCapability_ExposesDislikesHabitsAndFibre()
    {
        var trainer = await TestActors.Nutritionist(factory).CreateAsync(TestContext.Current.CancellationToken);
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        await TestActors.Link(factory, trainer, client).CreateAsync(TestContext.Current.CancellationToken);
        await SeedProfileAsync(client);

        var onboarding = await GetOnboardingAsync(trainer, client);

        onboarding.Dislikes.Should().Equal("Mushrooms");
        onboarding.Habits.Should().Equal("Cooks on Sundays");
        onboarding.FibreGrams.Should().Be(28m);
    }

    [Fact]
    public async Task GetDashboard_TrainingOnlyLink_HidesDislikesHabitsAndFibre()
    {
        var trainer = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        await TestActors.Link(factory, trainer, client)
            .CanViewNutritionPlans(false)
            .CreateAsync(TestContext.Current.CancellationToken);
        await SeedProfileAsync(client);

        var onboarding = await GetOnboardingAsync(trainer, client);

        onboarding.Dislikes.Should().BeNull();
        onboarding.Habits.Should().BeNull();
        onboarding.FibreGrams.Should().BeNull();
    }

    // ApplyTimestamps overwrites DateCreated on insert and DateUpdated on save, so dates are pinned
    // with ExecuteUpdateAsync afterwards, which bypasses SaveChanges.
    private async Task PinDatesAsync(
        Actor client,
        DateTime onboardingCreated,
        DateTime? onboardingUpdated,
        DateTime targetsCreated,
        DateTime? targetsUpdated)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        await db.ClientOnboardingData
            .Where(od => od.ClientProfileId == client.ProfileId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(od => od.DateCreated, onboardingCreated)
                    .SetProperty(od => od.DateUpdated, onboardingUpdated),
                TestContext.Current.CancellationToken);
        await db.ClientNutritionTargets
            .Where(t => t.ClientOnboardingData.ClientProfileId == client.ProfileId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(t => t.DateCreated, targetsCreated)
                    .SetProperty(t => t.DateUpdated, targetsUpdated),
                TestContext.Current.CancellationToken);
    }

    private static readonly DateTime Day1 = new(2026, 1, 10, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Day2 = new(2026, 2, 11, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Day3 = new(2026, 3, 12, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task GetDashboard_NutritionCaller_TargetsUpdatedLater_ReturnsTargetsDate()
    {
        var trainer = await TestActors.Nutritionist(factory).CreateAsync(TestContext.Current.CancellationToken);
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        await TestActors.Link(factory, trainer, client).CreateAsync(TestContext.Current.CancellationToken);
        await SeedProfileAsync(client);
        await PinDatesAsync(client, Day1, Day2, Day1, Day3);

        var onboarding = await GetOnboardingAsync(trainer, client);

        onboarding.DateUpdated.Should().BeCloseTo(Day3, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task GetDashboard_NutritionCaller_NeverUpdated_ReturnsLaterCreationDate()
    {
        var trainer = await TestActors.Nutritionist(factory).CreateAsync(TestContext.Current.CancellationToken);
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        await TestActors.Link(factory, trainer, client).CreateAsync(TestContext.Current.CancellationToken);
        await SeedProfileAsync(client);
        await PinDatesAsync(client, Day1, null, Day2, null);

        var onboarding = await GetOnboardingAsync(trainer, client);

        onboarding.DateUpdated.Should().BeCloseTo(Day2, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task GetDashboard_TrainingOnlyLink_IgnoresTargetsDate()
    {
        var trainer = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        await TestActors.Link(factory, trainer, client)
            .CanViewNutritionPlans(false)
            .CreateAsync(TestContext.Current.CancellationToken);
        await SeedProfileAsync(client);
        await PinDatesAsync(client, Day1, Day2, Day1, Day3);

        var onboarding = await GetOnboardingAsync(trainer, client);

        onboarding.DateUpdated.Should().BeCloseTo(Day2, TimeSpan.FromMilliseconds(1));
    }

    private sealed record DashboardBody(OnboardingBody? Onboarding);

    private sealed record OnboardingBody(
        List<string>? Dislikes, List<string>? Habits, decimal? FibreGrams, DateTime? DateUpdated);
}
