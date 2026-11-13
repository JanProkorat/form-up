using System.Reflection;
using FastEndpoints;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Features.SubscriptionPlans.Shared;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Architecture;

/// <summary>
/// Guards the App Store rule that the client app never learns what a coach pays for a subscription: no route a
/// client, an anonymous caller or a login-only caller can reach may expose a subscription plan or its price.
/// </summary>
[Collection(TestCollection.Name)]
public class ClientResponsesExcludeSubscriptionPriceTests(FitnessApiFactory factory)
{
    private static readonly HashSet<Type> ForbiddenTypes =
    [
        typeof(SubscriptionPlan),
        typeof(SubscriptionPlanPrice),
        typeof(SubscriptionPlanDto),
        typeof(CoachSubscription),
    ];

    private static readonly HashSet<string> ForbiddenPropertyNames =
        ["PriceMinorUnits", "SecondRoleAddOnPriceMinorUnits", "ExternalPriceId"];

    [Fact]
    public void ClientAnonymousAndLoginOnlyResponses_NeverReachSubscriptionPlanOrPrice()
    {
        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<IHttpMethodMetadata>() is not null)
            .Where(e => e.RoutePattern.RawText != "_test_url_cache_")
            .Where(IsReachableByNonAdminCaller)
            .ToList();

        routes.Should().NotBeEmpty();

        var violations = new List<string>();
        var inspected = new HashSet<Type>();

        foreach (var route in routes)
        {
            foreach (var responseType in ResponseTypesOf(route))
            {
                inspected.Add(responseType);

                foreach (var finding in Walk(responseType))
                {
                    violations.Add($"{route.RoutePattern.RawText} ({route.DisplayName}): {finding}");
                }
            }
        }

        inspected.Should().Contain(
            typeof(Application.Features.Client.GetClientAppConfig.GetClientAppConfigResponse),
            "the walk must actually see response types, otherwise it passes vacuously");

        violations.Should().BeEmpty(
            "subscription prices must not reach the client app; only Admin-only routes may expose them");
    }

    private static bool IsReachableByNonAdminCaller(RouteEndpoint endpoint)
    {
        if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            return true;
        }

        var roles = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Where(a => !string.IsNullOrWhiteSpace(a.Roles))
            .SelectMany(a => a.Roles!.Split(',', StringSplitOptions.TrimEntries))
            .ToList();

        return roles.Count == 0 || roles.Contains("Client");
    }

    private static IEnumerable<Type> ResponseTypesOf(RouteEndpoint endpoint)
    {
        var types = new HashSet<Type>();

        var definitionType = endpoint.Metadata.GetMetadata<EndpointDefinition>()?.ResDtoType;

        if (definitionType is not null && definitionType != typeof(object))
        {
            types.Add(definitionType);
        }

        foreach (var produces in endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>())
        {
            if (produces.Type is not null && produces.Type != typeof(void) && produces.Type != typeof(object))
            {
                types.Add(produces.Type);
            }
        }

        return types;
    }

    private static List<string> Walk(Type root)
    {
        var findings = new List<string>();
        var visited = new HashSet<Type>();
        var pending = new Stack<(Type Type, string Path)>();
        pending.Push((root, root.Name));

        while (pending.Count > 0)
        {
            var (current, path) = pending.Pop();

            if (current.IsGenericParameter || !visited.Add(current))
            {
                continue;
            }

            if (current.HasElementType)
            {
                pending.Push((current.GetElementType()!, path));
                continue;
            }

            foreach (var argument in current.GetGenericArguments())
            {
                pending.Push((argument, path));
            }

            if (ForbiddenTypes.Contains(current))
            {
                findings.Add($"{path} reaches forbidden type {current.Name}");
                continue;
            }

            if (current.Namespace is null || !current.Namespace.StartsWith("FormUp", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var property in current.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (ForbiddenPropertyNames.Contains(property.Name))
                {
                    findings.Add($"{path}.{property.Name} is a forbidden property");
                }

                pending.Push((property.PropertyType, $"{path}.{property.Name}"));
            }
        }

        return findings;
    }
}
