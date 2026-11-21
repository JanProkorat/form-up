using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Application.Features.Auth.Shared;
using FormUp.Tests.Infrastructure;

namespace FormUp.Tests.Endpoints.Auth;

/// <summary>
/// Proves the social-login validators run in the real pipeline: Admin is rejected with 400 before
/// the handler (and so before any token verification) is reached.
/// </summary>
[Collection(TestCollection.Shard3)]
public class SocialLoginRolesValidationWiringTests(FitnessApiFactory factory)
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task GoogleSocialLogin_AdminRole_Returns400BeforeHandler()
    {
        using var http = factory.CreateClient();

        var response = await http.PostAsJsonAsync(
            "/auth/social/google",
            new { idToken = "token", nonce = "nonce", roles = new[] { "Admin" } },
            Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(SocialSignUpRoles.InvalidRoleMessage);
    }

    [Fact]
    public async Task AppleSocialLogin_AdminRole_Returns400BeforeHandler()
    {
        using var http = factory.CreateClient();

        var response = await http.PostAsJsonAsync(
            "/auth/social/apple",
            new { identityToken = "token", nonce = "nonce", roles = new[] { "Admin" } },
            Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(SocialSignUpRoles.InvalidRoleMessage);
    }

    [Fact]
    public async Task GoogleSocialLogin_CoachWithHealthDataConsent_Returns400BeforeHandler()
    {
        using var http = factory.CreateClient();

        var response = await http.PostAsJsonAsync(
            "/auth/social/google",
            new { idToken = "token", nonce = "nonce", roles = new[] { "Trainer" }, healthDataConsent = true },
            Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(SocialSignUpRoles.CoachHealthDataConsentMessage);
    }

    [Fact]
    public async Task AppleSocialLogin_CoachWithHealthDataConsent_Returns400BeforeHandler()
    {
        using var http = factory.CreateClient();

        var response = await http.PostAsJsonAsync(
            "/auth/social/apple",
            new { identityToken = "token", nonce = "nonce", roles = new[] { "Nutritionist" }, healthDataConsent = true },
            Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(SocialSignUpRoles.CoachHealthDataConsentMessage);
    }
}
