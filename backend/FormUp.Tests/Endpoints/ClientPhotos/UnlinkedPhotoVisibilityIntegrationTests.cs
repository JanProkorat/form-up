using System.Net;
using System.Net.Http.Json;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;

namespace FormUp.Tests.Endpoints.ClientPhotos;

/// <summary>
/// End-to-end tests (upload-url, finalize, list) for who sees a plan-less progress photo.
/// </summary>
[Collection(TestCollection.Shard3)]
public class UnlinkedPhotoVisibilityIntegrationTests(FitnessApiFactory factory)
{
    private sealed record UploadUrlBody(string UploadUrl, string BlobUrl);

    private sealed record PhotoBody(Guid Id, Guid? PlanId);

    private sealed record GroupBody(string YearMonth, List<PhotoBody> Photos);

    private sealed record MyPhotosBody(List<GroupBody>? Groups);

    private sealed record TrainerPhotosBody(List<PhotoBody>? Photos);

    private async Task<Guid> UploadUnlinkedPhotoAsync(Actor client, string category)
    {
        var ct = TestContext.Current.CancellationToken;
        var urlResponse = await client.Http.PostAsJsonAsync(
            "/client/photos/upload-url", new { contentType = "image/jpeg", sizeBytes = 1024 }, ct);
        urlResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var urlBody = await urlResponse.Content.ReadFromJsonAsync<UploadUrlBody>(cancellationToken: ct);

        var finalizeResponse = await client.Http.PostAsJsonAsync(
            "/client/photos", new { blobUrl = urlBody!.BlobUrl, category }, ct);
        finalizeResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var photo = await finalizeResponse.Content.ReadFromJsonAsync<PhotoBody>(cancellationToken: ct);
        return photo!.Id;
    }

    private async Task<List<Guid>> TrainerListAsync(Actor coach, Actor client)
    {
        var response = await coach.Http.GetAsync(
            $"/trainer/clients/{client.PublicId}/photos?pageSize=100", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<TrainerPhotosBody>(
            cancellationToken: TestContext.Current.CancellationToken);
        return body!.Photos!.Select(photo => photo.Id).ToList();
    }

    [Fact]
    public async Task GetMyPhotos_GroupedByMonth_IncludesPlanlessUpload()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await TestActors.Client(factory).CreateAsync(ct);
        var photoId = await UploadUnlinkedPhotoAsync(client, "FreeForm");

        var response = await client.Http.GetAsync("/client/me/photos?groupByMonth=true&pageSize=100", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<MyPhotosBody>(cancellationToken: ct);
        var mine = body!.Groups!.SelectMany(group => group.Photos).Single(photo => photo.Id == photoId);
        mine.PlanId.Should().BeNull();
    }

    [Fact]
    public async Task GetTrainerClientPhotos_LinkedCoach_SeesPlanlessUpload()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await TestActors.Client(factory).CreateAsync(ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(ct);
        await TestActors.Link(factory, coach, client).CreateAsync(ct);
        var photoId = await UploadUnlinkedPhotoAsync(client, "Body");

        (await TrainerListAsync(coach, client)).Should().Contain(photoId);
    }

    [Fact]
    public async Task GetTrainerClientPhotos_CoachWithoutLiveLinkAtUpload_DoesNotSeePhoto()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await TestActors.Client(factory).CreateAsync(ct);
        var linkedCoach = await TestActors.Trainer(factory).CreateAsync(ct);
        var unlinkedCoach = await TestActors.Trainer(factory).CreateAsync(ct);
        await TestActors.Link(factory, linkedCoach, client).CreateAsync(ct);
        await TestActors.Link(factory, unlinkedCoach, client).Inactive().CreateAsync(ct);
        var photoId = await UploadUnlinkedPhotoAsync(client, "Body");

        var response = await unlinkedCoach.Http.GetAsync(
            $"/trainer/clients/{client.PublicId}/photos?pageSize=100", ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await TrainerListAsync(linkedCoach, client)).Should().Contain(photoId);
    }

    [Fact]
    public async Task GetTrainerClientPhotos_CoachLinkedAfterUpload_DoesNotSeePhoto()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await TestActors.Client(factory).CreateAsync(ct);
        var earlyCoach = await TestActors.Trainer(factory).CreateAsync(ct);
        var lateCoach = await TestActors.Trainer(factory).CreateAsync(ct);
        await TestActors.Link(factory, earlyCoach, client).CreateAsync(ct);
        var photoId = await UploadUnlinkedPhotoAsync(client, "Body");

        await TestActors.Link(factory, lateCoach, client).CreateAsync(ct);

        (await TrainerListAsync(lateCoach, client)).Should().NotContain(photoId);
        (await TrainerListAsync(earlyCoach, client)).Should().Contain(photoId);
    }
}
