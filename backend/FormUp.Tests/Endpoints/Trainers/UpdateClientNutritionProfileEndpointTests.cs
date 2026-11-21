using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Trainers;

/// <summary>
/// Integration tests for PUT /trainer/clients/{ClientId}/nutrition-profile.
/// </summary>
[Collection(TestCollection.Shard3)]
public class UpdateClientNutritionProfileEndpointTests(FitnessApiFactory factory)
{
    private async Task SeedOnboardingAsync(Actor client, bool withTargets, decimal? fibreGrams = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var onboarding = EntityBuilder.ClientOnboardingData
            .WithGeneratedId()
            .WithClientProfileId(client.ProfileId)
            .WithDislikes("Olives")
            .Build();
        db.ClientOnboardingData.Add(onboarding);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        if (withTargets)
        {
            db.ClientNutritionTargets.Add(EntityBuilder.ClientNutritionTargets
                .WithGeneratedId()
                .WithClientOnboardingDataId(onboarding.Id)
                .WithFibreGrams(fibreGrams)
                .Build());
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
    }

    private static object ValidBody(decimal? fibreGrams = 30m) => new
    {
        dislikes = new[] { "Mushrooms", "  Olives " },
        habits = new[] { "Cooks on Sundays" },
        allergies = new[] { "lactose", "nuts" },
        fibreGrams
    };

    [Fact]
    public async Task UpdateNutritionProfile_NoLink_Returns404()
    {
        var trainer = await TestActors.Nutritionist(factory).CreateAsync(TestContext.Current.CancellationToken);
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        await SeedOnboardingAsync(client, withTargets: true);

        var response = await trainer.Http.PutAsJsonAsync(
            $"/trainer/clients/{client.PublicId}/nutrition-profile", ValidBody(), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateNutritionProfile_LinkWithoutNutritionCapability_Returns403_AndDoesNotWrite()
    {
        var trainer = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        await TestActors.Link(factory, trainer, client)
            .CanViewNutritionPlans(false)
            .CreateAsync(TestContext.Current.CancellationToken);
        await SeedOnboardingAsync(client, withTargets: true);

        var response = await trainer.Http.PutAsJsonAsync(
            $"/trainer/clients/{client.PublicId}/nutrition-profile", ValidBody(), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stored = await db.ClientOnboardingData.AsNoTracking()
            .SingleAsync(d => d.ClientProfileId == client.ProfileId, TestContext.Current.CancellationToken);
        stored.Dislikes.Should().Equal("Olives");
    }

    [Fact]
    public async Task UpdateNutritionProfile_NoOnboardingData_Returns409WithErrorCode()
    {
        var trainer = await TestActors.Nutritionist(factory).CreateAsync(TestContext.Current.CancellationToken);
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        await TestActors.Link(factory, trainer, client).CreateAsync(TestContext.Current.CancellationToken);

        var response = await trainer.Http.PutAsJsonAsync(
            $"/trainer/clients/{client.PublicId}/nutrition-profile", ValidBody(), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain(ErrorCodes.ClientNutritionProfileMissing);
    }

    [Fact]
    public async Task UpdateNutritionProfile_FibreSetButNoTargetsRow_Returns409_AndDoesNotWrite()
    {
        var trainer = await TestActors.Nutritionist(factory).CreateAsync(TestContext.Current.CancellationToken);
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        await TestActors.Link(factory, trainer, client).CreateAsync(TestContext.Current.CancellationToken);
        await SeedOnboardingAsync(client, withTargets: false);

        var response = await trainer.Http.PutAsJsonAsync(
            $"/trainer/clients/{client.PublicId}/nutrition-profile", ValidBody(30m), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stored = await db.ClientOnboardingData.AsNoTracking()
            .SingleAsync(d => d.ClientProfileId == client.ProfileId, TestContext.Current.CancellationToken);
        stored.Dislikes.Should().Equal("Olives");
        (await db.ClientNutritionTargets.AnyAsync(
            t => t.ClientOnboardingDataId == stored.Id, TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    [Fact]
    public async Task UpdateNutritionProfile_NoFibreAndNoTargetsRow_UpdatesListsOnly()
    {
        var trainer = await TestActors.Nutritionist(factory).CreateAsync(TestContext.Current.CancellationToken);
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        await TestActors.Link(factory, trainer, client).CreateAsync(TestContext.Current.CancellationToken);
        await SeedOnboardingAsync(client, withTargets: false);

        var response = await trainer.Http.PutAsJsonAsync(
            $"/trainer/clients/{client.PublicId}/nutrition-profile",
            ValidBody(fibreGrams: null),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UpdateNutritionProfile_ValidRequest_PersistsAndIsVisibleOnDashboard()
    {
        var trainer = await TestActors.Nutritionist(factory).CreateAsync(TestContext.Current.CancellationToken);
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        await TestActors.Link(factory, trainer, client).CreateAsync(TestContext.Current.CancellationToken);
        await SeedOnboardingAsync(client, withTargets: true);

        var response = await trainer.Http.PutAsJsonAsync(
            $"/trainer/clients/{client.PublicId}/nutrition-profile", ValidBody(32.5m), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stored = await db.ClientOnboardingData.AsNoTracking()
            .Include(d => d.NutritionTargets)
            .SingleAsync(d => d.ClientProfileId == client.ProfileId, TestContext.Current.CancellationToken);

        stored.Dislikes.Should().Equal("Mushrooms", "Olives");
        stored.Habits.Should().Equal("Cooks on Sundays");
        stored.Allergies.Should().Be("lactose,nuts");
        stored.NutritionTargets!.FibreGrams.Should().Be(32.5m);

        var dashboard = await trainer.Http.GetAsync(
            $"/trainer/clients/{client.PublicId}", TestContext.Current.CancellationToken);
        dashboard.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await dashboard.Content.ReadFromJsonAsync<DashboardBody>(
            cancellationToken: TestContext.Current.CancellationToken);
        body!.Onboarding!.Dislikes.Should().Equal("Mushrooms", "Olives");
        body.Onboarding.Habits.Should().Equal("Cooks on Sundays");
        body.Onboarding.FibreGrams.Should().Be(32.5m);
    }

    [Fact]
    public async Task UpdateNutritionProfile_InvalidBody_Returns400()
    {
        var trainer = await TestActors.Nutritionist(factory).CreateAsync(TestContext.Current.CancellationToken);
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        await TestActors.Link(factory, trainer, client).CreateAsync(TestContext.Current.CancellationToken);

        var response = await trainer.Http.PutAsJsonAsync(
            $"/trainer/clients/{client.PublicId}/nutrition-profile",
            new { dislikes = new[] { " " }, habits = Array.Empty<string>(), allergies = Array.Empty<string>() },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private sealed record DashboardBody(OnboardingBody? Onboarding);

    private sealed record OnboardingBody(List<string>? Dislikes, List<string>? Habits, decimal? FibreGrams);
}
