using System.Net;
using System.Net.Http.Json;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;

namespace FormUp.Tests.Endpoints.ClientPhotos;

/// <summary>
/// Integration tests for POST /client/photos/upload-url.
/// </summary>
[Collection(TestCollection.Shard2)]
public class GenerateUnlinkedPhotoUploadUrlEndpointTests(FitnessApiFactory factory)
{
    private sealed record UploadUrlBody(string UploadUrl, string BlobUrl);

    [Fact]
    public async Task HandleAsync_ValidRequest_Returns200WithCallerBoundUnlinkedPath()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await TestActors.Client(factory).CreateAsync(ct);

        var response = await client.Http.PostAsJsonAsync(
            "/client/photos/upload-url", new { contentType = "image/png", sizeBytes = 2048 }, ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<UploadUrlBody>(cancellationToken: ct);
        body!.BlobUrl.Should().StartWith($"plan-photos/unlinked/{client.UserId}/").And.EndWith(".png");
    }

    [Fact]
    public async Task HandleAsync_UnsupportedContentType_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await TestActors.Client(factory).CreateAsync(ct);

        var response = await client.Http.PostAsJsonAsync(
            "/client/photos/upload-url", new { contentType = "image/gif", sizeBytes = 2048 }, ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
