using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.EmailVerification;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Architecture;

/// <summary>
/// Pins the email-verification gate (#1517): the exemption list is the reviewed set, and every other authenticated
/// route answers 403 EMAIL_NOT_VERIFIED to an unverified caller. "Gated" means the route carries IAuthorizeData, is not
/// anonymous and is not exempt, the same rule the middleware applies.
/// </summary>
[Collection(TestCollection.Name)]
public partial class EmailVerificationGateArchitectureTests(FitnessApiFactory factory)
{
    private static readonly string[] ExpectedExemptions =
    [
        "ChangeUnverifiedEmailEndpoint",
        "GetProfileEndpoint",
        "LogoutEndpoint",
        "ResendVerificationEndpoint",
    ];

    private static readonly UserRole[] AllRoles = [UserRole.Client, UserRole.Trainer, UserRole.Nutritionist, UserRole.Admin];

    [GeneratedRegex(@"\{\*{0,2}[^}]+\}")]
    private static partial Regex RouteParameter();

    [Fact]
    public void ExemptionList_IsTheReviewedSet()
    {
        EmailVerificationExemptions.Endpoints.Select(type => type.Name).Order(StringComparer.Ordinal)
            .Should().Equal(ExpectedExemptions);
    }

    [Fact]
    public void EveryExemptType_IsARegisteredEndpoint()
    {
        var registered = AppRoutes().Select(route => route.EndpointType).Where(type => type is not null).ToHashSet();

        EmailVerificationExemptions.Endpoints.Where(type => !registered.Contains(type))
            .Should().BeEmpty("a stale entry would exempt nothing and hide a renamed endpoint");
    }

    [Fact]
    public void AcceptInvitation_IsNotExempt()
    {
        EmailVerificationExemptions.Endpoints.Select(type => type.Name).Should().NotContain("AcceptInvitationEndpoint");
    }

    [Fact]
    public async Task UnverifiedActorWithEveryRole_IsRefusedOnEveryGatedRoute()
    {
        var actor = await UnverifiedActorAsync();
        var gated = GatedRoutes();
        gated.Should().NotBeEmpty();

        var failures = new List<string>();

        foreach (var (method, route) in gated)
        {
            using var response = await SendAsync(actor.Http, method, route);
            var errorCode = await ErrorCodeAsync(response);

            if (response.StatusCode != HttpStatusCode.Forbidden || errorCode != ErrorCodes.EmailNotVerified)
            {
                var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
                failures.Add($"{method} {route} -> {(int)response.StatusCode} errorCode={errorCode} body={body}");
            }
        }

        failures.Should().BeEmpty("every authenticated, non-exempt route must answer 403 EMAIL_NOT_VERIFIED");
    }

    [Fact]
    public async Task UnverifiedActor_OnExemptRoutes_IsNotRefusedByTheGate()
    {
        var actor = await UnverifiedActorAsync();
        var exempt = AppRoutes()
            .Where(route => route.EndpointType is not null && EmailVerificationExemptions.Endpoints.Contains(route.EndpointType))
            .SelectMany(route => route.Methods.Select(method => (Method: method, route.Route)))
            .ToList();
        exempt.Should().HaveCount(EmailVerificationExemptions.Endpoints.Count);

        foreach (var (method, route) in exempt)
        {
            using var response = await SendAsync(actor.Http, method, route);

            (await ErrorCodeAsync(response)).Should().NotBe(ErrorCodes.EmailNotVerified, $"{method} {route} is exempt");
        }
    }

    [Fact]
    public async Task UnverifiedToken_OnAnAnonymousRoute_Passes()
    {
        var actor = await UnverifiedActorAsync();

        using var response = await actor.Http.GetAsync("/subscription-plans", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UnverifiedToken_OnHealthLive_IsNotRefused()
    {
        var actor = await UnverifiedActorAsync();

        using var response = await actor.Http.GetAsync("/health/live", TestContext.Current.CancellationToken);

        (await ErrorCodeAsync(response)).Should().NotBe(ErrorCodes.EmailNotVerified);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UnverifiedToken_OnHubNegotiate_IsRefused()
    {
        var actor = await UnverifiedActorAsync();

        using var response = await actor.Http.PostAsync(
            "/hubs/notifications/negotiate?negotiateVersion=1", null, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(response)).Should().Be(
            ErrorCodes.EmailNotVerified,
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken) + response.Content.Headers.ContentType);
    }

    [Fact]
    public async Task ValidToken_ForADeletedUser_Answers401()
    {
        var ghost = factory.CreateClient();
        TestHelpers.SetBearerToken(ghost, TestTokenFactory.CreateAccessToken(
            factory, Guid.NewGuid(), "ghost@actor-fixture.com", AllRoles.Select(role => role.ToString())));
        var (method, route) = GatedRoutes().First(entry => entry.Method == "GET" && !entry.Route.Contains('{'));

        using var response = await SendAsync(ghost, method, route);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private Task<Actor> UnverifiedActorAsync() =>
        TestActors.Professional(factory, AllRoles).Unverified().CreateAsync(TestContext.Current.CancellationToken);

    private List<(string Method, string Route)> GatedRoutes() =>
        AppRoutes()
            .Where(route => route.RequiresLogin && !route.IsAnonymous
                && (route.EndpointType is null || !EmailVerificationExemptions.Endpoints.Contains(route.EndpointType)))
            .SelectMany(route => route.Methods.Select(method => (Method: method, route.Route)))
            .ToList();

    private List<RouteInfo> AppRoutes() =>
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<IHttpMethodMetadata>() is not null)
            .Where(endpoint => endpoint.RoutePattern.RawText != "_test_url_cache_")
            .Select(endpoint => new RouteInfo(
                RouteParameter().Replace(endpoint.RoutePattern.RawText ?? string.Empty, Guid.Empty.ToString()),
                endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.ToList(),
                endpoint.Metadata.GetMetadata<EndpointDefinition>()?.EndpointType,
                endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null,
                endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Any()))
            .ToList();

    private static async Task<HttpResponseMessage> SendAsync(HttpClient http, string method, string route)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), route);

        if (method is "POST" or "PUT" or "PATCH")
        {
            request.Content = JsonContent.Create(new { });
        }

        return await http.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        if (response.Content.Headers.ContentType?.MediaType != "application/problem+json")
        {
            return null;
        }

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        return body.RootElement.TryGetProperty("errorCode", out var code) ? code.GetString() : null;
    }

    private sealed record RouteInfo(
        string Route, List<string> Methods, Type? EndpointType, bool IsAnonymous, bool RequiresLogin);
}
