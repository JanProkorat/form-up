using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Features.Client.SubmitOnboarding;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using FluentValidation.TestHelper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Client;

/// <summary>
/// Dislikes and habits on POST /client/onboarding: integration persistence plus validator boundaries.
/// </summary>
[Collection(TestCollection.Shard3)]
public class SubmitOnboardingDislikesHabitsTests(FitnessApiFactory factory)
{
    private static object Body(string[] dislikes, string[] habits) => new
    {
        Age = 25,
        Sex = "Male",
        HeightCm = 180,
        WeightKg = 80,
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
        PreferredActivities = new[] { "strength" },
        Injuries = new[] { "none" },
        MealsPerDay = "FourToFive",
        DietaryStyle = "Standard",
        Allergies = new[] { "none" },
        DietRating = 3,
        PlanExperience = "TriedFailed",
        PastBlockers = new[] { "time" },
        PrimaryMotivation = "Appearance",
        Dislikes = dislikes,
        Habits = habits,
    };

    [Fact]
    public async Task SubmitOnboarding_WithDislikesAndHabits_PersistsTrimmedLists()
    {
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);

        var response = await client.Http.PostAsJsonAsync(
            "/client/onboarding",
            Body(["Mushrooms", " Olives "], ["Cooks on Sundays"]),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stored = await db.ClientOnboardingData.AsNoTracking()
            .SingleAsync(d => d.ClientProfileId == client.ProfileId, TestContext.Current.CancellationToken);
        stored.Dislikes.Should().Equal("Mushrooms", "Olives");
        stored.Habits.Should().Equal("Cooks on Sundays");
    }

    [Fact]
    public async Task SubmitOnboarding_BlankDislike_Returns400()
    {
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);

        var response = await client.Http.PostAsJsonAsync(
            "/client/onboarding", Body([" "], []), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(20, true)]
    [InlineData(21, false)]
    public void Validate_DislikeCount_Boundary(int count, bool expectedValid)
    {
        var request = ValidRequest();
        request.Dislikes = Enumerable.Range(0, count).Select(i => $"item{i}").ToList();

        new SubmitOnboardingValidator().TestValidate(request).IsValid.Should().Be(expectedValid);
    }

    [Theory]
    [InlineData(20, true)]
    [InlineData(21, false)]
    public void Validate_HabitCount_Boundary(int count, bool expectedValid)
    {
        var request = ValidRequest();
        request.Habits = Enumerable.Range(0, count).Select(i => $"item{i}").ToList();

        new SubmitOnboardingValidator().TestValidate(request).IsValid.Should().Be(expectedValid);
    }

    [Theory]
    [InlineData(60, true)]
    [InlineData(61, false)]
    public void Validate_ItemLength_Boundary(int length, bool expectedValid)
    {
        var request = ValidRequest();
        request.Dislikes = [new string('a', length)];
        request.Habits = [new string('b', length)];

        new SubmitOnboardingValidator().TestValidate(request).IsValid.Should().Be(expectedValid);
    }

    [Fact]
    public void Validate_BlankHabit_Fails()
    {
        var request = ValidRequest();
        request.Habits = ["  "];

        new SubmitOnboardingValidator().TestValidate(request).IsValid.Should().BeFalse();
    }

    private static SubmitOnboardingRequest ValidRequest() => new()
    {
        Age = 25,
        Sex = "Male",
        HeightCm = 180,
        WeightKg = 80,
        BodyType = "Mesomorph",
        PrimaryGoal = "GainMuscle",
        TimeHorizon = "SixMonths",
        JobType = "Sedentary",
        SleepHours = 7,
        StressLevel = 3,
        CurrentTrainingFrequency = "Regular",
        DesiredTrainingFrequency = "FourPerWeek",
        FitnessRating = 6,
        MealsPerDay = "FourToFive",
        DietaryStyle = "Standard",
        PlanExperience = "TriedFailed",
        PrimaryMotivation = "Appearance",
    };
}
