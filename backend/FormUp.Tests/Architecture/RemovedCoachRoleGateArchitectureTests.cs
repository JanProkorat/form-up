using System.Reflection;
using FastEndpoints;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Architecture;

/// <summary>
/// Pins the read-only gate's coverage: every non-read endpoint gated to exactly one coach discipline
/// declares <see cref="RemovedCoachRoleAttribute"/> with its own role, every non-read endpoint gated to both
/// coach disciplines (and not Client) declares the role-less form, and nothing else carries either.
/// </summary>
[Collection(TestCollection.Name)]
public class RemovedCoachRoleGateArchitectureTests(FitnessApiFactory factory)
{
    private static readonly string[] ReadMethods = ["GET", "HEAD", "OPTIONS"];

    private sealed record GatedEndpoint(string Route, string Role, Type Type, RemovedCoachRoleAttribute? Attribute);

    private List<(RouteEndpoint Endpoint, Type Type, string[] Methods, string[] Roles)> Endpoints()
    {
        return factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText != "_test_url_cache_")
            .Select(e => (
                Endpoint: e,
                Type: e.Metadata.GetMetadata<EndpointDefinition>()?.EndpointType,
                Methods: (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? []).ToArray(),
                Roles: e.Metadata.GetOrderedMetadata<IAuthorizeData>()
                    .SelectMany(a => (a.Roles ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    .Distinct()
                    .ToArray()))
            .Where(e => e.Type is not null && e.Methods.Length > 0)
            .Select(e => (e.Endpoint, e.Type!, e.Methods, e.Roles))
            .ToList();
    }

    private static bool IsSingleDisciplineWrite(string[] methods, string[] roles) =>
        methods.Any(m => !ReadMethods.Contains(m, StringComparer.OrdinalIgnoreCase))
        && roles.Length == 1
        && roles[0] is AppRoles.Trainer or AppRoles.Nutritionist;

    private static bool IsDualDisciplineWrite(string[] methods, string[] roles) =>
        methods.Any(m => !ReadMethods.Contains(m, StringComparer.OrdinalIgnoreCase))
        && roles.Contains(AppRoles.Trainer)
        && roles.Contains(AppRoles.Nutritionist)
        && !roles.Contains(AppRoles.Client);

    [Fact]
    public void EverySingleDisciplineWrite_DeclaresTheAttribute_WithItsOwnRole()
    {
        var violations = new List<string>();

        foreach (var (endpoint, type, methods, roles) in Endpoints().Where(e => IsSingleDisciplineWrite(e.Methods, e.Roles)))
        {
            var attribute = type.GetCustomAttribute<RemovedCoachRoleAttribute>();
            var label = $"{string.Join('/', methods)} {endpoint.RoutePattern.RawText} ({type.Name})";

            if (attribute is null)
            {
                violations.Add($"{label} has no [RemovedCoachRole]");
            }
            else if (attribute.Role != roles[0])
            {
                violations.Add($"{label} declares {attribute.Role} but is gated to {roles[0]}");
            }
            else if (attribute.Mode == RemovedCoachRoleMode.Exempt && string.IsNullOrWhiteSpace(attribute.Reason))
            {
                violations.Add($"{label} is Exempt without a Reason");
            }
        }

        violations.Should().BeEmpty("an unannotated single-discipline write would be refused by the gate without anyone deciding it");
    }

    [Fact]
    public void EveryDualDisciplineWrite_DeclaresTheRolelessAttribute()
    {
        var violations = new List<string>();

        foreach (var (endpoint, type, methods, roles) in Endpoints().Where(e => IsDualDisciplineWrite(e.Methods, e.Roles)))
        {
            var attribute = type.GetCustomAttribute<RemovedCoachRoleAttribute>();
            var label = $"{string.Join('/', methods)} {endpoint.RoutePattern.RawText} ({type.Name})";

            if (attribute is null)
            {
                violations.Add($"{label} has no [RemovedCoachRole]");
            }
            else if (attribute.Role is not null)
            {
                violations.Add($"{label} declares role {attribute.Role} but is gated to both coach roles");
            }
            else if (attribute.Mode == RemovedCoachRoleMode.Exempt && string.IsNullOrWhiteSpace(attribute.Reason))
            {
                violations.Add($"{label} is Exempt without a Reason");
            }
        }

        violations.Should().BeEmpty("an unannotated dual-discipline write would be refused without anyone deciding it");
    }

    [Fact]
    public void TheAttribute_IsOnlyOnCoachWrites_InItsOwnForm()
    {
        var misplaced = Endpoints()
            .Where(e => e.Type.GetCustomAttribute<RemovedCoachRoleAttribute>() is not null)
            .Where(e => e.Type.GetCustomAttribute<RemovedCoachRoleAttribute>()!.Role is null
                ? !IsDualDisciplineWrite(e.Methods, e.Roles)
                : !IsSingleDisciplineWrite(e.Methods, e.Roles))
            .Select(e => $"{string.Join('/', e.Methods)} {e.Endpoint.RoutePattern.RawText} ({e.Type.Name})")
            .ToList();

        misplaced.Should().BeEmpty("reads and endpoints that allow Client are never gated, and each form fits only its own kind of endpoint");
    }

    [Fact]
    public void NoWrite_IsGatedToOneCoachRolePlusAdmin()
    {
        var uncovered = Endpoints()
            .Where(e => e.Methods.Any(m => !ReadMethods.Contains(m, StringComparer.OrdinalIgnoreCase)))
            .Where(e => e.Roles.Contains(AppRoles.Trainer) != e.Roles.Contains(AppRoles.Nutritionist))
            .Where(e => e.Roles.Contains(AppRoles.Admin) && !e.Roles.Contains(AppRoles.Client))
            .Select(e => $"{string.Join('/', e.Methods)} {e.Endpoint.RoutePattern.RawText} ({e.Type.Name})")
            .ToList();

        uncovered.Should().BeEmpty("a write gated to one coach role plus Admin matches neither attribute form, so the gate leaves it open without anyone deciding it");
    }

    [Fact]
    public void AttributeCounts_MatchTheReviewedInventory()
    {
        var counts = Endpoints()
            .Where(e => IsSingleDisciplineWrite(e.Methods, e.Roles))
            .Select(e => (Role: e.Roles[0], Mode: e.Type.GetCustomAttribute<RemovedCoachRoleAttribute>()!.Mode))
            .GroupBy(e => e)
            .ToDictionary(g => g.Key, g => g.Count());

        counts.GetValueOrDefault((AppRoles.Trainer, RemovedCoachRoleMode.Refuse)).Should().Be(22);
        counts.GetValueOrDefault((AppRoles.Trainer, RemovedCoachRoleMode.WhilePlanInProgress)).Should().Be(8);
        counts.GetValueOrDefault((AppRoles.Trainer, RemovedCoachRoleMode.Exempt)).Should().Be(0);
        counts.GetValueOrDefault((AppRoles.Nutritionist, RemovedCoachRoleMode.Refuse)).Should().Be(34);
        counts.GetValueOrDefault((AppRoles.Nutritionist, RemovedCoachRoleMode.WhilePlanInProgress)).Should().Be(5);
        counts.GetValueOrDefault((AppRoles.Nutritionist, RemovedCoachRoleMode.Exempt)).Should().Be(1);
        counts.Values.Sum().Should().Be(70);
    }

    [Fact]
    public void DualDisciplineAttributeCounts_MatchTheReviewedInventory()
    {
        var counts = Endpoints()
            .Where(e => IsDualDisciplineWrite(e.Methods, e.Roles))
            .GroupBy(e => e.Type.GetCustomAttribute<RemovedCoachRoleAttribute>()!.Mode)
            .ToDictionary(g => g.Key, g => g.Count());

        counts.GetValueOrDefault(RemovedCoachRoleMode.Refuse).Should().Be(48);
        counts.GetValueOrDefault(RemovedCoachRoleMode.Exempt).Should().Be(12);
        counts.Values.Sum().Should().Be(60);
    }
}
