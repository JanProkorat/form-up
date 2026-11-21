using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Trainers;

/// <summary>
/// Integration tests (real PostgreSQL) for the Body photo count on the trainer's client dashboard:
/// it counts only photos the calling coach holds a viewer row for.
/// </summary>
[Collection(TestCollection.Shard2)]
public class GetClientDashboardPhotoVisibilityIntegrationTests(FitnessApiFactory factory)
{
    private sealed record DashboardBody(int TotalProgressPhotos);

    private async Task SeedBodyPhotoAsync(Actor client, params Actor[] viewers)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var photo = new PlanPhoto
        {
            PublicId = Guid.NewGuid(),
            ClientProfileId = client.ProfileId,
            Category = PlanPhotoCategory.Body,
            BlobUrl = $"https://example.invalid/{Guid.NewGuid():N}.jpg",
            TakenAt = DateTime.UtcNow,
            UploadedByUserId = client.UserId,
        };
        db.PlanPhotos.Add(photo);
        db.PlanPhotoViewers.AddRange(viewers.Select(viewer => EntityBuilder.PlanPhotoViewer
            .ForPhoto(photo)
            .WithProfessionalUserId(viewer.UserId)
            .Build()));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<int> GetCountAsync(Actor coach, Actor client)
    {
        var response = await coach.Http.GetAsync(
            $"/trainer/clients/{client.PublicId}", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<DashboardBody>(
            cancellationToken: TestContext.Current.CancellationToken);
        return body!.TotalProgressPhotos;
    }

    [Fact]
    public async Task GetClientDashboard_BodyPhotoOnlyOtherCoachCanSee_IsExcludedFromCount()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await TestActors.Client(factory).CreateAsync(ct);
        var coachA = await TestActors.Trainer(factory).CreateAsync(ct);
        var coachB = await TestActors.Trainer(factory).CreateAsync(ct);
        await TestActors.Link(factory, coachA, client).CreateAsync(ct);
        await TestActors.Link(factory, coachB, client).CreateAsync(ct);
        await SeedBodyPhotoAsync(client, coachB);
        await SeedBodyPhotoAsync(client, coachA, coachB);

        (await GetCountAsync(coachA, client)).Should().Be(1);
        (await GetCountAsync(coachB, client)).Should().Be(2);
    }
}
