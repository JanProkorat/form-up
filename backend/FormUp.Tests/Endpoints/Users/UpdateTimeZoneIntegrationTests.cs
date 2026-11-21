using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Tests.Infrastructure;

namespace FormUp.Tests.Endpoints.Users;

/// <summary>
/// Integration tests for <c>PUT /users/me/timezone</c> and the timezone field
/// on <c>GET /users/me</c> using a real PostgreSQL instance (Testcontainers).
/// </summary>
[Collection(TestCollection.Shard3)]
public class UpdateTimeZoneIntegrationTests(FitnessApiFactory factory)
{
    private static string UniqueEmail() => $"{Guid.NewGuid():N}@timezone-test.com";

    /// <summary>
    /// PUT /users/me/timezone with a valid IANA ID returns 200 and persists the value.
    /// GET /users/me subsequently returns the updated time zone.
    /// </summary>
    [Fact]
    public async Task PutTimezone_ValidIana_Returns200AndGetMeReflectsChange()
    {
        var client = factory.CreateClient();
        var email = UniqueEmail();

        await TestHelpers.RegisterAsync(client, email, "TestPass1!", "Test", "User", "Client");
        var (accessToken, _) = await TestHelpers.LoginAsync(client, email, "TestPass1!");
        TestHelpers.SetBearerToken(client, accessToken);

        // PUT with a valid IANA timezone
        var putResponse = await client.PutAsJsonAsync(
            "/users/me/timezone",
            new { TimeZone = "America/New_York" },
            TestContext.Current.CancellationToken);

        putResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // GET /users/me must now return the updated timezone
        var getResponse = await client.GetAsync(
            "/users/me",
            TestContext.Current.CancellationToken);

        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var profile = await getResponse.Content.ReadFromJsonAsync<ProfileResponse>(
            cancellationToken: TestContext.Current.CancellationToken);

        profile.Should().NotBeNull();
        profile!.TimeZone.Should().Be("America/New_York");
    }

    /// <summary>
    /// PUT /users/me/timezone with an invalid zone string returns 400 with an RFC 7807 payload.
    /// </summary>
    [Fact]
    public async Task PutTimezone_InvalidZone_Returns400WithRfc7807()
    {
        var client = factory.CreateClient();
        var email = UniqueEmail();

        await TestHelpers.RegisterAsync(client, email, "TestPass1!", "Test", "User", "Client");
        var (accessToken, _) = await TestHelpers.LoginAsync(client, email, "TestPass1!");
        TestHelpers.SetBearerToken(client, accessToken);

        var putResponse = await client.PutAsJsonAsync(
            "/users/me/timezone",
            new { TimeZone = "Not/A/Zone" },
            TestContext.Current.CancellationToken);

        putResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await putResponse.Content.ReadFromJsonAsync<ProblemResponse>(
            cancellationToken: TestContext.Current.CancellationToken);

        body.Should().NotBeNull();
        // FastEndpoints wraps ValidationFailureException in a 400 with errors array
        // The error code must be present somewhere in the response.
        var raw = await putResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        raw.Should().Contain("INVALID_TIME_ZONE");
    }

    /// <summary>
    /// GET /users/me for a newly registered user returns the default timezone "Europe/Prague".
    /// </summary>
    [Fact]
    public async Task GetMe_NewUser_ReturnsDefaultTimeZone()
    {
        var client = factory.CreateClient();
        var email = UniqueEmail();

        await TestHelpers.RegisterAsync(client, email, "TestPass1!", "Test", "User", "Client");
        var (accessToken, _) = await TestHelpers.LoginAsync(client, email, "TestPass1!");
        TestHelpers.SetBearerToken(client, accessToken);

        var getResponse = await client.GetAsync(
            "/users/me",
            TestContext.Current.CancellationToken);

        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var profile = await getResponse.Content.ReadFromJsonAsync<ProfileResponse>(
            cancellationToken: TestContext.Current.CancellationToken);

        profile.Should().NotBeNull();
        profile!.TimeZone.Should().Be("Europe/Prague");
    }

    // ── Local response DTOs (per slice rules — no cross-feature imports) ──

    private record ProfileResponse(string TimeZone);

    private record ProblemResponse(int? Status, string? Title, string? Type);
}
