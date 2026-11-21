using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Tests.Infrastructure;

namespace FormUp.Tests.Users;

/// <summary>
/// Integration tests for user profile endpoints: GET /users/me, PUT /users/me.
/// </summary>
[Collection(TestCollection.Shard3)]
public class ProfileTests(FitnessApiFactory factory)
{
    private static string UniqueEmail() => $"{Guid.NewGuid():N}@test.com";

    [Fact]
    public async Task UpdateProfile_WithValidData_Returns200()
    {
        var client = factory.CreateClient();
        var email = UniqueEmail();

        await TestHelpers.RegisterAsync(client, email, "TestPass1!", "John", "Doe", "Client");
        var (accessToken, _) = await TestHelpers.LoginAsync(client, email, "TestPass1!");
        TestHelpers.SetBearerToken(client, accessToken);

        var response = await client.PutAsJsonAsync("/users/me", new
        {
            FirstName = "Jane",
            LastName = "Smith"
        }, cancellationToken: TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify the profile was updated
        var profileResponse = await client.GetAsync("/users/me", TestContext.Current.CancellationToken);
        var profile = await profileResponse.Content.ReadFromJsonAsync<ProfileResult>(cancellationToken: TestContext.Current.CancellationToken);

        profile!.FirstName.Should().Be("Jane");
        profile.LastName.Should().Be("Smith");
    }

    [Fact]
    public async Task UpdateProfile_EmptyFirstName_Returns400()
    {
        var client = factory.CreateClient();
        var email = UniqueEmail();

        await TestHelpers.RegisterAsync(client, email, "TestPass1!", "John", "Doe", "Client");
        var (accessToken, _) = await TestHelpers.LoginAsync(client, email, "TestPass1!");
        TestHelpers.SetBearerToken(client, accessToken);

        var response = await client.PutAsJsonAsync("/users/me", new
        {
            FirstName = "",
            LastName = "Smith"
        }, cancellationToken: TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdateProfile_TooLongLastName_Returns400()
    {
        var client = factory.CreateClient();
        var email = UniqueEmail();

        await TestHelpers.RegisterAsync(client, email, "TestPass1!", "John", "Doe", "Client");
        var (accessToken, _) = await TestHelpers.LoginAsync(client, email, "TestPass1!");
        TestHelpers.SetBearerToken(client, accessToken);

        var response = await client.PutAsJsonAsync("/users/me", new
        {
            FirstName = "Jane",
            LastName = new string('A', 51)
        }, cancellationToken: TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetProfile_ReturnsCorrectRoles()
    {
        var client = factory.CreateClient();
        var email = UniqueEmail();

        await TestHelpers.RegisterAsync(client, email, "TestPass1!", "John", "Doe", "Trainer");
        var (accessToken, _) = await TestHelpers.LoginAsync(client, email, "TestPass1!");
        TestHelpers.SetBearerToken(client, accessToken);

        var response = await client.GetAsync("/users/me", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<ProfileResult>(cancellationToken: TestContext.Current.CancellationToken);

        body!.Roles.Should().Contain("Trainer");
    }

    private record ProfileResult(Guid UserId, string Email, string FirstName, string LastName, List<string> Roles);
}
