using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Users;

/// <summary>
/// Integration tests for <c>POST /users/me/password</c> against a real PostgreSQL instance.
/// </summary>
[Collection(TestCollection.Shard2)]
public class ChangePasswordEndpointIntegrationTests(FitnessApiFactory factory)
{
    private const string OldPassword = "OldPass123!";
    private const string NewPassword = "NewPass456!";

    private static string UniqueEmail() => $"{Guid.NewGuid():N}@changepw-test.com";

    private async Task<(HttpClient Http, string Email, string RefreshToken, Guid UserId)> RegisterAndLoginAsync()
    {
        var http = factory.CreateClient();
        var email = UniqueEmail();

        await TestHelpers.RegisterAsync(http, email, OldPassword, "Pat", "Trainer", "Trainer");
        var (accessToken, refreshToken) = await TestHelpers.LoginAsync(http, email, OldPassword);
        TestHelpers.SetBearerToken(http, accessToken);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userId = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync(TestContext.Current.CancellationToken);

        return (http, email, refreshToken, userId);
    }

    private static object Body(string current, string next, string? confirm = null) =>
        new { CurrentPassword = current, NewPassword = next, ConfirmPassword = confirm ?? next };

    private static async Task<List<string?>> ErrorReasonsAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("errors", out var errors))
        {
            return [];
        }

        return errors.EnumerateArray()
            .Select(e => e.TryGetProperty("code", out var code) ? code.GetString() : null)
            .ToList();
    }

    [Fact]
    public async Task Post_ValidChange_RevokesOldTokensIssuesWorkingNewOnesAndSetsDate()
    {
        var ct = TestContext.Current.CancellationToken;
        var (http, email, oldRefreshToken, userId) = await RegisterAndLoginAsync();

        var response = await http.PostAsJsonAsync("/users/me/password", Body(OldPassword, NewPassword), ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ChangePasswordResult>(ct);
        body!.AccessToken.Should().NotBeNullOrEmpty();
        body.RefreshToken.Should().NotBeNullOrEmpty().And.NotBe(oldRefreshToken);
        body.PasswordChangedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var tokens = await db.RefreshTokens.AsNoTracking()
                .Where(t => t.UserId == userId)
                .ToListAsync(ct);

            tokens.Should().ContainSingle(t => t.RevokedAt == null).Which.Token.Should().Be(body.RefreshToken);
            tokens.Where(t => t.Token == oldRefreshToken).Should().ContainSingle().Which.RevokedAt.Should().NotBeNull();

            var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId, ct);
            user.PasswordChangedAt.Should().NotBeNull();
        }

        var anonymous = factory.CreateClient();
        var oldRefresh = await anonymous.PostAsJsonAsync("/auth/refresh", new { RefreshToken = oldRefreshToken }, ct);
        oldRefresh.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var newRefresh = await anonymous.PostAsJsonAsync("/auth/refresh", new { RefreshToken = body.RefreshToken }, ct);
        newRefresh.StatusCode.Should().Be(HttpStatusCode.OK);

        var oldLogin = await anonymous.PostAsJsonAsync("/auth/login", new { Email = email, Password = OldPassword }, ct);
        oldLogin.StatusCode.Should().NotBe(HttpStatusCode.OK);

        var newLogin = await anonymous.PostAsJsonAsync("/auth/login", new { Email = email, Password = NewPassword }, ct);
        newLogin.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Post_WrongCurrentPassword_Returns400WithCodeAndChangesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (http, email, refreshToken, userId) = await RegisterAndLoginAsync();

        var response = await http.PostAsJsonAsync("/users/me/password", Body("WrongPass123!", NewPassword), ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorReasonsAsync(response)).Should().Contain("INVALID_CURRENT_PASSWORD");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var token = await db.RefreshTokens.AsNoTracking().SingleAsync(t => t.Token == refreshToken, ct);
        token.RevokedAt.Should().BeNull();
        (await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId, ct)).PasswordChangedAt.Should().BeNull();

        var login = await factory.CreateClient()
            .PostAsJsonAsync("/auth/login", new { Email = email, Password = OldPassword }, ct);
        login.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Post_NewPasswordViolatesPolicy_Returns400()
    {
        var (http, _, _, _) = await RegisterAndLoginAsync();

        var response = await http.PostAsJsonAsync(
            "/users/me/password", Body(OldPassword, "alllowercase1"), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Post_AccountWithoutPassword_Returns400PasswordNotSet()
    {
        var actor = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);

        var response = await actor.Http.PostAsJsonAsync(
            "/users/me/password", Body(OldPassword, NewPassword), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorReasonsAsync(response)).Should().Contain("PASSWORD_NOT_SET");
    }

    [Fact]
    public async Task Post_UserNoLongerExists_Returns401()
    {
        // #1517: the email-verification gate answers 401 for a valid token whose user no longer exists,
        // so the request never reaches the endpoint's own 404 guard.
        var http = factory.CreateClient();
        var token = TestTokenFactory.CreateAccessToken(
            factory, Guid.NewGuid(), UniqueEmail(), [UserRole.Trainer.ToString()]);
        TestHelpers.SetBearerToken(http, token);

        var response = await http.PostAsJsonAsync(
            "/users/me/password", Body(OldPassword, NewPassword), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private record ChangePasswordResult(
        string AccessToken, string RefreshToken, DateTime ExpiresAt, DateTime PasswordChangedAt);
}
