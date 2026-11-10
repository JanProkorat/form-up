using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.ClientPhotos;

/// <summary>
/// Integration tests for POST /client/photos.
/// </summary>
[Collection(TestCollection.Name)]
public class FinalizeUnlinkedPhotoEndpointTests(FitnessApiFactory factory)
{
    private sealed record PhotoBody(Guid Id, string BlobUrl, string DisplayUrl, string Category, Guid? PlanId);

    private static string NewBlobPath(Guid userId, string extension = "jpg") =>
        $"plan-photos/unlinked/{userId}/{Guid.NewGuid()}.{extension}";

    [Fact]
    public async Task HandleAsync_ValidRequest_Returns201AndPersistsPlanlessPhoto()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await TestActors.Client(factory).CreateAsync(ct);
        var blobPath = NewBlobPath(client.UserId);

        var response = await client.Http.PostAsJsonAsync(
            "/client/photos", new { blobUrl = blobPath, category = "Body", description = " front " }, ct);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<PhotoBody>(cancellationToken: ct);
        body!.PlanId.Should().BeNull();
        body.DisplayUrl.Should().NotBeNullOrEmpty();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stored = await db.PlanPhotos.AsNoTracking().SingleAsync(photo => photo.PublicId == body.Id, ct);
        stored.PlanId.Should().BeNull();
        stored.PlanType.Should().BeNull();
        stored.LinkId.Should().BeNull();
        stored.BlobUrl.Should().Be(blobPath);
        stored.Description.Should().Be("front");
        stored.UploadedByUserId.Should().Be(client.UserId);
        stored.ClientProfileId.Should().Be(client.ProfileId);
    }

    [Fact]
    public async Task HandleAsync_BlobUnderAnotherUsersPrefix_Returns400InvalidBlobUrl()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await TestActors.Client(factory).CreateAsync(ct);
        var otherClient = await TestActors.Client(factory).CreateAsync(ct);

        var response = await client.Http.PostAsJsonAsync(
            "/client/photos", new { blobUrl = NewBlobPath(otherClient.UserId), category = "Body" }, ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(ct)).Should().Contain("INVALID_BLOB_URL");
    }

    [Fact]
    public async Task HandleAsync_PlanScopedBlobPath_Returns400InvalidBlobUrl()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await TestActors.Client(factory).CreateAsync(ct);

        var response = await client.Http.PostAsJsonAsync(
            "/client/photos",
            new { blobUrl = $"plan-photos/{Guid.NewGuid()}/{Guid.NewGuid()}.jpg", category = "Body" },
            ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(ct)).Should().Contain("INVALID_BLOB_URL");
    }

    [Fact]
    public async Task HandleAsync_NestedPathUnderCallerPrefix_Returns400InvalidBlobUrl()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await TestActors.Client(factory).CreateAsync(ct);

        var response = await client.Http.PostAsJsonAsync(
            "/client/photos",
            new { blobUrl = $"plan-photos/unlinked/{client.UserId}/nested/{Guid.NewGuid()}.jpg", category = "Body" },
            ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Photo diary request linking ────────────────────────────────────────────

    private sealed record RequestWorld(Actor Coach, Actor Client, Guid RequestId);

    private async Task<RequestWorld> SeedRequestAsync(
        Action<PhotoDiaryRequestBuilder> configure,
        CancellationToken ct)
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(ct);
        var client = await TestActors.Client(factory).CreateAsync(ct);
        var linkId = await TestActors.Link(factory, coach, client).CanViewNutritionPlans().CreateAsync(ct);

        var builder = EntityBuilder.PhotoDiaryRequest.OnLink(coach.UserId, linkId);
        configure(builder);
        var request = builder.Build();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.PhotoDiaryRequests.Add(request);
        await db.SaveChangesAsync(ct);
        return new RequestWorld(coach, client, request.Id);
    }

    private async Task<int> CountRequestPhotosAsync(Guid requestId, CancellationToken ct)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.PlanPhotos.CountAsync(photo => photo.DiaryRequestId == requestId, ct);
    }

    [Fact]
    public async Task HandleAsync_WithAcceptedDiaryRequest_Returns201_LinksPhoto_MovesRequestToInProgress_AndNotifiesCoach()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await SeedRequestAsync(b => b.WithStatus(PhotoDiaryStatus.Accepted), ct);

        var response = await world.Client.Http.PostAsJsonAsync(
            "/client/photos",
            new { blobUrl = NewBlobPath(world.Client.UserId), category = "FreeForm", diaryRequestId = world.RequestId },
            ct);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<RequestPhotoBody>(cancellationToken: ct);
        body!.DiaryRequestId.Should().Be(world.RequestId);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.PhotoDiaryRequests.AsNoTracking().SingleAsync(r => r.Id == world.RequestId, ct))
            .Status.Should().Be(PhotoDiaryStatus.InProgress);
        (await db.PlanPhotos.AsNoTracking().SingleAsync(photo => photo.PublicId == body.Id, ct))
            .DiaryRequestId.Should().Be(world.RequestId);
        (await db.PlanPhotoViewers.AsNoTracking().AnyAsync(
                viewer => viewer.ProfessionalUserId == world.Coach.UserId && viewer.PlanPhoto.PublicId == body.Id, ct))
            .Should().BeTrue("the requesting coach, linked to the client, can see the photo");

        factory.Services.GetRequiredService<FakeRealtimeNotifier>().Calls
            .Should().Contain(call => call.UserId == world.Coach.UserId && call.EventType == "photodiaryphotouploaded");
    }

    [Fact]
    public async Task HandleAsync_SecondUploadOnInProgressRequest_Returns201_AndStaysInProgress()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await SeedRequestAsync(b => b.WithStatus(PhotoDiaryStatus.InProgress), ct);

        var response = await world.Client.Http.PostAsJsonAsync(
            "/client/photos",
            new { blobUrl = NewBlobPath(world.Client.UserId), category = "Body", diaryRequestId = world.RequestId },
            ct);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.PhotoDiaryRequests.AsNoTracking().SingleAsync(r => r.Id == world.RequestId, ct))
            .Status.Should().Be(PhotoDiaryStatus.InProgress);
    }

    [Fact]
    public async Task HandleAsync_OldAcceptedBulkRequest_Returns201_BecauseOneTimeRequestsNeverExpire()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await SeedRequestAsync(b => b
            .WithMode(PhotoDiaryMode.Bulk)
            .WithStatus(PhotoDiaryStatus.Accepted)
            .CreatedAt(DateTimeOffset.UtcNow.AddDays(-6)), ct);

        var response = await world.Client.Http.PostAsJsonAsync(
            "/client/photos",
            new { blobUrl = NewBlobPath(world.Client.UserId), category = "FreeForm", diaryRequestId = world.RequestId },
            ct);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task HandleAsync_DiaryRequestOfAnotherClient_Returns404_AndWritesNoPhoto()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await SeedRequestAsync(b => b.WithStatus(PhotoDiaryStatus.Accepted), ct);
        var intruder = await TestActors.Client(factory).CreateAsync(ct);

        var response = await intruder.Http.PostAsJsonAsync(
            "/client/photos",
            new { blobUrl = NewBlobPath(intruder.UserId), category = "FreeForm", diaryRequestId = world.RequestId },
            ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await CountRequestPhotosAsync(world.RequestId, ct)).Should().Be(0);
    }

    [Fact]
    public async Task HandleAsync_UnknownDiaryRequest_Returns404_AndWritesNoPhoto()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await TestActors.Client(factory).CreateAsync(ct);

        var response = await client.Http.PostAsJsonAsync(
            "/client/photos",
            new { blobUrl = NewBlobPath(client.UserId), category = "FreeForm", diaryRequestId = Guid.NewGuid() },
            ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.PlanPhotos.CountAsync(photo => photo.ClientProfileId == client.ProfileId, ct)).Should().Be(0);
    }

    [Theory]
    [InlineData(PhotoDiaryStatus.Pending)]
    [InlineData(PhotoDiaryStatus.Dismissed)]
    [InlineData(PhotoDiaryStatus.Cancelled)]
    [InlineData(PhotoDiaryStatus.Completed)]
    public async Task HandleAsync_DiaryRequestNotAcceptedOrInProgress_Returns409InvalidStatus_AndWritesNoPhoto(
        PhotoDiaryStatus status)
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await SeedRequestAsync(b => b.WithStatus(status), ct);

        var response = await world.Client.Http.PostAsJsonAsync(
            "/client/photos",
            new { blobUrl = NewBlobPath(world.Client.UserId), category = "FreeForm", diaryRequestId = world.RequestId },
            ct);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(ct)).Should().Contain("PHOTO_DIARY_REQUEST_INVALID_STATUS");
        (await CountRequestPhotosAsync(world.RequestId, ct)).Should().Be(0);
    }

    [Fact]
    public async Task HandleAsync_WorkflowRequestPastItsEnd_Returns409Expired_AndWritesNoPhoto()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await SeedRequestAsync(b => b
            .WithStatus(PhotoDiaryStatus.Accepted)
            .WithDurationDays(3)
            .CreatedAt(DateTimeOffset.UtcNow.AddDays(-10)), ct);

        var response = await world.Client.Http.PostAsJsonAsync(
            "/client/photos",
            new { blobUrl = NewBlobPath(world.Client.UserId), category = "FreeForm", diaryRequestId = world.RequestId },
            ct);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(ct)).Should().Contain("PHOTO_DIARY_REQUEST_EXPIRED");
        (await CountRequestPhotosAsync(world.RequestId, ct)).Should().Be(0);
    }

    private sealed record RequestPhotoBody(Guid Id, Guid? DiaryRequestId);

    [Theory]
    [InlineData("Food")]
    [InlineData("Training")]
    public async Task HandleAsync_CategoryNeedingAPlan_Returns400(string category)
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await TestActors.Client(factory).CreateAsync(ct);

        var response = await client.Http.PostAsJsonAsync(
            "/client/photos", new { blobUrl = NewBlobPath(client.UserId), category }, ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
