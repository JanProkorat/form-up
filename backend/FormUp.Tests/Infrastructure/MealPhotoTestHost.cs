using FormUp.Application.Domain.Interfaces;
using FormUp.Tests.Builders;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Infrastructure;

/// <summary>
/// A host derived from <see cref="FitnessApiFactory"/> whose blob storage builds full
/// <c>https://</c> public URLs, as production does. The default fake returns bare container
/// paths, which the meal-photo request validator rejects (it requires an HTTP/HTTPS URL), so
/// tests that save meal photos over HTTP need this host to post a URL that is both valid and
/// under <c>diary/{mealId}/</c>.
/// </summary>
public static class MealPhotoTestHost
{
    /// <summary>Public-URL root the derived host's blob storage prepends to every container path.</summary>
    public const string BlobRoot = "https://blob.test/bucket/";

    private static WebApplicationFactory<Program>? _host;

    /// <summary>
    /// Returns a client on the derived host that carries <paramref name="actor"/>'s bearer token.
    /// </summary>
    /// <remarks>
    /// Memoized in a static field and never disposed, like the other derived hosts in this suite
    /// (disposing one repoints FastEndpoints' process-global service resolver at a dead provider).
    /// </remarks>
    public static HttpClient CreateClientFor(FitnessApiFactory factory, Actor actor)
    {
        _host ??= FastEndpointsBootGate.Booted(factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IBlobStorageService)).ToList())
            {
                services.Remove(descriptor);
            }

            services.AddSingleton<IBlobStorageService, PrefixedBlobStorage>();
        })));

        var client = _host.CreateClient();
        client.DefaultRequestHeaders.Authorization = actor.Http.DefaultRequestHeaders.Authorization;
        return client;
    }

    private sealed class PrefixedBlobStorage : IBlobStorageService
    {
        private readonly FakeBlobStorageService _inner = new();

        public string BuildPublicUrl(string containerPath) => BlobRoot + containerPath;

        public async Task<BlobUploadUrl> GenerateUploadUrlAsync(
            string containerPath, string contentType, TimeSpan expiresIn, CancellationToken ct)
        {
            var upload = await _inner.GenerateUploadUrlAsync(containerPath, contentType, expiresIn, ct);
            return upload with { BlobUrl = BuildPublicUrl(containerPath) };
        }

        public Task<string?> GenerateReadUrlAsync(string? storedBlobUrl, CancellationToken ct) =>
            _inner.GenerateReadUrlAsync(storedBlobUrl, ct);

        public string? NormalizeToCanonicalUrl(string blobUrl) => _inner.NormalizeToCanonicalUrl(blobUrl);

        public Task UploadAsync(string containerPath, byte[] data, string contentType, CancellationToken ct) =>
            _inner.UploadAsync(containerPath, data, contentType, ct);

        public Task<bool> ObjectExistsAsync(string containerPath, CancellationToken ct) =>
            _inner.ObjectExistsAsync(containerPath, ct);

        public Task DeleteAsync(string containerPath, CancellationToken ct) => _inner.DeleteAsync(containerPath, ct);

        public Task<BlobObject?> DownloadAsync(string containerPath, long maxBytesToDownload, CancellationToken ct) =>
            _inner.DownloadAsync(containerPath, maxBytesToDownload, ct);
    }
}
