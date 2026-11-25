using System.Net;
using System.Text.Json;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace FormUp.Tests.Endpoints.Foods.CopyFood;

/// <summary>
/// Real-pipeline tests for <c>POST /foods/{FoodId}/copy</c>. The Mongo mock ignores filters and
/// <c>CreatedAtAsync</c> needs the full host, so every test runs against the factory.
/// </summary>
[Collection(TestCollection.Name)]
public class CopyFoodEndpointTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private FakeBlobStorageService Blobs => (FakeBlobStorageService)factory.Services.GetRequiredService<IBlobStorageService>();

    private async Task<Food> InsertFoodAsync(
        Guid? ownerId,
        FoodVisibility visibility = FoodVisibility.Public,
        bool isDeleted = false,
        string? imageUrl = null,
        List<string>? gallery = null,
        Guid? sourceId = null)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        var food = new Food
        {
            ExternalId = sourceId ?? Guid.NewGuid(),
            Name = $"Copy Source {Guid.NewGuid():N}",
            LocalizedNames = new LocalizedNames { En = "Source EN", Cs = "Zdroj CS" },
            NutrientValue = new NutrientValue { Kcal = 120, Protein = 8, Carbs = 15, Fat = 3, Fiber = 2 },
            Category = FoodCategory.Dairy,
            Allergens = ["Milk"],
            DietaryPreferences = ["Vegetarian"],
            CommonServings = [new ServingSize { Label = "piece", WeightGrams = 40 }],
            Note = "Source note",
            NutritionistId = ownerId,
            Visibility = visibility,
            IsDeleted = isDeleted,
            ImageUrl = imageUrl,
            GalleryImageUrls = gallery ?? [],
            DateCreated = DateTime.UtcNow.AddDays(-3)
        };
        await mongo.Foods.InsertOneAsync(food, cancellationToken: Ct);

        return food;
    }

    private async Task<Food> LoadFoodAsync(Guid foodId)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        return await mongo.Foods.Find(f => f.ExternalId == foodId).FirstAsync(Ct);
    }

    private static async Task<Guid> CopiedIdAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: Ct);

        return body.GetProperty("foodId").GetGuid();
    }

    [Fact]
    public async Task CopyFood_PublicForeignFood_Returns201PrivateOwnedCopyAndLeavesSourceUntouched()
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var source = await InsertFoodAsync(Guid.NewGuid());

        var response = await coach.Http.PostAsJsonAsync($"/foods/{source.ExternalId}/copy", new { }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: Ct);
        body.GetProperty("visibility").GetString().Should().Be("Private");
        body.GetProperty("isOwnedByCurrentUser").GetBoolean().Should().BeTrue();

        var copy = await LoadFoodAsync(body.GetProperty("foodId").GetGuid());
        copy.ExternalId.Should().NotBe(source.ExternalId);
        copy.NutritionistId.Should().Be(coach.UserId);
        copy.Visibility.Should().Be(FoodVisibility.Private);
        copy.IsDeleted.Should().BeFalse();
        copy.DateUpdated.Should().BeNull();
        copy.Name.Should().Be(source.Name);
        copy.LocalizedNames!.Cs.Should().Be("Zdroj CS");
        copy.NutrientValue.Kcal.Should().Be(120);
        copy.Category.Should().Be(FoodCategory.Dairy);
        copy.Allergens.Should().Equal("Milk");
        copy.CommonServings.Should().ContainSingle().Which.WeightGrams.Should().Be(40);
        copy.Note.Should().Be("Source note");

        var untouched = await LoadFoodAsync(source.ExternalId);
        untouched.NutritionistId.Should().Be(source.NutritionistId);
        untouched.Visibility.Should().Be(FoodVisibility.Public);
        untouched.DateUpdated.Should().BeNull();
    }

    [Fact]
    public async Task CopyFood_OwnPrivateFood_Returns201()
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var source = await InsertFoodAsync(coach.UserId, FoodVisibility.Private);

        var response = await coach.Http.PostAsJsonAsync($"/foods/{source.ExternalId}/copy", new { }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await CopiedIdAsync(response)).Should().NotBe(source.ExternalId);
    }

    [Fact]
    public async Task CopyFood_PrivateForeignFood_Returns404()
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var source = await InsertFoodAsync(Guid.NewGuid(), FoodVisibility.Private);

        var response = await coach.Http.PostAsJsonAsync($"/foods/{source.ExternalId}/copy", new { }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CopyFood_UnknownId_Returns404()
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);

        var response = await coach.Http.PostAsJsonAsync($"/foods/{Guid.NewGuid()}/copy", new { }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CopyFood_DeletedFood_Returns404()
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var source = await InsertFoodAsync(coach.UserId, isDeleted: true);

        var response = await coach.Http.PostAsJsonAsync($"/foods/{source.ExternalId}/copy", new { }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CopyFood_PlanWithoutIngredients_Returns403SubscriptionRequired()
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.MoveToPlanAsync(factory, coach, plan => plan.CanUseIngredients = false);
        var source = await InsertFoodAsync(Guid.NewGuid());

        var response = await coach.Http.PostAsJsonAsync($"/foods/{source.ExternalId}/copy", new { }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.SubscriptionRequired);
    }

    [Fact]
    public async Task CopyFood_WithImages_DuplicatesBlobsUnderTheCopyAndKeepsSourceBlobs()
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var sourceId = Guid.NewGuid();
        var mainPath = $"foods/{sourceId}/main-{Guid.NewGuid():N}.jpg";
        var galleryPath = $"foods/{sourceId}/gallery-{Guid.NewGuid():N}.png";
        Blobs.SeedObject(mainPath, [1, 2, 3]);
        Blobs.SeedObject(galleryPath, [4, 5, 6]);
        var source = await InsertFoodAsync(Guid.NewGuid(), imageUrl: mainPath, gallery: [galleryPath], sourceId: sourceId);

        var response = await coach.Http.PostAsJsonAsync($"/foods/{source.ExternalId}/copy", new { }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var copy = await LoadFoodAsync(await CopiedIdAsync(response));
        copy.ImageUrl.Should().MatchRegex($"^foods/{copy.ExternalId}/main-[0-9a-f]{{32}}\\.jpg$");
        copy.ImageUrl.Should().NotBe(source.ImageUrl);
        copy.GalleryImageUrls.Should().ContainSingle().Which.Should().MatchRegex($"^foods/{copy.ExternalId}/gallery-[0-9a-f]{{32}}\\.png$");
        Blobs.UploadedPaths.Should().Contain(copy.ImageUrl!);
        Blobs.UploadedPaths.Should().Contain(copy.GalleryImageUrls[0]);
        (await Blobs.ObjectExistsAsync(mainPath, Ct)).Should().BeTrue("the source blob must stay in place");
        (await Blobs.ObjectExistsAsync(galleryPath, Ct)).Should().BeTrue();
        Blobs.DeletedPaths.Should().NotContain(mainPath);

        var untouched = await LoadFoodAsync(source.ExternalId);
        untouched.ImageUrl.Should().Be(mainPath);
        untouched.GalleryImageUrls.Should().Equal(galleryPath);
    }

    [Fact]
    public async Task CopyFood_ImageBlobMissing_SucceedsWithoutImage()
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var sourceId = Guid.NewGuid();
        var source = await InsertFoodAsync(
            Guid.NewGuid(),
            imageUrl: $"foods/{sourceId}/main-{Guid.NewGuid():N}.jpg",
            gallery: [$"foods/{sourceId}/gallery-{Guid.NewGuid():N}.jpg"],
            sourceId: sourceId);

        var response = await coach.Http.PostAsJsonAsync($"/foods/{source.ExternalId}/copy", new { }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var copy = await LoadFoodAsync(await CopiedIdAsync(response));
        copy.ImageUrl.Should().BeNull();
        copy.GalleryImageUrls.Should().BeEmpty();
    }

    [Fact]
    public async Task CopyFood_ImageUrlPointsAtAnotherFoodsBlob_SkipsItAndNeverSharesThePath()
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var foreignPath = $"foods/{Guid.NewGuid()}/main-{Guid.NewGuid():N}.jpg";
        Blobs.SeedObject(foreignPath, [9, 9, 9]);
        var source = await InsertFoodAsync(Guid.NewGuid(), imageUrl: foreignPath);

        var response = await coach.Http.PostAsJsonAsync($"/foods/{source.ExternalId}/copy", new { }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var copy = await LoadFoodAsync(await CopiedIdAsync(response));
        copy.ImageUrl.Should().BeNull();
        copy.ImageUrl.Should().NotBe(foreignPath);
    }
}
