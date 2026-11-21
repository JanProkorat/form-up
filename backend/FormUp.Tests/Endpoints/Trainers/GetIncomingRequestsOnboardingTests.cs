using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static FormUp.Tests.Endpoints.ClientPayments.ClientPaymentFixtures;
using static FormUp.Tests.Endpoints.PaymentSettings.PaymentSettingsFixtures;

namespace FormUp.Tests.Endpoints.Trainers;

/// <summary>
/// <c>GET /trainer/client-requests</c> and <c>GET /trainer/clients/pending</c>: a pending requester's
/// onboarding answers are visible to the addressed coach only, and never after the request is answered;
/// the attached package and the shared-history counts ride along on request rows.
/// </summary>
[Collection(TestCollection.Shard4)]
public class GetIncomingRequestsOnboardingTests(FitnessApiFactory factory)
{
    private record ListResult(List<RequestItem> Requests);

    private record RequestItem(
        Guid PublicId, Guid ClientPublicId, decimal? HeightCm, decimal? WeightKg,
        string? PrimaryGoal, string? DerivedActivityLevel,
        PackageItem? Package, bool ShareHistory, HistoryItem? History);

    private record PackageItem(Guid PublicId, string Name, string Billing, int? IntervalWeeks, decimal Price, string Currency);

    private record HistoryItem(int PlanCount, int MeasurementCount, int PhotoCount);

    private record PendingResult(List<PendingItem> Rows);

