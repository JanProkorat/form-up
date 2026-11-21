using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FormUp.Tests.Endpoints.ClientPhotos;

/// <summary>
/// Integration tests (real PostgreSQL) for who sees a plan photo in the trainer photo list. Rows
/// are written by <see cref="PlanPhotoVisibilityWriter"/> at upload, never recomputed afterwards.
/// </summary>
[Collection(TestCollection.Shard2)]
public class GetTrainerClientPhotosVisibilityIntegrationTests(FitnessApiFactory factory)
{
    private sealed record PhotoBody(Guid Id, string Category);

    private sealed record PhotosBody(List<PhotoBody>? Photos);

    private async Task<Guid> UploadAsync(Actor client, PlanPhotoCategory category, Guid? creator, Guid? planId = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var photo = new PlanPhoto
        {
            PublicId = Guid.NewGuid(),
            ClientProfileId = client.ProfileId,
            Category = category,
            PlanId = planId,
            BlobUrl = $"https://example.invalid/{Guid.NewGuid():N}.jpg",
            TakenAt = DateTime.UtcNow,
            UploadedByUserId = client.UserId,
        };
        db.PlanPhotos.Add(photo);
        await PlanPhotoVisibilityWriter.AddViewersAsync(
            db, NullLogger.Instance, [photo], creator, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return photo.PublicId;
    }

    private async Task<List<Guid>> ListPhotoIdsAsync(Actor coach, Actor client)
    {
        var response = await coach.Http.GetAsync(
            $"/trainer/clients/{client.PublicId}/photos?pageSize=100", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PhotosBody>(
            cancellationToken: TestContext.Current.CancellationToken);
        return body!.Photos!.Select(photo => photo.Id).ToList();
    }

    [Fact]
    public async Task GetPhotos_FoodPhotoOfAnotherCoachsPlan_IsNotReturned()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await TestActors.Client(factory).CreateAsync(ct);
        var coachA = await TestActors.Nutritionist(factory).CreateAsync(ct);
        var coachB = await TestActors.Nutritionist(factory).CreateAsync(ct);
        await TestActors.Link(factory, coachA, client).CanViewTrainingPlans(false).CreateAsync(ct);
        await TestActors.Link(factory, coachB, client).CanViewTrainingPlans(false).CreateAsync(ct);
        var photoId = await UploadAsync(client, PlanPhotoCategory.Food, coachB.UserId, Guid.NewGuid());

        (await ListPhotoIdsAsync(coachA, client)).Should().NotContain(photoId);
        (await ListPhotoIdsAsync(coachB, client)).Should().Contain(photoId);
    }

    [Theory]
    [InlineData(PlanPhotoCategory.Body)]
    [InlineData(PlanPhotoCategory.FreeForm)]
    public async Task GetPhotos_UnlinkedPhotoUploadedBeforeCoachConnected_IsNotReturned(PlanPhotoCategory category)
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await TestActors.Client(factory).CreateAsync(ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(ct);
        var earlyPhotoId = await UploadAsync(client, category, creator: null);

        await TestActors.Link(factory, coach, client).CreateAsync(ct);
        var latePhotoId = await UploadAsync(client, category, creator: null);

        var visible = await ListPhotoIdsAsync(coach, client);
        visible.Should().NotContain(earlyPhotoId);
        visible.Should().Contain(latePhotoId);
    }

    [Fact]
    public async Task GetPhotos_FoodAndTrainingPhotosWithViewerRow_StillHiddenWithoutCapability()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await TestActors.Client(factory).CreateAsync(ct);
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(ct);
        await TestActors.Link(factory, coach, client).CanViewNutritionPlans(false).CreateAsync(ct);
        var foodId = await UploadAsync(client, PlanPhotoCategory.Food, coach.UserId, Guid.NewGuid());
        var trainingId = await UploadAsync(client, PlanPhotoCategory.Training, coach.UserId, Guid.NewGuid());

        var visible = await ListPhotoIdsAsync(coach, client);

        visible.Should().Contain(trainingId);
        visible.Should().NotContain(foodId);
    }
}
