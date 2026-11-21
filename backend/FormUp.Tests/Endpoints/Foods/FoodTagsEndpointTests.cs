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
using MongoDB.Driver;

namespace FormUp.Tests.Endpoints.Foods;

/// <summary>
/// Integration tests (#1120) for the coach-private food-tag CRUD slice:
/// <c>POST /trainer/food-tags</c>, <c>GET /trainer/food-tags</c>,
/// <c>PUT /trainer/food-tags/{TagId}</c>, and <c>DELETE /trainer/food-tags/{TagId}</c>.
/// </summary>
[Collection(TestCollection.Shard4)]
public class FoodTagsEndpointTests(FitnessApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task Create_ValidRequest_Returns201AndPersistsTag()
    {
        var nutritionist = await TestActors.Nutritionist(factory).CreateAsync(TestContext.Current.CancellationToken);

        var response = await nutritionist.Http.PostAsJsonAsync("/trainer/food-tags",
            new { Name = "High Protein", Description = "Protein-forward foods", ColorHex = "#3B82F6" },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<FoodTagDto>(
            JsonOptions, TestContext.Current.CancellationToken);
        body!.Name.Should().Be("High Protein");
        body.ColorHex.Should().Be("#3b82f6", "ColorHex is normalised to lowercase on write");

        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        var persisted = await mongo.FoodTags
            .Find(t => t.ExternalId == body.TagId)
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        persisted.Should().NotBeNull();
        persisted!.OwnerUserId.Should().Be(nutritionist.UserId);
        persisted.Description.Should().Be("Protein-forward foods");
    }

    [Fact]
    public async Task Create_DuplicateNameCaseVariant_Returns409()
    {
        var nutritionist = await TestActors.Nutritionist(factory).CreateAsync(TestContext.Current.CancellationToken);

        var first = await nutritionist.Http.PostAsJsonAsync("/trainer/food-tags",
            new { Name = "Meal Prep", Description = (string?)null, ColorHex = "#3b82f6" },
            TestContext.Current.CancellationToken);
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await nutritionist.Http.PostAsJsonAsync("/trainer/food-tags",
            new { Name = "MEAL PREP", Description = (string?)null, ColorHex = "#22c55e" },
            TestContext.Current.CancellationToken);

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Create_EmptyName_Returns400()
    {
        var nutritionist = await TestActors.Nutritionist(factory).CreateAsync(TestContext.Current.CancellationToken);

        var response = await nutritionist.Http.PostAsJsonAsync("/trainer/food-tags",
            new { Name = "", Description = (string?)null, ColorHex = "#3b82f6" },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Get_SecondNutritionist_SeesOnlyOwnTags()
    {
        var first = await TestActors.Nutritionist(factory).CreateAsync(TestContext.Current.CancellationToken);
        await CreateTagAsync(first.Http, "First Coach's Tag", "#3b82f6");

        var second = await TestActors.Nutritionist(factory).CreateAsync(TestContext.Current.CancellationToken);
        await CreateTagAsync(second.Http, "Second Coach's Tag", "#22c55e");

        var response = await second.Http.GetAsync("/trainer/food-tags", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<FoodTagsListDto>(
            JsonOptions, TestContext.Current.CancellationToken);
        body!.Tags.Should().ContainSingle(t => t.Name == "Second Coach's Tag");
        body.Tags.Should().NotContain(t => t.Name == "First Coach's Tag");
    }

    [Fact]
    public async Task Update_Rename_Returns200AndUpdatesFields()
    {
        var nutritionist = await TestActors.Nutritionist(factory).CreateAsync(TestContext.Current.CancellationToken);
        var tagId = await CreateTagAsync(nutritionist.Http, "Old Name", "#3b82f6");

        var response = await nutritionist.Http.PutAsJsonAsync($"/trainer/food-tags/{tagId}",
            new { Name = "New Name", Description = "Updated", ColorHex = "#EF4444" },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<FoodTagDto>(
            JsonOptions, TestContext.Current.CancellationToken);
        body!.Name.Should().Be("New Name");
        body.Description.Should().Be("Updated");
        body.ColorHex.Should().Be("#ef4444");
    }

    [Fact]
    public async Task Update_RenameToExistingName_Returns409()
    {
        var nutritionist = await TestActors.Nutritionist(factory).CreateAsync(TestContext.Current.CancellationToken);
        await CreateTagAsync(nutritionist.Http, "Taken", "#3b82f6");
        var otherTagId = await CreateTagAsync(nutritionist.Http, "Available", "#22c55e");

        var response = await nutritionist.Http.PutAsJsonAsync($"/trainer/food-tags/{otherTagId}",
            new { Name = "Taken", Description = (string?)null, ColorHex = "#22c55e" },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Update_OtherCoachsTag_Returns404()
    {
        var owner = await TestActors.Nutritionist(factory).CreateAsync(TestContext.Current.CancellationToken);
        var tagId = await CreateTagAsync(owner.Http, "Owned", "#3b82f6");

        var other = await TestActors.Nutritionist(factory).CreateAsync(TestContext.Current.CancellationToken);

        var response = await other.Http.PutAsJsonAsync($"/trainer/food-tags/{tagId}",
            new { Name = "Hijacked", Description = (string?)null, ColorHex = "#ef4444" },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Update_InvalidColorHex_Returns400()
    {
        var nutritionist = await TestActors.Nutritionist(factory).CreateAsync(TestContext.Current.CancellationToken);
        var tagId = await CreateTagAsync(nutritionist.Http, "Tag", "#3b82f6");

        var response = await nutritionist.Http.PutAsJsonAsync($"/trainer/food-tags/{tagId}",
            new { Name = "Tag", Description = (string?)null, ColorHex = "not-a-color" },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Delete_ValidRequest_Returns204AndRemovesTag()
    {
        var nutritionist = await TestActors.Nutritionist(factory).CreateAsync(TestContext.Current.CancellationToken);
        var tagId = await CreateTagAsync(nutritionist.Http, "Doomed", "#3b82f6");

        var response = await nutritionist.Http.DeleteAsync(
            $"/trainer/food-tags/{tagId}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        var persisted = await mongo.FoodTags
            .Find(t => t.ExternalId == tagId)
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        persisted.Should().BeNull();
    }

    [Fact]
    public async Task Delete_OtherCoachsTag_Returns404()
    {
        var owner = await TestActors.Nutritionist(factory).CreateAsync(TestContext.Current.CancellationToken);
        var tagId = await CreateTagAsync(owner.Http, "Owned", "#3b82f6");

        var other = await TestActors.Nutritionist(factory).CreateAsync(TestContext.Current.CancellationToken);

        var response = await other.Http.DeleteAsync(
            $"/trainer/food-tags/{tagId}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        var stillThere = await mongo.FoodTags
            .Find(t => t.ExternalId == tagId)
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        stillThere.Should().NotBeNull("a non-owning coach's delete must never affect the tag");
    }

    [Fact]
    public async Task Delete_AssignedTag_RemovesItFromTheAssignmentAndFromFoodChips()
    {
        var nutritionist = await TestActors.Nutritionist(factory).CreateAsync(TestContext.Current.CancellationToken);
        var tagId = await CreateTagAsync(nutritionist.Http, "Removable", "#3b82f6");

        var foodId = Guid.NewGuid();
        using (var setupScope = factory.Services.CreateScope())
        {
            var mongo = setupScope.ServiceProvider.GetRequiredService<IMongoContext>();
            await mongo.Foods.InsertOneAsync(new Food
            {
                ExternalId = foodId,
                Name = "Tagged Food",
                NutritionistId = nutritionist.UserId,
                Visibility = FoodVisibility.Private,
                IsDeleted = false,
                DateCreated = DateTime.UtcNow,
            }, cancellationToken: TestContext.Current.CancellationToken);
        }

        var assignResponse = await nutritionist.Http.PutAsJsonAsync($"/trainer/foods/{foodId}/tags",
            new { TagIds = new[] { tagId } },
            TestContext.Current.CancellationToken);
        assignResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var deleteResponse = await nutritionist.Http.DeleteAsync(
            $"/trainer/food-tags/{tagId}", TestContext.Current.CancellationToken);
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var getResponse = await nutritionist.Http.GetAsync($"/foods/{foodId}", TestContext.Current.CancellationToken);
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var foodBody = await getResponse.Content.ReadFromJsonAsync<FoodSummaryTagsDto>(
            JsonOptions, TestContext.Current.CancellationToken);
        foodBody!.Tags.Should().BeEmpty("the deleted tag must be pulled out of the food's assignment");
    }

    private static async Task<Guid> CreateTagAsync(HttpClient http, string name, string colorHex)
    {
        var response = await http.PostAsJsonAsync("/trainer/food-tags",
            new { Name = name, Description = (string?)null, ColorHex = colorHex },
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

    private sealed class FoodTagsListDto
    {
        public List<FoodTagDto> Tags { get; set; } = [];
    }

    private sealed class FoodSummaryTagsDto
    {
        public List<FoodTagDto> Tags { get; set; } = [];
    }
}
