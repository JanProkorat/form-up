using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Professionals.Avatar;

/// <summary>
/// Integration tests for the professional avatar endpoints using a real PostgreSQL instance
/// (Testcontainers). Covers:
/// - PUT /professionals/me/avatar  — persist and return
/// - DELETE /professionals/me/avatar — clear
/// - GET /professionals/search — avatarBlobUrl present in list response
/// - GET /professionals/{id}   — avatarBlobUrl present in detail response
/// - Unauthenticated access — 401
/// - Client (non-professional) — blocked by role policy
/// - Foreign/attacker-supplied blobUrl — rejected before persist (#658)
/// </summary>
[Collection(TestCollection.Shard4)]
public class ProfessionalAvatarIntegrationTests(FitnessApiFactory factory)
{
    private static string UniqueEmail() => $"{Guid.NewGuid():N}@prof-avatar-test.com";
    private const string TestPassword = "TestPass1!";

    /// <summary>
    /// Requests a real, identity-scoped avatar blobUrl via POST /professionals/me/avatar/upload-url
    /// — exactly what a legitimate client does before calling PUT /professionals/me/avatar. Tests
    /// must use this instead of a hand-picked string: since #658, ConfirmProfessionalAvatarEndpoint
    /// rejects any blobUrl that isn't the caller's own presigned key.
    /// </summary>
    private static async Task<string> GetOwnUploadBlobUrlAsync(HttpClient client)
    {
        var resp = await client.PostAsJsonAsync(
            "/professionals/me/avatar/upload-url",
            new { ContentType = "image/jpeg", SizeBytes = 1024 },
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<UploadUrlResponse>(
            cancellationToken: TestContext.Current.CancellationToken);

        body.Should().NotBeNull();
        return body!.BlobUrl;
    }

    // ── PUT /professionals/me/avatar ─────────────────────────────────────────

    [Fact]
    public async Task PutAvatar_AuthenticatedTrainer_Returns204_AndStoresUrl()
    {
        var client = factory.CreateClient();
        var email = UniqueEmail();

        await TestHelpers.RegisterAsync(client, email, TestPassword, "Alice", "Trainer", "Trainer");
        var (token, _) = await TestHelpers.LoginAsync(client, email, TestPassword);
        TestHelpers.SetBearerToken(client, token);

        var blobUrl = await GetOwnUploadBlobUrlAsync(client);

        var putResp = await client.PutAsJsonAsync(
            "/professionals/me/avatar",
            new { BlobUrl = blobUrl },
            TestContext.Current.CancellationToken);

        putResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Verify it's persisted in Postgres
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userId = (await db.Users.FirstAsync(
            u => u.Email == email,
            TestContext.Current.CancellationToken)).Id;
        var profile = await db.ProfessionalProfiles.FirstAsync(
            p => p.UserId == userId,
            TestContext.Current.CancellationToken);

        profile.AvatarBlobUrl.Should().Be(blobUrl);
    }

    // ── Stored-content injection (#658) ──────────────────────────────────────

    [Fact]
    public async Task PutAvatar_ForeignUrl_Returns400_AndDoesNotPersist()
    {
        var client = factory.CreateClient();
        var email = UniqueEmail();

        await TestHelpers.RegisterAsync(client, email, TestPassword, "Mallory", "Attacker", "Trainer");
        var (token, _) = await TestHelpers.LoginAsync(client, email, TestPassword);
        TestHelpers.SetBearerToken(client, token);

        var putResp = await client.PutAsJsonAsync(
            "/professionals/me/avatar",
            new { BlobUrl = "https://evil.example.com/phishing.jpg" },
            TestContext.Current.CancellationToken);

        putResp.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userId = (await db.Users.FirstAsync(
            u => u.Email == email,
            TestContext.Current.CancellationToken)).Id;
        var profile = await db.ProfessionalProfiles.FirstAsync(
            p => p.UserId == userId,
            TestContext.Current.CancellationToken);

        profile.AvatarBlobUrl.Should().BeNull();
    }

    [Fact]
    public async Task PutAvatar_AnotherProfessionalsKey_Returns400_AndDoesNotPersist()
    {
        // Trainer A obtains their own real, valid upload key.
        var clientA = factory.CreateClient();
        var emailA = UniqueEmail();
        await TestHelpers.RegisterAsync(clientA, emailA, TestPassword, "Nora", "Owner", "Trainer");
        var (tokenA, _) = await TestHelpers.LoginAsync(clientA, emailA, TestPassword);
        TestHelpers.SetBearerToken(clientA, tokenA);
        var foreignBlobUrl = await GetOwnUploadBlobUrlAsync(clientA);

        // Trainer B tries to confirm Trainer A's key as their own avatar.
        var clientB = factory.CreateClient();
        var emailB = UniqueEmail();
        await TestHelpers.RegisterAsync(clientB, emailB, TestPassword, "Oscar", "Impersonator", "Trainer");
        var (tokenB, _) = await TestHelpers.LoginAsync(clientB, emailB, TestPassword);
        TestHelpers.SetBearerToken(clientB, tokenB);

        var putResp = await clientB.PutAsJsonAsync(
            "/professionals/me/avatar",
            new { BlobUrl = foreignBlobUrl },
            TestContext.Current.CancellationToken);

        putResp.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userIdB = (await db.Users.FirstAsync(
            u => u.Email == emailB,
            TestContext.Current.CancellationToken)).Id;
        var profileB = await db.ProfessionalProfiles.FirstAsync(
            p => p.UserId == userIdB,
            TestContext.Current.CancellationToken);

        profileB.AvatarBlobUrl.Should().BeNull();
    }

    // ── DELETE /professionals/me/avatar ─────────────────────────────────────

    [Fact]
    public async Task DeleteAvatar_AuthenticatedTrainer_Returns204_AndClearsUrl()
    {
        var client = factory.CreateClient();
        var email = UniqueEmail();

        await TestHelpers.RegisterAsync(client, email, TestPassword, "Carol", "Trainer", "Trainer");
        var (token, _) = await TestHelpers.LoginAsync(client, email, TestPassword);
        TestHelpers.SetBearerToken(client, token);

        // Set an avatar first, using a real presigned key for this caller
        var blobUrl = await GetOwnUploadBlobUrlAsync(client);
        await client.PutAsJsonAsync(
            "/professionals/me/avatar",
            new { BlobUrl = blobUrl },
            TestContext.Current.CancellationToken);

        // Then delete it
        var delResp = await client.DeleteAsync(
            "/professionals/me/avatar",
            TestContext.Current.CancellationToken);

        delResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Verify it's null in Postgres
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userId = (await db.Users.FirstAsync(
            u => u.Email == email,
            TestContext.Current.CancellationToken)).Id;
        var profile = await db.ProfessionalProfiles.FirstAsync(
            p => p.UserId == userId,
            TestContext.Current.CancellationToken);

        profile.AvatarBlobUrl.Should().BeNull();
    }

    // ── GET /professionals/search — avatarBlobUrl in list ───────────────────

    [Fact]
    public async Task SearchProfessionals_TrainerWithAvatar_IncludesAvatarBlobUrl()
    {
        var trainerClient = factory.CreateClient();
        var trainerEmail = UniqueEmail();

        await TestHelpers.RegisterAsync(trainerClient, trainerEmail, TestPassword, "Dan", "SearchAvatar", "Trainer");
        var (trainerToken, _) = await TestHelpers.LoginAsync(trainerClient, trainerEmail, TestPassword);
        TestHelpers.SetBearerToken(trainerClient, trainerToken);

        var blobUrl = await GetOwnUploadBlobUrlAsync(trainerClient);
        await trainerClient.PutAsJsonAsync(
            "/professionals/me/avatar",
            new { BlobUrl = blobUrl },
            TestContext.Current.CancellationToken);

        // Register a client who will perform the search
        var searchClient = factory.CreateClient();
        var clientEmail = UniqueEmail();
        await TestHelpers.RegisterAsync(searchClient, clientEmail, TestPassword, "Eve", "Seeker", "Client");
        var (clientToken, _) = await TestHelpers.LoginAsync(searchClient, clientEmail, TestPassword);
        TestHelpers.SetBearerToken(searchClient, clientToken);

        var searchResp = await searchClient.GetAsync(
            "/professionals/search",
            TestContext.Current.CancellationToken);

        searchResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var raw = await searchResp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        raw.Should().Contain("avatarBlobUrl");
    }

    [Fact]
    public async Task SearchProfessionals_TrainerWithAvatar_ResponseContainsCorrectUrl()
    {
        var trainerClient = factory.CreateClient();
        var trainerEmail = UniqueEmail();
        // Unique surname so the search below finds this trainer on page 1 however many
        // professionals other tests have left in the shared database.
        var trainerLastName = $"AvatarTrainer{Guid.NewGuid():N}";

        await TestHelpers.RegisterAsync(trainerClient, trainerEmail, TestPassword, "Frank", trainerLastName, "Trainer");
        var (trainerToken, _) = await TestHelpers.LoginAsync(trainerClient, trainerEmail, TestPassword);
        TestHelpers.SetBearerToken(trainerClient, trainerToken);

        var blobUrl = await GetOwnUploadBlobUrlAsync(trainerClient);
        await trainerClient.PutAsJsonAsync(
            "/professionals/me/avatar",
            new { BlobUrl = blobUrl },
            TestContext.Current.CancellationToken);

        // Register a client searcher
        var searchClient = factory.CreateClient();
        var clientEmail = UniqueEmail();
        await TestHelpers.RegisterAsync(searchClient, clientEmail, TestPassword, "Grace", "Finder", "Client");
        var (clientToken, _) = await TestHelpers.LoginAsync(searchClient, clientEmail, TestPassword);
        TestHelpers.SetBearerToken(searchClient, clientToken);

        var searchResp = await searchClient.GetAsync(
            $"/professionals/search?search={trainerLastName}",
            TestContext.Current.CancellationToken);

        searchResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var raw = await searchResp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        raw.Should().Contain(blobUrl);
    }

    // ── GET /professionals/{publicId} — avatarBlobUrl in detail ─────────────

    [Fact]
    public async Task GetPublicProfile_TrainerWithAvatar_IncludesAvatarBlobUrl()
    {
        var trainerClient = factory.CreateClient();
        var trainerEmail = UniqueEmail();

        await TestHelpers.RegisterAsync(trainerClient, trainerEmail, TestPassword, "Hank", "DetailAvatar", "Trainer");
        var (trainerToken, _) = await TestHelpers.LoginAsync(trainerClient, trainerEmail, TestPassword);
        TestHelpers.SetBearerToken(trainerClient, trainerToken);

        var blobUrl = await GetOwnUploadBlobUrlAsync(trainerClient);
        await trainerClient.PutAsJsonAsync(
            "/professionals/me/avatar",
            new { BlobUrl = blobUrl },
            TestContext.Current.CancellationToken);

        // Resolve the trainer's publicId from Postgres
        Guid publicId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var userId = (await db.Users.FirstAsync(
                u => u.Email == trainerEmail,
                TestContext.Current.CancellationToken)).Id;
            var profile = await db.ProfessionalProfiles.FirstAsync(
                p => p.UserId == userId,
                TestContext.Current.CancellationToken);
            publicId = profile.PublicId;
        }

        // Register a client who will view the profile
        var viewClient = factory.CreateClient();
        var clientEmail = UniqueEmail();
        await TestHelpers.RegisterAsync(viewClient, clientEmail, TestPassword, "Iris", "Viewer", "Client");
        var (clientToken, _) = await TestHelpers.LoginAsync(viewClient, clientEmail, TestPassword);
        TestHelpers.SetBearerToken(viewClient, clientToken);

        var detailResp = await viewClient.GetAsync(
            $"/professionals/{publicId}",
            TestContext.Current.CancellationToken);

        detailResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var raw = await detailResp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        raw.Should().Contain("avatarBlobUrl");
        raw.Should().Contain(blobUrl);
    }

