using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.Trainers.GetClientDashboard;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Trainers;

/// <summary>
/// Integration tests for the relational split of the computed nutrition-target columns
/// out of <see cref="ClientOnboardingData"/> into their own <see cref="ClientNutritionTargets"/>
/// table (one-to-one, cascade delete).
///
/// These use real PostgreSQL (Testcontainers) because <c>GetClientDashboardEndpoint</c>'s
/// <c>OnboardingDataDto</c> mapping must stay byte-identical on the wire even though the
/// source data now lives across two tables joined via <c>Include().ThenInclude()</c> — a
/// mock-based test would not exercise the real join.
/// </summary>
[Collection(TestCollection.Name)]
public class ClientNutritionTargetsSplitIntegrationTests(FitnessApiFactory factory)
{
    // The API serializes enums as strings (JsonStringEnumConverter globally), so use matching
    // deserialization options here — GetClientDashboardResponse.Status (#1094) is this response's
    // first raw enum-typed property; the bare ReadFromJsonAsync<T>() default options that read
    // every other field here fine cannot convert an enum's string representation.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static string UniqueEmail(string tag) => $"{Guid.NewGuid():N}@nutrition-targets-{tag}.com";

    private async Task<(HttpClient Http, long ProfessionalProfileId)> SetupTrainerAsync()
    {
        var http = factory.CreateClient();
        var email = UniqueEmail("trainer");

        await TestHelpers.RegisterAsync(http, email, "TestPass1!", "Test", "Trainer", "Trainer");
        await factory.GrantRoleAsync(email, "Nutritionist");
        var (token, _) = await TestHelpers.LoginAsync(http, email, "TestPass1!");
        TestHelpers.SetBearerToken(http, token);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var user = await db.Users.FirstAsync(
            u => u.Email == email, TestContext.Current.CancellationToken);
        var profile = await db.ProfessionalProfiles.FirstAsync(
            p => p.UserId == user.Id, TestContext.Current.CancellationToken);

        return (http, profile.Id);
    }

    private async Task<(Guid ClientPublicId, long ClientProfileId)> SetupClientAsync()
    {
        var http = factory.CreateClient();
        var email = UniqueEmail("client");

        await TestHelpers.RegisterAsync(http, email, "TestPass1!", "Test", "Client", "Client");
        await TestHelpers.LoginAsync(http, email, "TestPass1!");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var user = await db.Users.FirstAsync(
            u => u.Email == email, TestContext.Current.CancellationToken);
        var profile = await db.ClientProfiles.FirstAsync(
            cp => cp.UserId == user.Id, TestContext.Current.CancellationToken);

        return (profile.PublicId, profile.Id);
    }

