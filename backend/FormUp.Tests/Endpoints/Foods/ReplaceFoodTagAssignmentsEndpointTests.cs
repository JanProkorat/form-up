using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Foods;

/// <summary>
/// Integration tests (#1120) for <c>PUT /trainer/foods/{FoodId}/tags</c>.
/// </summary>
[Collection(TestCollection.Shard4)]
public class ReplaceFoodTagAssignmentsEndpointTests(FitnessApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task Replace_OwnPrivateFood_Returns200AndAssignsTags()
    {
        var nutritionist = await TestActors.Nutritionist(factory).CreateAsync(TestContext.Current.CancellationToken);
        var tagId = await CreateTagAsync(nutritionist.Http, "Own Food Tag");
        var foodId = await InsertFoodAsync("Own Private Food", nutritionist.UserId, FoodVisibility.Private);

        var response = await nutritionist.Http.PutAsJsonAsync($"/trainer/foods/{foodId}/tags",
            new { TagIds = new[] { tagId } },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ReplaceResponseDto>(
            JsonOptions, TestContext.Current.CancellationToken);
        body!.Tags.Should().ContainSingle(t => t.TagId == tagId);
    }

    [Fact]
    public async Task Replace_SystemFood_Returns200()
    {
        // NutritionistId = null — a platform catalog entry, not owned by anyone.
        var nutritionist = await TestActors.Nutritionist(factory).CreateAsync(TestContext.Current.CancellationToken);
        var tagId = await CreateTagAsync(nutritionist.Http, "System Food Tag");
        var foodId = await InsertFoodAsync("System Catalog Food", nutritionistId: null, FoodVisibility.Public);

        var response = await nutritionist.Http.PutAsJsonAsync($"/trainer/foods/{foodId}/tags",
            new { TagIds = new[] { tagId } },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Replace_AnotherCoachsPublicFood_Returns200()
    {
        var owner = await TestActors.Nutritionist(factory).CreateAsync(TestContext.Current.CancellationToken);
        var foodId = await InsertFoodAsync("Shared Public Food", owner.UserId, FoodVisibility.Public);

        var tagger = await TestActors.Nutritionist(factory).CreateAsync(TestContext.Current.CancellationToken);
        var tagId = await CreateTagAsync(tagger.Http, "Tagger's Own Tag");

        var response = await tagger.Http.PutAsJsonAsync($"/trainer/foods/{foodId}/tags",
            new { TagIds = new[] { tagId } },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Replace_AnotherCoachsPrivateFood_Returns404()
    {
        var owner = await TestActors.Nutritionist(factory).CreateAsync(TestContext.Current.CancellationToken);
        var foodId = await InsertFoodAsync("Others Private Food", owner.UserId, FoodVisibility.Private);

        var tagger = await TestActors.Nutritionist(factory).CreateAsync(TestContext.Current.CancellationToken);
        var tagId = await CreateTagAsync(tagger.Http, "Tagger's Own Tag");

        var response = await tagger.Http.PutAsJsonAsync($"/trainer/foods/{foodId}/tags",
            new { TagIds = new[] { tagId } },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Replace_UnknownTagId_Returns404()
    {
        var nutritionist = await TestActors.Nutritionist(factory).CreateAsync(TestContext.Current.CancellationToken);
        var foodId = await InsertFoodAsync("Some Food", nutritionist.UserId, FoodVisibility.Private);

        var response = await nutritionist.Http.PutAsJsonAsync($"/trainer/foods/{foodId}/tags",
            new { TagIds = new[] { Guid.NewGuid() } },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Replace_UnknownFoodId_Returns404()
    {
        var nutritionist = await TestActors.Nutritionist(factory).CreateAsync(TestContext.Current.CancellationToken);
        var tagId = await CreateTagAsync(nutritionist.Http, "Orphan Tag");

        var response = await nutritionist.Http.PutAsJsonAsync($"/trainer/foods/{Guid.NewGuid()}/tags",
            new { TagIds = new[] { tagId } },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Replace_EmptyTagIds_ClearsAssignment()
    {
        var nutritionist = await TestActors.Nutritionist(factory).CreateAsync(TestContext.Current.CancellationToken);
        var tagId = await CreateTagAsync(nutritionist.Http, "Clearable");
        var foodId = await InsertFoodAsync("Food To Clear", nutritionist.UserId, FoodVisibility.Private);

        var assign = await nutritionist.Http.PutAsJsonAsync($"/trainer/foods/{foodId}/tags",
            new { TagIds = new[] { tagId } },
            TestContext.Current.CancellationToken);
        assign.StatusCode.Should().Be(HttpStatusCode.OK);

        var clear = await nutritionist.Http.PutAsJsonAsync($"/trainer/foods/{foodId}/tags",
            new { TagIds = Array.Empty<Guid>() },
            TestContext.Current.CancellationToken);

        clear.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await clear.Content.ReadFromJsonAsync<ReplaceResponseDto>(
            JsonOptions, TestContext.Current.CancellationToken);
        body!.Tags.Should().BeEmpty();
    }

    [Fact]
    public async Task Replace_DuplicateTagIds_Returns400()
    {
        var nutritionist = await TestActors.Nutritionist(factory).CreateAsync(TestContext.Current.CancellationToken);
        var tagId = await CreateTagAsync(nutritionist.Http, "Duplicated");
        var foodId = await InsertFoodAsync("Some Food", nutritionist.UserId, FoodVisibility.Private);

        var response = await nutritionist.Http.PutAsJsonAsync($"/trainer/foods/{foodId}/tags",
            new { TagIds = new[] { tagId, tagId } },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private async Task<Guid> InsertFoodAsync(string name, Guid? nutritionistId, FoodVisibility visibility)
    {
        var foodId = Guid.NewGuid();
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        await mongo.Foods.InsertOneAsync(new Food
        {
            ExternalId = foodId,
            Name = name,
            NutritionistId = nutritionistId,
            Visibility = visibility,
            IsDeleted = false,
            DateCreated = DateTime.UtcNow,
        }, cancellationToken: TestContext.Current.CancellationToken);
        return foodId;
    }

    private static async Task<Guid> CreateTagAsync(HttpClient http, string name)
    {
        var response = await http.PostAsJsonAsync("/trainer/food-tags",
            new { Name = name, Description = (string?)null, ColorHex = "#3b82f6" },
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<FoodTagDto>(
            JsonOptions, TestContext.Current.CancellationToken);
        return body!.TagId;
    }

    private sealed class FoodTagDto
    {
        public Guid TagId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string ColorHex { get; set; } = string.Empty;
    }

    private sealed class ReplaceResponseDto
    {
        public Guid FoodId { get; set; }
        public List<FoodTagDto> Tags { get; set; } = [];
    }
}
