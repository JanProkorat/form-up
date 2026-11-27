using FormUp.Application.Domain.Constants;
using FormUp.Application.Features.Messaging.Shared;
using FormUp.Application.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace FormUp.Tests.Infrastructure.Services;

/// <summary>
/// Unit tests for <see cref="MinioBlobStorageService"/> — the F9 fail-closed signing contract
/// and the URL-normalization helpers it exposes. These are pure unit tests: no Docker / MinIO
/// container is required, because <c>GenerateReadUrlAsync</c>'s presigning and
/// <c>NormalizeToCanonicalUrl</c>'s prefix matching are both local, offline computations — the
/// MinIO SDK's presigned-URL signing never makes a network round trip.
///
/// <para>
/// Before this test class, every double implementing <see cref="FormUp.Application.Domain.Interfaces.IBlobStorageService"/>
/// in the test suite (<c>FormUp.Tests.Infrastructure.FakeBlobStorageService</c>, the
/// seed runner's <c>TrackingBlobStorageService</c>) returned a non-empty value from every call,
/// so none of the fail-closed contract added in commit 8b3baebf was ever asserted anywhere
/// against the real implementation.
/// </para>
/// </summary>
public class MinioBlobStorageServiceTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static MinioBlobStorageService CreateService(
        out ILogger<MinioBlobStorageService> logger,
        bool publicUrlIncludesBucket = true,
        string publicEndpoint = "http://localhost:9000",
        string bucketName = "form-up",
        string? presignEndpoint = null,
        string? presignSecure = null)
    {
        logger = Substitute.For<ILogger<MinioBlobStorageService>>();

        var settings = new Dictionary<string, string?>
        {
            ["MinIO:Endpoint"] = "localhost:9000",
            ["MinIO:AccessKey"] = "minioadmin",
            ["MinIO:SecretKey"] = "minioadmin",
            ["MinIO:Secure"] = "false",
            ["MinIO:Region"] = "us-east-1",
            ["MinIO:BucketName"] = bucketName,
            ["MinIO:ManageBucket"] = "false",
            ["MinIO:PublicUrlIncludesBucket"] = publicUrlIncludesBucket ? "true" : "false",
            ["MinIO:PublicEndpoint"] = publicEndpoint,
            [ConfigKeys.MinIoReadUrlExpiryMinutes] = "15"
        };

        if (presignEndpoint is not null)
        {
            settings["MinIO:PresignEndpoint"] = presignEndpoint;
        }

        if (presignSecure is not null)
        {
            settings["MinIO:PresignSecure"] = presignSecure;
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        return new MinioBlobStorageService(configuration, logger);
    }

    // ── GenerateReadUrlAsync — fail-closed contract ────────────────────────────

    [Fact]
    public async Task GenerateReadUrlAsync_NullInput_PassesThroughUnchanged()
    {
        var service = CreateService(out _);

        var result = await service.GenerateReadUrlAsync(null, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GenerateReadUrlAsync_EmptyInput_PassesThroughUnchanged()
    {
        var service = CreateService(out _);

        var result = await service.GenerateReadUrlAsync(string.Empty, CancellationToken.None);

        result.Should().Be(string.Empty);
    }

    [Fact]
    public async Task GenerateReadUrlAsync_ForeignPrefix_ReturnsEmptyString_NotStoredValue()
    {
        // Root cause this proves: before commit 8b3baebf, an unparseable stored URL fell back to
        // the raw permanent value via "?? storedBlobUrl", handing the caller an unauthenticated,
        // never-expiring URL — silently undoing F9 for every photo whose row predates a
        // MinIO:PublicEndpoint / BucketName / PublicUrlIncludesBucket change. Revert the
        // fail-closed `return string.Empty;` branch in GenerateReadUrlAsync back to
        // `return storedBlobUrl;` and this assertion fails (result equals the foreign input
        // instead of empty).
        var service = CreateService(out _);
        const string foreignUrl = "https://totally-unrelated-host.example/some/path.jpg";

        var result = await service.GenerateReadUrlAsync(foreignUrl, CancellationToken.None);

        result.Should().Be(string.Empty);
        result.Should().NotBe(foreignUrl);
    }

    [Fact]
    public async Task GenerateReadUrlAsync_ForeignPrefix_LogsWarning()
    {
        // Load-bearing: remove the _logger.LogWarning call from the fail-closed branch and this
        // assertion fails — silent fail-closed extraction is a diagnosability regression, per
        // the review's finding that empty-string-on-failure must remain "visible, diagnosable".
        var service = CreateService(out var logger);
        const string foreignUrl = "https://totally-unrelated-host.example/some/path.jpg";

        await service.GenerateReadUrlAsync(foreignUrl, CancellationToken.None);

        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task GenerateReadUrlAsync_StrictPrefixMatch_RejectsPartialPrefixCollision()
    {
        // The prefix match is a literal StartsWith on "{publicBase}/{bucket}/" — a URL that
        // merely shares the host but diverges on the bucket segment must NOT be treated as
        // belonging to this service. Revert the prefix computation to a looser match (e.g. host
        // only) and this assertion fails (result would resolve instead of failing closed).
        var service = CreateService(out _, publicUrlIncludesBucket: true, bucketName: "form-up");

        // Correct bucket is "form-up" — this uses a different bucket segment
        // ("other-bucket") under the same host, so the prefix must not match.
        const string wrongBucketUrl = "http://localhost:9000/other-bucket/plan-photos/abc/photo.jpg";

        var result = await service.GenerateReadUrlAsync(wrongBucketUrl, CancellationToken.None);

        result.Should().Be(string.Empty);
    }

    // ── GenerateReadUrlAsync — round trip via BuildPublicUrl (both bucket-URL shapes) ──────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GenerateReadUrlAsync_RoundTripsBuildPublicUrl_ForBothPublicUrlIncludesBucketValues(
        bool publicUrlIncludesBucket)
    {
        // Root cause this proves: TryExtractContainerPath must reverse BuildPublicUrl exactly —
        // it computes its own prefix from _publicEndpoint/_bucketName/_publicUrlIncludesBucket
        // independently of BuildPublicUrl's construction, so the two could silently drift apart
        // in either config branch. Revert either method's prefix logic without keeping them in
        // sync and this test fails for the affected branch: extraction fails, GenerateReadUrlAsync
        // falls back to its fail-closed empty-string branch instead of returning a signed URL.
        //
        // Note: the returned SIGNED url is not asserted to reuse BuildPublicUrl's host/bucket
        // shape — the MinIO client signs against its own S3 endpoint (always path-style,
        // "{endpoint}/{bucket}/{object}"), independent of PublicUrlIncludesBucket, which governs
        // only the PUBLIC-facing shape BuildPublicUrl returns (see the review's MINOR finding on
        // MinIO:Endpoint vs MinIO:PublicEndpoint — a known, disclosed divergence, not a defect
        // this test is about). What this test proves is narrower and load-bearing on its own:
        // extraction from a BuildPublicUrl-produced URL must succeed (not fail closed) in BOTH
        // configuration branches.
        var service = CreateService(out _, publicUrlIncludesBucket: publicUrlIncludesBucket);

        const string containerPath = "plan-photos/plan-abc/photo.jpg";
        var storedBlobUrl = service.BuildPublicUrl(containerPath);

        var result = await service.GenerateReadUrlAsync(storedBlobUrl, CancellationToken.None);

        result.Should().NotBeNullOrEmpty(
            "extraction of a BuildPublicUrl-produced URL must succeed, not fail closed, in this PublicUrlIncludesBucket branch");
        result.Should().NotBe(storedBlobUrl, "a signed URL must differ from the stored canonical value");
        result!.Should().Contain(containerPath, "the signed URL must still resolve to the same underlying object");
    }

    // ── GenerateReadUrlAsync — chat image container-path shape (SendMessageEndpoint) ──────────

    [Fact]
    public async Task GenerateReadUrlAsync_ChatImageBarePath_FailsClosed_NotBugSymptom()
    {
        // Reproduces the SendMessageEndpoint bug directly: it used to store the BARE container
        // path returned by ChatImagePolicy.BuildFinalContainerPath (e.g.
        // "chat/<conversationId>/<messageId>.jpg") instead of running it through BuildPublicUrl.
        // TryExtractContainerPath expects the full "{publicBase}/{bucket}/" prefix, so a bare
        // path never matches and GenerateReadUrlAsync fails closed to string.Empty — this is the
        // exact "Could not derive a container path" warning observed on the compose harness for
        // every chat image.
        var service = CreateService(out _);
        var barePath = ChatImagePolicy.BuildFinalContainerPath(Guid.NewGuid(), Guid.NewGuid(), "jpg");

        var result = await service.GenerateReadUrlAsync(barePath, CancellationToken.None);

        result.Should().Be(string.Empty, "a bare container path is exactly the pre-fix stored value and must fail closed");
    }

    [Fact]
    public async Task GenerateReadUrlAsync_ChatImagePublicUrlForm_RoundTripsToSignedUrl()
    {
        // The fix: SendMessageEndpoint now stores blobStorage.BuildPublicUrl(finalPath), not the
        // bare finalPath (see the test above). This proves that stored form round-trips through
        // the REAL MinioBlobStorageService into a non-empty signed URL, using the exact
        // ChatImagePolicy path shape SendMessageEndpoint builds.
        var service = CreateService(out _);
        var finalPath = ChatImagePolicy.BuildFinalContainerPath(Guid.NewGuid(), Guid.NewGuid(), "jpg");
        var storedImageBlobUrl = service.BuildPublicUrl(finalPath);

        var result = await service.GenerateReadUrlAsync(storedImageBlobUrl, CancellationToken.None);

        result.Should().NotBeNullOrEmpty("the stored public-URL form must resolve to a signed read URL, not fail closed");
        result!.Should().Contain(finalPath, "the signed URL must still resolve to the same underlying chat image object");
    }

    // ── NormalizeToCanonicalUrl ─────────────────────────────────────────────────

    [Fact]
    public void NormalizeToCanonicalUrl_NullOrEmpty_ReturnsNull()
    {
        var service = CreateService(out _);

        service.NormalizeToCanonicalUrl(string.Empty).Should().BeNull();
    }

    [Fact]
    public void NormalizeToCanonicalUrl_ForeignValue_ReturnsNull()
    {
        var service = CreateService(out _);

        service.NormalizeToCanonicalUrl("https://totally-unrelated-host.example/x.jpg")
            .Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NormalizeToCanonicalUrl_StripsSignedQueryString_BackToCanonicalForm(
        bool publicUrlIncludesBucket)
    {
        // Load-bearing for the write-path defence-in-depth fix: a client that echoes back a
        // signed read URL (query string intact) must normalize to EXACTLY the same value
        // BuildPublicUrl produces — not a value that still carries the signature. Revert
        // NormalizeToCanonicalUrl to skip the query-string strip and this assertion fails (the
        // "normalized" value would still contain "?X-Amz-Signature=...").
        var service = CreateService(out _, publicUrlIncludesBucket: publicUrlIncludesBucket);

        const string containerPath = "plan-photos/plan-abc/photo.jpg";
        var canonical = service.BuildPublicUrl(containerPath);
        var signedEcho = $"{canonical}?X-Amz-Signature=deadbeef&X-Amz-Expires=900";

        var normalized = service.NormalizeToCanonicalUrl(signedEcho);

        normalized.Should().Be(canonical);
        normalized.Should().NotContain("?");
    }

    [Fact]
    public void NormalizeToCanonicalUrl_RelativeContainerPath_NormalizesToCanonicalUrl()
    {
        // A client may submit the bare container path (no scheme) rather than the full public
        // URL — both write-path validators historically accepted this shape. It must still
        // normalize to the canonical, full form so it round-trips through GenerateReadUrlAsync.
        var service = CreateService(out _);

        const string containerPath = "plan-photos/plan-abc/photo.jpg";
        var expected = service.BuildPublicUrl(containerPath);

        var normalized = service.NormalizeToCanonicalUrl(containerPath);

        normalized.Should().Be(expected);
    }

    // ── ReadUrlExpiryMinutes default ────────────────────────────────────────────

    [Fact]
    public async Task GenerateReadUrlAsync_NoExpiryConfigured_DefaultsTo15Minutes()
    {
        // Load-bearing: build a service with NO MinIO:ReadUrlExpiryMinutes key at all (as
        // render.yaml / docker-compose.test.yml currently do — the review flagged this key as
        // absent from both) and confirm the presigned URL still carries a 900-second
        // (15-minute) expiry window rather than throwing or defaulting to something else.
        // Revert the "15" default literal in the constructor and this assertion fails.
        var logger = Substitute.For<ILogger<MinioBlobStorageService>>();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MinIO:Endpoint"] = "localhost:9000",
                ["MinIO:AccessKey"] = "minioadmin",
                ["MinIO:SecretKey"] = "minioadmin",
                ["MinIO:Secure"] = "false",
                ["MinIO:Region"] = "us-east-1",
                ["MinIO:BucketName"] = "form-up",
                ["MinIO:ManageBucket"] = "false",
                ["MinIO:PublicUrlIncludesBucket"] = "true",
                ["MinIO:PublicEndpoint"] = "http://localhost:9000"
                // MinIO:ReadUrlExpiryMinutes intentionally absent.
            })
            .Build();
        var service = new MinioBlobStorageService(configuration, logger);

        const string containerPath = "plan-photos/plan-abc/photo.jpg";
        var storedBlobUrl = service.BuildPublicUrl(containerPath);

        var result = await service.GenerateReadUrlAsync(storedBlobUrl, CancellationToken.None);

        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("Expires=900", "15 minutes == 900 seconds, the MinIO SDK's presigned-URL expiry query parameter");
    }

    // ── PresignEndpoint — separate client for presigned URLs ───────────────────

    [Fact]
    public async Task GenerateUploadUrlAsync_NoPresignEndpointConfigured_SignsAgainstRegularEndpoint()
    {
        // Default behaviour, unchanged: with no MinIO:PresignEndpoint set, the upload URL is
        // signed against the same host server-side calls use.
        var service = CreateService(out _);

        var result = await service.GenerateUploadUrlAsync(
            "chat-uploads/conv-1/upload-1.jpg", "image/jpeg", TimeSpan.FromMinutes(15), CancellationToken.None);

        result.UploadUrl.Should().StartWith("http://localhost:9000/");
    }

    [Fact]
    public async Task GenerateUploadUrlAsync_PresignEndpointConfigured_SignsAgainstPresignEndpoint_NotRegularEndpoint()
    {
        // Root cause this proves: reusing the regular MinIO:Endpoint client for presigning signs
        // a URL against a host the caller (a browser, outside the docker network) may not be able
        // to reach — e.g. "minio-test:9000" in docker-compose.test.yml. With MinIO:PresignEndpoint
        // configured, the presigned upload URL must name THAT host instead.
        var service = CreateService(out _, presignEndpoint: "localhost:29000");

        var result = await service.GenerateUploadUrlAsync(
            "chat-uploads/conv-1/upload-1.jpg", "image/jpeg", TimeSpan.FromMinutes(15), CancellationToken.None);

        result.UploadUrl.Should().StartWith("http://localhost:29000/");
        result.UploadUrl.Should().NotContain("localhost:9000/", "the presign endpoint host must fully replace the regular endpoint, not merely be appended");
    }

    [Fact]
    public async Task GenerateReadUrlAsync_PresignEndpointConfigured_SignsAgainstPresignEndpoint_NotRegularEndpoint()
    {
        // Same contract as the upload URL, for the read/GET side — GenerateReadUrlAsync is the
        // only path a stored blob URL resolves to fetchable bytes through (F9), so it must honor
        // MinIO:PresignEndpoint too.
        var service = CreateService(out _, presignEndpoint: "localhost:29000");

        const string containerPath = "chat/conv-1/msg-1.jpg";
        var storedBlobUrl = service.BuildPublicUrl(containerPath);

        var result = await service.GenerateReadUrlAsync(storedBlobUrl, CancellationToken.None);

        result.Should().NotBeNullOrEmpty();
        result!.Should().StartWith("http://localhost:29000/");
        result.Should().NotContain("localhost:9000/", "the presign endpoint host must fully replace the regular endpoint, not merely be appended");
    }

    [Fact]
    public async Task GenerateUploadUrlAsync_PresignSecureUnset_DefaultsToSecure()
    {
        // MinIO:PresignSecure is optional and must fall back to MinIO:Secure (false in every test
        // fixture here) rather than defaulting independently to true or false.
        var service = CreateService(out _, presignEndpoint: "localhost:29000");

        var result = await service.GenerateUploadUrlAsync(
            "chat-uploads/conv-1/upload-1.jpg", "image/jpeg", TimeSpan.FromMinutes(15), CancellationToken.None);

        result.UploadUrl.Should().StartWith("http://", "MinIO:Secure is false in the test fixture and PresignSecure was not overridden");
    }

    [Fact]
    public async Task GenerateUploadUrlAsync_PresignSecureTrue_OverridesSecureIndependently()
    {
        // PresignSecure must be settable independently of Secure — the two clients can genuinely
        // differ (e.g. a harness serving plain HTTP internally behind a TLS-terminating host
        // proxy the browser reaches over HTTPS).
        var service = CreateService(out _, presignEndpoint: "localhost:29000", presignSecure: "true");

        var result = await service.GenerateUploadUrlAsync(
            "chat-uploads/conv-1/upload-1.jpg", "image/jpeg", TimeSpan.FromMinutes(15), CancellationToken.None);

        result.UploadUrl.Should().StartWith("https://localhost:29000/");
    }
}
