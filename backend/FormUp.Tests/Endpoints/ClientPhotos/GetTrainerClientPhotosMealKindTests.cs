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
/// Integration tests (real MongoDB) for the meal id / meal kind resolution on
/// GET /trainer/clients/{clientId}/photos. Filter behaviour (client scoping) cannot be proven
/// against the mock Mongo, which ignores filters.
/// </summary>
[Collection(TestCollection.Shard2)]
public class GetTrainerClientPhotosMealKindTests(FitnessApiFactory factory)
{
    private sealed record PhotoBody(string Category, string? MealLogId, Guid? MealId, string? MealKind);

    private sealed record MonthGroupBody(List<PhotoBody> Photos);

    private sealed record PhotosBody(List<PhotoBody>? Photos, List<MonthGroupBody>? Groups);

    private async Task<(Guid PlanId, Guid MealId)> SeedPlanAsync(Guid clientUserId, Guid coachUserId, MealKind kind)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        var planId = Guid.NewGuid();
        var mealId = Guid.NewGuid();

        await mongo.NutritionPlans.InsertOneAsync(new NutritionPlan
        {
            Id = ObjectId.GenerateNewId(),
            ExternalId = planId,
            ClientId = clientUserId,
            NutritionistId = coachUserId,
            Name = "Meal kind plan",
            Status = NutritionPlanStatus.Active,
            StartDate = DateTime.UtcNow.Date,
            Weeks =
            [
                new PlanWeek
                {
                    WeekNumber = 1,
                    Status = WeekStatus.Published,
                    Days = [new PlanDay { DayOfWeek = 1, Meals = [new PlanMeal { MealId = mealId, Kind = kind }] }],
                },
            ],
            Version = 1,
            DateCreated = DateTime.UtcNow,
        }, cancellationToken: TestContext.Current.CancellationToken);

        return (planId, mealId);
    }

    private async Task<string> SeedMealLogAsync(Guid clientUserId, Guid planId, Guid mealId)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        var log = new MealLog
        {
            Id = ObjectId.GenerateNewId(),
            ClientId = clientUserId,
            PlanId = planId,
            MealId = mealId,
            LogDate = DateTime.UtcNow.Date,
        };
        await mongo.MealLogs.InsertOneAsync(log, cancellationToken: TestContext.Current.CancellationToken);
        return log.Id.ToString();
    }

    private async Task SeedPhotoAsync(
        Actor client, Actor viewer, PlanPhotoCategory category, string? mealLogId, DateTime takenAt)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var photo = new PlanPhoto
        {
            ClientProfileId = client.ProfileId,
            Category = category,
            MealLogId = mealLogId,
            BlobUrl = "https://example.invalid/photo.jpg",
            TakenAt = takenAt,
            UploadedByUserId = client.UserId,
        };
        db.PlanPhotos.Add(photo);
        db.PlanPhotoViewers.Add(EntityBuilder.PlanPhotoViewer
            .ForPhoto(photo)
            .WithProfessionalUserId(viewer.UserId)
            .Build());
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<List<PhotoBody>> GetPhotosAsync(Actor coach, Actor client, bool groupByMonth)
    {
        var response = await coach.Http.GetAsync(
            $"/trainer/clients/{client.PublicId}/photos?groupByMonth={groupByMonth.ToString().ToLowerInvariant()}",
            TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PhotosBody>(
            cancellationToken: TestContext.Current.CancellationToken);
        return groupByMonth ? body!.Groups!.SelectMany(group => group.Photos).ToList() : body!.Photos!;
    }

    private async Task<(Actor Coach, Actor Client)> LinkedPairAsync()
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(TestContext.Current.CancellationToken);
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        await TestActors.Link(factory, coach, client).CreateAsync(TestContext.Current.CancellationToken);
        return (coach, client);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetPhotos_MealPhoto_ReturnsMealIdAndKind_AndNullForBodyPhoto(bool groupByMonth)
    {
        var (coach, client) = await LinkedPairAsync();
        var (planId, mealId) = await SeedPlanAsync(client.UserId, coach.UserId, MealKind.Dinner);
        var logId = await SeedMealLogAsync(client.UserId, planId, mealId);
        await SeedPhotoAsync(client, coach, PlanPhotoCategory.Food, logId, DateTime.UtcNow.AddMinutes(-1));
        await SeedPhotoAsync(client, coach, PlanPhotoCategory.Body, null, DateTime.UtcNow.AddMinutes(-2));

        var photos = await GetPhotosAsync(coach, client, groupByMonth);

        var food = photos.Single(photo => photo.Category == "Food");
        food.MealId.Should().Be(mealId);
        food.MealKind.Should().Be("Dinner");
        var body = photos.Single(photo => photo.Category == "Body");
        body.MealId.Should().BeNull();
        body.MealKind.Should().BeNull();
    }

    [Fact]
    public async Task GetPhotos_MealLogOfAnotherClient_ReturnsNulls()
    {
        var (coach, client) = await LinkedPairAsync();
        var otherClient = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        var (otherPlanId, otherMealId) = await SeedPlanAsync(otherClient.UserId, coach.UserId, MealKind.Lunch);
        var otherLogId = await SeedMealLogAsync(otherClient.UserId, otherPlanId, otherMealId);
        await SeedPhotoAsync(client, coach, PlanPhotoCategory.Food, otherLogId, DateTime.UtcNow);

        var photos = await GetPhotosAsync(coach, client, groupByMonth: false);

        var food = photos.Single();
        food.MealId.Should().BeNull();
        food.MealKind.Should().BeNull();
    }

    [Fact]
    public async Task GetPhotos_PlanOfAnotherClient_SetsMealIdButNoKind()
    {
        var (coach, client) = await LinkedPairAsync();
        var otherClient = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        var (otherPlanId, otherMealId) = await SeedPlanAsync(otherClient.UserId, coach.UserId, MealKind.Lunch);
        var logId = await SeedMealLogAsync(client.UserId, otherPlanId, otherMealId);
        await SeedPhotoAsync(client, coach, PlanPhotoCategory.Food, logId, DateTime.UtcNow);

        var photos = await GetPhotosAsync(coach, client, groupByMonth: false);

        var food = photos.Single();
        food.MealId.Should().Be(otherMealId);
        food.MealKind.Should().BeNull();
    }

    [Fact]
    public async Task GetPhotos_DeletedPlanOrRemovedMeal_SetsMealIdButNoKind()
    {
        var (coach, client) = await LinkedPairAsync();
        var (planId, _) = await SeedPlanAsync(client.UserId, coach.UserId, MealKind.Lunch);
        var removedMealId = Guid.NewGuid();
        var logId = await SeedMealLogAsync(client.UserId, planId, removedMealId);
        await SeedPhotoAsync(client, coach, PlanPhotoCategory.Food, logId, DateTime.UtcNow);

        var photos = await GetPhotosAsync(coach, client, groupByMonth: false);

        var food = photos.Single();
        food.MealId.Should().Be(removedMealId);
        food.MealKind.Should().BeNull();
    }

    [Theory]
    [InlineData("not-an-object-id")]
    [InlineData("0123456789abcdef01234567")]
    public async Task GetPhotos_UnparseableOrDanglingMealLogId_Returns200WithNulls(string mealLogId)
    {
        var (coach, client) = await LinkedPairAsync();
        await SeedPhotoAsync(client, coach, PlanPhotoCategory.Food, mealLogId, DateTime.UtcNow);

        var photos = await GetPhotosAsync(coach, client, groupByMonth: false);

        var food = photos.Single();
        food.MealId.Should().BeNull();
        food.MealKind.Should().BeNull();
    }
}
