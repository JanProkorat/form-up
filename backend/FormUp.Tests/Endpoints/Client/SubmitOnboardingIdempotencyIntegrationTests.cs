using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Client;

/// <summary>
/// Integration test for the re-submit (idempotent) path of
/// <c>SubmitOnboardingEndpoint</c> against real PostgreSQL (Testcontainers).
///
/// The unit tests in <c>SubmitOnboardingEndpointTests</c> are mock-based and seed
/// <c>OnboardingData</c> with <c>NutritionTargets == null</c>, so they only exercise the
/// create branch. This test proves the no-duplicate-child invariant the #1036 split relies
/// on: a second submit must find the already-loaded <c>ClientNutritionTargets</c> row,
/// mutate it in place, and never <c>Add</c> a second one — the unique index on
/// <c>ClientOnboardingDataId</c> is the thing actually being tested, so it needs a real
/// database, not a mock that would silently accept two rows.
/// </summary>
[Collection(TestCollection.Shard3)]
public class SubmitOnboardingIdempotencyIntegrationTests(FitnessApiFactory factory)
{
    private static string UniqueEmail() => $"{Guid.NewGuid():N}@onboarding-idempotency.com";

    private static object BuildRequest(decimal weightKg) => new
    {
        Age = 25,
        Sex = "Male",
        HeightCm = 180,
        WeightKg = weightKg,
        TargetWeightKg = 75,
        BodyType = "Mesomorph",
        PrimaryGoal = "GainMuscle",
        TimeHorizon = "SixMonths",
        JobType = "Sedentary",
        SleepHours = 7,
        StressLevel = 3,
        CurrentTrainingFrequency = "Regular",
        DesiredTrainingFrequency = "FourPerWeek",
        FitnessRating = 6,
        GymAccess = "Yes",
        PreferredActivities = new[] { "strength", "cardio" },
        Injuries = new[] { "none" },
        MealsPerDay = "FourToFive",
        DietaryStyle = "Standard",
        Allergies = new[] { "none" },
        DietRating = 3,
        PlanExperience = "TriedFailed",
        PastBlockers = new[] { "time", "motivation" },
        PrimaryMotivation = "Appearance",
    };

    /// <summary>
    /// Two submits for the same client must leave exactly one
    /// <see cref="Application.Domain.Entities.ClientNutritionTargets"/> row — the second
    /// submit mutates the row created by the first rather than inserting a duplicate.
    /// </summary>
    [Fact]
    public async Task SubmitOnboarding_ResubmittedWithDifferentWeight_MutatesSameRow_NoDuplicate()
    {
        var http = factory.CreateClient();
        var email = UniqueEmail();

        await TestHelpers.RegisterAsync(http, email, "TestPass1!", "Test", "Client", "Client");
        var (token, _) = await TestHelpers.LoginAsync(http, email, "TestPass1!");
        TestHelpers.SetBearerToken(http, token);

        var firstResponse = await http.PostAsJsonAsync(
            "/client/onboarding", BuildRequest(weightKg: 80), TestContext.Current.CancellationToken);
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        long onboardingDataId;
        long firstTargetsId;
        decimal firstBmr;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var user = await db.Users.FirstAsync(
                u => u.Email == email, TestContext.Current.CancellationToken);
            var profile = await db.ClientProfiles
                .Include(cp => cp.OnboardingData)
                .FirstAsync(cp => cp.UserId == user.Id, TestContext.Current.CancellationToken);

            profile.OnboardingData.Should().NotBeNull();
            onboardingDataId = profile.OnboardingData!.Id;

            // SingleAsync (not FirstAsync) is the assertion: it throws if the first submit
            // ever inserted more than one targets row for this onboarding id.
            var targets = await db.ClientNutritionTargets.SingleAsync(
                t => t.ClientOnboardingDataId == onboardingDataId, TestContext.Current.CancellationToken);
            firstTargetsId = targets.Id;
            firstBmr = targets.Bmr;
        }

        // Re-submit with a materially different weight so the recalculated Bmr differs —
        // proving the second submit actually wrote through, not just left the row untouched.
        var secondResponse = await http.PostAsJsonAsync(
            "/client/onboarding", BuildRequest(weightKg: 95), TestContext.Current.CancellationToken);
        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // Same guard: SingleAsync throws on a duplicate row, which is exactly the
            // unique-index violation an incorrect re-submit path would have produced.
            var targets = await db.ClientNutritionTargets.SingleAsync(
                t => t.ClientOnboardingDataId == onboardingDataId, TestContext.Current.CancellationToken);

            targets.Id.Should().Be(firstTargetsId,
                "the second submit must reuse the existing child row, not replace it");
            targets.Bmr.Should().NotBe(firstBmr,
                "the recalculated Bmr from the new WeightKg must have been persisted");
        }
    }
}