    private record PendingItem(Guid PublicId, string Kind, PackageItem? Package, bool ShareHistory, HistoryItem? History);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(Actor Client, Actor Coach)> RequestWithOnboardingAsync()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var profile = await db.ClientProfiles.FirstAsync(p => p.Id == client.ProfileId, Ct);
            profile.HeightCm = 181m;
            profile.WeightKg = 82.5m;
            profile.OnboardingData = new ClientOnboardingData
            {
                HeightCm = 181m,
                WeightKg = 82.5m,
                PrimaryGoal = PrimaryGoal.LoseFat,
                NutritionTargets = new ClientNutritionTargets { DerivedActivityLevel = ActivityLevel.ModeratelyActive },
            };
            await db.SaveChangesAsync(Ct);
        }

        (await client.Http.PostAsJsonAsync("/client/requests", new { ProfessionalPublicId = coach.PublicId }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        return (client, coach);
    }

    private static async Task<List<RequestItem>> ListAsync(Actor coach)
    {
        var response = await coach.Http.GetAsync("/trainer/client-requests", Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<ListResult>(cancellationToken: Ct))!.Requests;
    }

    [Fact]
    public async Task AddresseeCoach_SeesClientIdAndOnboardingAnswers()
    {
        var (client, coach) = await RequestWithOnboardingAsync();

        var item = (await ListAsync(coach)).Should().ContainSingle().Subject;

        item.ClientPublicId.Should().Be(client.PublicId);
        item.HeightCm.Should().Be(181m);
        item.WeightKg.Should().Be(82.5m);
        item.PrimaryGoal.Should().Be(PrimaryGoal.LoseFat.ToString());
        item.DerivedActivityLevel.Should().Be(ActivityLevel.ModeratelyActive.ToString());
    }

    [Fact]
    public async Task AnotherCoach_DoesNotSeeTheRequestOrItsAnswers()
    {
        await RequestWithOnboardingAsync();
        var other = await TestActors.Trainer(factory).CreateAsync(Ct);

        (await ListAsync(other)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(ClientRequestStatus.Accepted)]
    [InlineData(ClientRequestStatus.Rejected)]
    [InlineData(ClientRequestStatus.Cancelled)]
    public async Task AnsweredRequest_IsNotListed_SoAnswersDoNotLeak(ClientRequestStatus status)
    {
        var (client, coach) = await RequestWithOnboardingAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var request = await db.ClientRequests.SingleAsync(r => r.ClientProfileId == client.ProfileId, Ct);
            request.Status = status;
            await db.SaveChangesAsync(Ct);
        }

        (await ListAsync(coach)).Should().BeEmpty();
    }

    [Fact]
    public async Task ClientWithoutOnboarding_HasNullAnswers()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        (await client.Http.PostAsJsonAsync("/client/requests", new { ProfessionalPublicId = coach.PublicId }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var item = (await ListAsync(coach)).Should().ContainSingle().Subject;

        item.PrimaryGoal.Should().BeNull();
        item.DerivedActivityLevel.Should().BeNull();
    }

    private async Task<(Actor Client, Actor Coach)> SendRequestAsync(object body, Actor? existingCoach = null)
    {
        var coach = existingCoach ?? await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var payload = new Dictionary<string, object?>
        {
            ["ProfessionalPublicId"] = coach.PublicId,
        };

        foreach (var property in body.GetType().GetProperties())
        {
            payload[property.Name] = property.GetValue(body);
        }

        (await client.Http.PostAsJsonAsync("/client/requests", payload, Ct)).StatusCode.Should().Be(HttpStatusCode.Created);

        return (client, coach);
    }

    private async Task SeedHistoryAsync(Actor client, int measurements, int photos, int nutritionPlans, int trainingPlans)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        for (var i = 0; i < measurements; i++)
        {
            db.BodyMeasurements.Add(new BodyMeasurement { ClientProfileId = client.ProfileId, MeasuredAt = DateTime.UtcNow.AddDays(-i) });
        }

        for (var i = 0; i < photos; i++)
        {
            db.PlanPhotos.Add(new PlanPhoto
            {
                ClientProfileId = client.ProfileId,
                UploadedByUserId = client.UserId,
                Category = PlanPhotoCategory.FreeForm,
                BlobUrl = $"plan-photos/{Guid.NewGuid()}.jpg",
                TakenAt = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync(Ct);

        for (var i = 0; i < nutritionPlans; i++)
        {
            // Plans from any coach count, so the author is a fresh id.
            await mongo.NutritionPlans.InsertOneAsync(
                new NutritionPlan { ExternalId = Guid.NewGuid(), ClientId = client.UserId, NutritionistId = Guid.NewGuid() },
                cancellationToken: Ct);
        }

        for (var i = 0; i < trainingPlans; i++)
        {
            await mongo.TrainingPlans.InsertOneAsync(
                new TrainingPlan { ExternalId = Guid.NewGuid(), ClientId = client.UserId, TrainerId = Guid.NewGuid() },
                cancellationToken: Ct);
        }
    }

    private static async Task<List<PendingItem>> ListPendingAsync(Actor coach)
    {
        var response = await coach.Http.GetAsync("/trainer/clients/pending", Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return (await response.Content.ReadFromJsonAsync<PendingResult>(cancellationToken: Ct))!.Rows;
    }

    [Fact]
    public async Task RequestWithPackage_ListsThePackageOnBothEndpoints()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await SubscribeAsync(factory, coach, canUsePayments: true);
        var packageId = await CreatePackageAsync(coach, 1200m, "Starter");
        await SendRequestAsync(new { PackagePublicId = packageId }, coach);

        var incoming = (await ListAsync(coach)).Should().ContainSingle().Subject;
        var pending = (await ListPendingAsync(coach)).Should().ContainSingle().Subject;

        foreach (var package in new[] { incoming.Package, pending.Package })
        {
            package.Should().NotBeNull();
            package!.PublicId.Should().Be(packageId);
            package.Name.Should().Be("Starter");
            package.Price.Should().Be(1200m);
            package.Billing.Should().Be("Monthly");
            package.Currency.Should().Be("CZK");
        }
    }

    [Fact]
    public async Task RequestWithoutPackage_HasNullPackage()
    {
        var (_, coach) = await RequestWithOnboardingAsync();

        (await ListAsync(coach)).Should().ContainSingle().Which.Package.Should().BeNull();
        (await ListPendingAsync(coach)).Should().ContainSingle().Which.Package.Should().BeNull();
    }

    [Fact]
    public async Task SharedHistory_ListsCountsOfAllTheClientsPlansMeasurementsAndPhotos()
    {
        var (client, coach) = await SendRequestAsync(new { ShareHistory = true });
        await SeedHistoryAsync(client, measurements: 3, photos: 2, nutritionPlans: 2, trainingPlans: 1);

        var incoming = (await ListAsync(coach)).Should().ContainSingle().Subject;
        var pending = (await ListPendingAsync(coach)).Should().ContainSingle().Subject;

        incoming.ShareHistory.Should().BeTrue();
        pending.ShareHistory.Should().BeTrue();
        incoming.History.Should().Be(new HistoryItem(PlanCount: 3, MeasurementCount: 3, PhotoCount: 2));
        pending.History.Should().Be(new HistoryItem(PlanCount: 3, MeasurementCount: 3, PhotoCount: 2));
    }

    [Fact]
    public async Task HistoryNotShared_HasNullCountsEvenWhenTheClientHoldsHistory()
    {
        var (client, coach) = await SendRequestAsync(new { ShareHistory = false });
        await SeedHistoryAsync(client, measurements: 1, photos: 1, nutritionPlans: 1, trainingPlans: 1);

        var incoming = (await ListAsync(coach)).Should().ContainSingle().Subject;
        var pending = (await ListPendingAsync(coach)).Should().ContainSingle().Subject;

        incoming.ShareHistory.Should().BeFalse();
        incoming.History.Should().BeNull();
        pending.History.Should().BeNull();
    }

    [Fact]
    public async Task SharedHistory_TwoClients_EachRowCarriesItsOwnCounts()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (first, _) = await SendRequestAsync(new { ShareHistory = true }, coach);
        var (second, _) = await SendRequestAsync(new { ShareHistory = true }, coach);
        await SeedHistoryAsync(first, measurements: 4, photos: 0, nutritionPlans: 0, trainingPlans: 0);
        await SeedHistoryAsync(second, measurements: 0, photos: 5, nutritionPlans: 0, trainingPlans: 2);

        var items = await ListAsync(coach);

        items.Single(i => i.ClientPublicId == first.PublicId).History.Should().Be(new HistoryItem(0, 4, 0));
        items.Single(i => i.ClientPublicId == second.PublicId).History.Should().Be(new HistoryItem(2, 0, 5));
    }
}
