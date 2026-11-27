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
        && roles.Count(role => role != AppRoles.Admin) == 1
        && roles.Length <= (roles.Contains(AppRoles.Admin) ? 2 : 1)
        && roles.Single(role => role != AppRoles.Admin) is AppRoles.Trainer or AppRoles.Nutritionist;

    private static string SingleRole(string[] roles) => roles.Single(role => role != AppRoles.Admin);

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
            else if (attribute.Role != SingleRole(roles))
            {
                violations.Add($"{label} declares {attribute.Role} but is gated to {SingleRole(roles)}");
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
    public void OneCoachRolePlusAdminWrites_AreTheReviewedLibraryEndpoints_AndGatedAsSingleDiscipline()
    {
        var libraryWrites = Endpoints()
            .Where(e => e.Methods.Any(m => !ReadMethods.Contains(m, StringComparer.OrdinalIgnoreCase)))
            .Where(e => e.Roles.Contains(AppRoles.Trainer) != e.Roles.Contains(AppRoles.Nutritionist))
            .Where(e => e.Roles.Contains(AppRoles.Admin) && !e.Roles.Contains(AppRoles.Client))
            .ToList();

        libraryWrites.Should().OnlyContain(
            e => IsSingleDisciplineWrite(e.Methods, e.Roles),
            "the gate must recognise one coach role plus Admin as a single-discipline write, or it leaves it open");

        // Recipes 8 + foods 8 (Nutritionist), exercises 4 (Trainer).
        libraryWrites.Count(e => SingleRole(e.Roles) == AppRoles.Nutritionist).Should().Be(16);
        libraryWrites.Count(e => SingleRole(e.Roles) == AppRoles.Trainer).Should().Be(4);
    }

    [Fact]
    public void AttributeCounts_MatchTheReviewedInventory()
    {
        var counts = Endpoints()
            .Where(e => IsSingleDisciplineWrite(e.Methods, e.Roles))
            .Select(e => (Role: SingleRole(e.Roles), Mode: e.Type.GetCustomAttribute<RemovedCoachRoleAttribute>()!.Mode))
            .GroupBy(e => e)
            .ToDictionary(g => g.Key, g => g.Count());

        counts.GetValueOrDefault((AppRoles.Trainer, RemovedCoachRoleMode.Refuse)).Should().Be(22);
        counts.GetValueOrDefault((AppRoles.Trainer, RemovedCoachRoleMode.WhilePlanInProgress)).Should().Be(8);
        counts.GetValueOrDefault((AppRoles.Trainer, RemovedCoachRoleMode.Exempt)).Should().Be(0);
        counts.GetValueOrDefault((AppRoles.Nutritionist, RemovedCoachRoleMode.Refuse)).Should().Be(40);
        counts.GetValueOrDefault((AppRoles.Nutritionist, RemovedCoachRoleMode.WhilePlanInProgress)).Should().Be(6);
        counts.GetValueOrDefault((AppRoles.Nutritionist, RemovedCoachRoleMode.Exempt)).Should().Be(1);
        counts.Values.Sum().Should().Be(77);
    }

    [Fact]
    public void DualDisciplineAttributeCounts_MatchTheReviewedInventory()
    {
        var counts = Endpoints()
            .Where(e => IsDualDisciplineWrite(e.Methods, e.Roles))
            .GroupBy(e => e.Type.GetCustomAttribute<RemovedCoachRoleAttribute>()!.Mode)
            .ToDictionary(g => g.Key, g => g.Count());

        counts.GetValueOrDefault(RemovedCoachRoleMode.Refuse).Should().Be(56);
        counts.GetValueOrDefault(RemovedCoachRoleMode.Exempt).Should().Be(12);
        counts.Values.Sum().Should().Be(68);
    }
}