    private async Task LinkTrainerToClientAsync(long trainerProfileId, long clientProfileId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.ClientProfessionalLinks.Add(new ClientProfessionalLink
        {
            PublicId = Guid.NewGuid(),
            ProfessionalProfileId = trainerProfileId,
            ClientProfileId = clientProfileId,
            ProfessionalRole = UserRole.Nutritionist,
            ConnectionState = ConnectionState.Connected,
            CanViewTrainingPlans = true,
            CanViewNutritionPlans = true,
            DateCreated = DateTime.UtcNow,
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static ClientOnboardingData BuildOnboardingData(long clientProfileId) => new()
    {
        ClientProfileId = clientProfileId,
        DateOfBirth = new DateTime(1995, 3, 12, 0, 0, 0, DateTimeKind.Utc),
        Sex = BiologicalSex.Male,
        HeightCm = 182,
        WeightKg = 84,
        TargetWeightKg = 78,
        BodyType = BodyType.Mesomorph,
        PrimaryGoal = PrimaryGoal.LoseFat,
        TimeHorizon = TimeHorizon.SixMonths,
        JobType = JobType.Sedentary,
        SleepHours = 7,
        StressLevel = 3,
        CurrentTrainingFrequency = CurrentTrainingFrequency.Regular,
        DesiredTrainingFrequency = DesiredTrainingFrequency.FourPerWeek,
        FitnessRating = 6,
        GymAccess = GymAccess.Yes,
        PreferredActivities = "strength,cardio",
        Injuries = "none",
        MealsPerDay = MealsPerDay.FourToFive,
        DietaryStyle = DietaryStyle.Standard,
        Allergies = "none",
        DietRating = 4,
        PlanExperience = PlanExperience.TriedFailed,
        PastBlockers = "time",
        PrimaryMotivation = PrimaryMotivation.Appearance,
    };

    /// <summary>
    /// A client with both an onboarding row AND its nutrition-targets child row must return
    /// the exact same flat <c>onboarding</c> shape on the dashboard response as before the
    /// split — the wire contract (<c>OnboardingDataDto</c>) is unchanged, only the source
    /// storage moved across two joined tables.
    /// </summary>
    [Fact]
    public async Task Dashboard_OnboardingWithNutritionTargets_ReturnsUnchangedWireShape()
    {
        var (trainerHttp, trainerProfileId) = await SetupTrainerAsync();
        var (clientPublicId, clientProfileId) = await SetupClientAsync();
        await LinkTrainerToClientAsync(trainerProfileId, clientProfileId);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var onboarding = BuildOnboardingData(clientProfileId);
            db.ClientOnboardingData.Add(onboarding);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            db.ClientNutritionTargets.Add(new ClientNutritionTargets
            {
                ClientOnboardingDataId = onboarding.Id,
                DerivedActivityLevel = ActivityLevel.ModeratelyActive,
                DerivedNutritionGoal = NutritionGoal.Cut,
                Bmr = 1780,
                Tdee = 2670,
                AdjustedKcal = 2270,
                ProteinGrams = 190,
                CarbsGrams = 227,
                FatGrams = 63,
                MealDistribution = """{"breakfast":25,"lunch":30,"dinner":25,"snack1":10,"snack2":10}""",
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var response = await trainerHttp.GetAsync(
            $"/trainer/clients/{clientPublicId}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<GetClientDashboardResponse>(
            JsonOptions, TestContext.Current.CancellationToken);

        body!.Onboarding.Should().NotBeNull();
        body.Onboarding!.DerivedActivityLevel.Should().Be(ActivityLevel.ModeratelyActive.ToString());
        body.Onboarding!.DerivedNutritionGoal.Should().Be(NutritionGoal.Cut.ToString());
        body.Onboarding!.Bmr.Should().Be(1780);
        body.Onboarding!.Tdee.Should().Be(2670);
        body.Onboarding!.AdjustedKcal.Should().Be(2270);
        body.Onboarding!.ProteinGrams.Should().Be(190);
        body.Onboarding!.CarbsGrams.Should().Be(227);
        body.Onboarding!.FatGrams.Should().Be(63);
        body.Onboarding!.MealDistribution.Should().Be(
            """{"breakfast":25,"lunch":30,"dinner":25,"snack1":10,"snack2":10}""");
        // Fields untouched by the split still round-trip correctly.
        body.Onboarding!.PrimaryGoal.Should().Be(PrimaryGoal.LoseFat.ToString());
        body.Onboarding!.TargetWeightKg.Should().Be(78);
    }

    /// <summary>
    /// A client whose onboarding row predates the split (or was patched on a single field
    /// that never touched nutrition targets) has no <see cref="ClientNutritionTargets"/>
    /// child row at all. The dashboard must still return 200 with the onboarding baseline
    /// fields populated and the nine target fields simply null — never a 404 or 500 from
    /// the added <c>ThenInclude</c> join.
    /// </summary>
    [Fact]
    public async Task Dashboard_OnboardingWithoutNutritionTargets_ReturnsNullTargetFields()
    {
        var (trainerHttp, trainerProfileId) = await SetupTrainerAsync();
        var (clientPublicId, clientProfileId) = await SetupClientAsync();
        await LinkTrainerToClientAsync(trainerProfileId, clientProfileId);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.ClientOnboardingData.Add(BuildOnboardingData(clientProfileId));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var response = await trainerHttp.GetAsync(
            $"/trainer/clients/{clientPublicId}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<GetClientDashboardResponse>(
            JsonOptions, TestContext.Current.CancellationToken);

        body!.Onboarding.Should().NotBeNull();
        body.Onboarding!.DerivedActivityLevel.Should().BeNull();
        body.Onboarding!.DerivedNutritionGoal.Should().BeNull();
        body.Onboarding!.Bmr.Should().BeNull();
        body.Onboarding!.Tdee.Should().BeNull();
        body.Onboarding!.AdjustedKcal.Should().BeNull();
        body.Onboarding!.ProteinGrams.Should().BeNull();
        body.Onboarding!.CarbsGrams.Should().BeNull();
        body.Onboarding!.FatGrams.Should().BeNull();
        body.Onboarding!.MealDistribution.Should().BeNull();
        // The onboarding baseline (not part of the split) is unaffected by the missing row.
        body.Onboarding!.PrimaryGoal.Should().Be(PrimaryGoal.LoseFat.ToString());
        body.Onboarding!.TargetWeightKg.Should().Be(78);
    }
}