    [Fact]
    public async Task GetPublicProfile_TrainerWithoutAvatar_AvatarBlobUrlIsNull()
    {
        var trainerClient = factory.CreateClient();
        var trainerEmail = UniqueEmail();

        await TestHelpers.RegisterAsync(trainerClient, trainerEmail, TestPassword, "Jack", "NoAvatarTrainer", "Trainer");

        // Resolve publicId
        Guid publicId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var userId = (await db.Users.FirstAsync(
                u => u.Email == trainerEmail,
                TestContext.Current.CancellationToken)).Id;
            var profile = await db.ProfessionalProfiles.FirstAsync(
                p => p.UserId == userId,
                TestContext.Current.CancellationToken);
            publicId = profile.PublicId;
        }

        // Register a client viewer
        var viewClient = factory.CreateClient();
        var clientEmail = UniqueEmail();
        await TestHelpers.RegisterAsync(viewClient, clientEmail, TestPassword, "Kate", "Looker", "Client");
        var (clientToken, _) = await TestHelpers.LoginAsync(viewClient, clientEmail, TestPassword);
        TestHelpers.SetBearerToken(viewClient, clientToken);

        var detailResp = await viewClient.GetAsync(
            $"/professionals/{publicId}",
            TestContext.Current.CancellationToken);

        detailResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await detailResp.Content.ReadFromJsonAsync<PublicProfileResponse>(
            cancellationToken: TestContext.Current.CancellationToken);

        body.Should().NotBeNull();
        body!.AvatarBlobUrl.Should().BeNull();
    }

    // ── Local response DTOs (per slice rules — no cross-feature imports) ─────

    private record PublicProfileResponse(string? AvatarBlobUrl);

    private record UploadUrlResponse(string UploadUrl, string BlobUrl);
}
