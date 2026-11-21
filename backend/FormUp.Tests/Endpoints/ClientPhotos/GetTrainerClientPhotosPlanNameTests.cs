using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;

namespace FormUp.Tests.Endpoints.ClientPhotos;

/// <summary>
/// Integration tests (real MongoDB) for the plan name resolution on
/// GET /trainer/clients/{clientId}/photos. Client scoping and batching live in the Mongo filter,
/// which the mock Mongo ignores.
/// </summary>
[Collection(TestCollection.Shard4)]
public class GetTrainerClientPhotosPlanNameTests(FitnessApiFactory factory)
{
    private sealed record PhotoBody(Guid Id, Guid? PlanId, string? PlanName);

    private sealed record MonthGroupBody(List<PhotoBody> Photos);

    private sealed record PhotosBody(List<PhotoBody>? Photos, List<MonthGroupBody>? Groups);

    private async Task<Guid> SeedNutritionPlanAsync(
        Guid clientUserId, string name, NutritionPlanStatus status = NutritionPlanStatus.Active)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        var planId = Guid.NewGuid();
        await mongo.NutritionPlans.InsertOneAsync(new NutritionPlan
        {
            Id = ObjectId.GenerateNewId(),
            ExternalId = planId,
            ClientId = clientUserId,
            NutritionistId = Guid.NewGuid(),
            Name = name,
            Status = status,
            StartDate = DateTime.UtcNow.Date,
            Version = 1,
            DateCreated = DateTime.UtcNow,
        }, cancellationToken: TestContext.Current.CancellationToken);
        return planId;
    }

    private async Task<Guid> SeedTrainingPlanAsync(Guid clientUserId, string name)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        var planId = Guid.NewGuid();
        await mongo.TrainingPlans.InsertOneAsync(new TrainingPlan
        {
            Id = ObjectId.GenerateNewId(),
            ExternalId = planId,
            ClientId = clientUserId,
            TrainerId = Guid.NewGuid(),
            Name = name,
            Status = TrainingPlanStatus.Active,
            Version = 1,
            DateCreated = DateTime.UtcNow,
        }, cancellationToken: TestContext.Current.CancellationToken);
        return planId;
    }

    private async Task SeedPhotoAsync(
        Actor client,
        Actor viewer,
        PlanPhotoCategory category,
        Guid? planId,
        PlanPhotoType? planType)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var photo = new PlanPhoto
        {
            ClientProfileId = client.ProfileId,
            Category = category,
            PlanId = planId,
            PlanType = planType,
            BlobUrl = "https://example.invalid/photo.jpg",
            TakenAt = DateTime.UtcNow,
            UploadedByUserId = client.UserId,
        };
        db.PlanPhotos.Add(photo);
        db.PlanPhotoViewers.Add(EntityBuilder.PlanPhotoViewer
            .ForPhoto(photo)
            .WithProfessionalUserId(viewer.UserId)
            .Build());
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<List<PhotoBody>> GetPhotosAsync(Actor coach, Actor client, bool groupByMonth = false)
    {
        var response = await coach.Http.GetAsync(
            $"/trainer/clients/{client.PublicId}/photos?pageSize=100&groupByMonth={groupByMonth.ToString().ToLowerInvariant()}",
            TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PhotosBody>(
            cancellationToken: TestContext.Current.CancellationToken);
        return groupByMonth ? body!.Groups!.SelectMany(group => group.Photos).ToList() : body!.Photos!;
    }

    private async Task<(Actor Coach, Actor Client)> LinkedPairAsync(bool nutrition = true, bool training = true)
    {
        var ct = TestContext.Current.CancellationToken;
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(ct);
        var client = await TestActors.Client(factory).CreateAsync(ct);
        await TestActors.Link(factory, coach, client)
            .CanViewNutritionPlans(nutrition)
            .CanViewTrainingPlans(training)
            .CreateAsync(ct);
        return (coach, client);
    }

    [Fact]
    public async Task GetPhotos_LinkedNutritionPlan_ReturnsPlanName()
    {
        var (coach, client) = await LinkedPairAsync();
        var planId = await SeedNutritionPlanAsync(client.UserId, "Cut plan");
        await SeedPhotoAsync(client, coach, PlanPhotoCategory.Food, planId, PlanPhotoType.Nutrition);

        var photos = await GetPhotosAsync(coach, client);

        photos.Single().PlanName.Should().Be("Cut plan");
    }

    [Fact]
    public async Task GetPhotos_LinkedTrainingPlan_ReturnsPlanName()
    {
        var (coach, client) = await LinkedPairAsync();
        var planId = await SeedTrainingPlanAsync(client.UserId, "Strength block");
        await SeedPhotoAsync(client, coach, PlanPhotoCategory.Training, planId, PlanPhotoType.Training);

        var photos = await GetPhotosAsync(coach, client);

        photos.Single().PlanName.Should().Be("Strength block");
    }

    [Fact]
    public async Task GetPhotos_PhotoWithoutPlan_ReturnsNullPlanName()
    {
        var (coach, client) = await LinkedPairAsync();
        await SeedPhotoAsync(client, coach, PlanPhotoCategory.Body, null, null);

        var photos = await GetPhotosAsync(coach, client);

        photos.Single().PlanName.Should().BeNull();
    }

    [Fact]
    public async Task GetPhotos_PlanDocumentMissing_ReturnsNullPlanName()
    {
        var (coach, client) = await LinkedPairAsync();
        await SeedPhotoAsync(client, coach, PlanPhotoCategory.Food, Guid.NewGuid(), PlanPhotoType.Nutrition);

        var photos = await GetPhotosAsync(coach, client);

        photos.Single().PlanName.Should().BeNull();
    }

    [Fact]
    public async Task GetPhotos_PlanOfAnotherClient_ReturnsNullPlanName()
    {
        var (coach, client) = await LinkedPairAsync();
        var otherClient = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        var otherPlanId = await SeedNutritionPlanAsync(otherClient.UserId, "Someone else's plan");
        await SeedPhotoAsync(client, coach, PlanPhotoCategory.Food, otherPlanId, PlanPhotoType.Nutrition);

        var photos = await GetPhotosAsync(coach, client);

        photos.Single().PlanName.Should().BeNull();
    }

    [Theory]
    [InlineData(PlanPhotoType.Nutrition)]
    [InlineData(PlanPhotoType.Training)]
    public async Task GetPhotos_BodyPhotoWithoutCapabilityForPlanType_ReturnsNullPlanName(PlanPhotoType planType)
    {
        var (coach, client) = await LinkedPairAsync(
            nutrition: planType != PlanPhotoType.Nutrition,
            training: planType != PlanPhotoType.Training);
        var planId = planType == PlanPhotoType.Nutrition
            ? await SeedNutritionPlanAsync(client.UserId, "Hidden nutrition plan")
            : await SeedTrainingPlanAsync(client.UserId, "Hidden training plan");
        await SeedPhotoAsync(client, coach, PlanPhotoCategory.Body, planId, planType);

        var photos = await GetPhotosAsync(coach, client);

        photos.Single().PlanName.Should().BeNull();
    }

    [Fact]
    public async Task GetPhotos_BodyPhotoWithPlanAndCapability_ReturnsPlanName()
    {
        var (coach, client) = await LinkedPairAsync();
        var planId = await SeedNutritionPlanAsync(client.UserId, "Body day plan");
        await SeedPhotoAsync(client, coach, PlanPhotoCategory.Body, planId, PlanPhotoType.Nutrition);

        var photos = await GetPhotosAsync(coach, client);

        photos.Single().PlanName.Should().Be("Body day plan");
    }

    [Fact]
    public async Task GetPhotos_ArchivedPlan_KeepsPlanName()
    {
        var (coach, client) = await LinkedPairAsync();
        var planId = await SeedNutritionPlanAsync(client.UserId, "Old plan", NutritionPlanStatus.Archived);
        await SeedPhotoAsync(client, coach, PlanPhotoCategory.Food, planId, PlanPhotoType.Nutrition);

        var photos = await GetPhotosAsync(coach, client);

        photos.Single().PlanName.Should().Be("Old plan");
    }

    [Fact]
    public async Task GetPhotos_GroupedByMonth_CarriesPlanName()
    {
        var (coach, client) = await LinkedPairAsync();
        var nutritionPlanId = await SeedNutritionPlanAsync(client.UserId, "Grouped nutrition");
        var trainingPlanId = await SeedTrainingPlanAsync(client.UserId, "Grouped training");
        await SeedPhotoAsync(client, coach, PlanPhotoCategory.Food, nutritionPlanId, PlanPhotoType.Nutrition);
        await SeedPhotoAsync(client, coach, PlanPhotoCategory.Training, trainingPlanId, PlanPhotoType.Training);

        var photos = await GetPhotosAsync(coach, client, groupByMonth: true);

        photos.Single(photo => photo.PlanId == nutritionPlanId).PlanName.Should().Be("Grouped nutrition");
        photos.Single(photo => photo.PlanId == trainingPlanId).PlanName.Should().Be("Grouped training");
    }
}
