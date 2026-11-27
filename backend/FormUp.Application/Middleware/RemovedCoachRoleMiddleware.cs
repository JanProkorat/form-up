using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FluentValidation.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using MongoDB.Driver;

namespace FormUp.Application.Middleware;

/// <summary>
/// Single global gate that makes a removed coach role read-only. Reads and non-coach endpoints pass with no
/// database work. A single-discipline write is refused once that role is removed; a dual-discipline write
/// (Trainer and Nutritionist, no Client) is refused only when the caller has no active coach role left, and an
/// Admin on an endpoint that allows Admin passes. An unannotated write fails closed as a refusal. It also answers
/// 403 for the Admin-membership rule (see <c>IsRefusedByAdminRule</c>), reads included.
/// Runs as middleware rather than a FastEndpoints pre-processor because FastEndpoints still validates the
/// request after a pre-processor answers, and endpoints that rethrow (DontCatchExceptions) then abort the response.
/// </summary>
/// <param name="next">The next middleware.</param>
public sealed class RemovedCoachRoleMiddleware(RequestDelegate next)
{
    /// <summary><see cref="HttpContext.Items"/> key set to true when the caller's role is removed but the write was let through.</summary>
    public const string RoleRemovedItemKey = "RemovedCoachRole.Removed";

    private static readonly string[] ReadMethods = ["GET", "HEAD", "OPTIONS"];

    private static readonly ConcurrentDictionary<Endpoint, GateRule> Rules = new();

    /// <summary>Applies the gate to one request.</summary>
    /// <param name="http">The request context.</param>
    public async Task InvokeAsync(HttpContext http)
    {
        var endpoint = http.GetEndpoint();
        var rule = endpoint is null ? GateRule.Open : Rules.GetOrAdd(endpoint, Resolve);

        if (IsRefusedByAdminRule(http.User, rule))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (rule.Mode is null or RemovedCoachRoleMode.Exempt)
        {
            await next(http);
            return;
        }

        var anyCoach = rule.Role is null;

        if (anyCoach ? rule.AllowsAdmin && http.User.IsInRole(AppRoles.Admin) : !http.User.IsInRole(rule.Role!))
        {
            await next(http);
            return;
        }

        if (!Guid.TryParse(http.User.FindFirstValue(AppClaims.UserId), out var userId))
        {
            await RefuseAsync(http);
            return;
        }

        var db = http.RequestServices.GetRequiredService<IApplicationDbContext>();
        var removal = await db.ProfessionalProfiles
            .AsNoTracking()
            .Where(profile => profile.UserId == userId)
            .Select(profile => new { profile.TrainerRoleRemovedAt, profile.NutritionistRoleRemovedAt })
            .FirstOrDefaultAsync(http.RequestAborted);

        if (anyCoach)
        {
            var heldRoles = new[] { AppRoles.Trainer, AppRoles.Nutritionist }.Where(http.User.IsInRole);
            var profile = removal is null
                ? null
                : new ProfessionalProfile
                {
                    UserId = userId,
                    TrainerRoleRemovedAt = removal.TrainerRoleRemovedAt,
                    NutritionistRoleRemovedAt = removal.NutritionistRoleRemovedAt,
                };

            if (CoachRoleStatus.ActiveRoles(heldRoles, profile).Count > 0)
            {
                await next(http);
                return;
            }

            await RefuseAsync(http);
            return;
        }

        var removed = rule.Role == AppRoles.Trainer
            ? removal?.TrainerRoleRemovedAt is not null
            : removal?.NutritionistRoleRemovedAt is not null;

        if (!removed)
        {
            await next(http);
            return;
        }

        if (rule.Mode == RemovedCoachRoleMode.WhilePlanInProgress &&
            await IsPlanInProgressAsync(http, rule.Role!, userId))
        {
            http.Items[RoleRemovedItemKey] = true;
            await next(http);
            return;
        }

        await RefuseAsync(http);
    }

    private static async Task<bool> IsPlanInProgressAsync(HttpContext http, string role, Guid userId)
    {
        if (!Guid.TryParse(http.GetRouteValue("PlanId")?.ToString(), out var planId))
        {
            return false;
        }

        var mongo = http.RequestServices.GetRequiredService<IMongoContext>();
        var today = DateOnly.FromDateTime(http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime);

        if (role == AppRoles.Trainer)
        {
            var plan = await mongo.TrainingPlans
                .Find(p => p.ExternalId == planId && p.TrainerId == userId)
                .Project(p => new { p.Status, p.StartDate, WeekCount = p.Weeks.Count })
                .FirstOrDefaultAsync(http.RequestAborted);

            return plan is not null &&
                   plan.Status == TrainingPlanStatus.Active &&
                   PlanWindowResolver.HasNotEnded(plan.StartDate, plan.WeekCount, today);
        }

        var nutritionPlan = await mongo.NutritionPlans
            .Find(p => p.ExternalId == planId && p.NutritionistId == userId)
            .Project(p => new { p.Status, p.StartDate, WeekCount = p.Weeks.Count })
            .FirstOrDefaultAsync(http.RequestAborted);

        return nutritionPlan is not null &&
               nutritionPlan.Status == NutritionPlanStatus.Active &&
               PlanWindowResolver.HasNotEnded(nutritionPlan.StartDate, nutritionPlan.WeekCount, today);
    }

    /// <summary>
    /// The Admin-membership rule, for reads and writes alike. An endpoint that allows both coach roles plus Admin is
    /// coach-only for an admin-only caller. An endpoint that allows one coach role plus Admin is open to an admin-only
    /// caller, and to a caller holding that role; any other caller (such as Admin plus the other coach role) is
    /// refused exactly as when the endpoint allowed the coach role alone.
    /// </summary>
    private static bool IsRefusedByAdminRule(ClaimsPrincipal user, GateRule rule)
    {
        if (rule.CoachOnlyForAdminOnly)
        {
            return user.IsAdminOnly();
        }

        return rule.RequiredCoachRole is { } required && !user.IsAdminOnly() && !user.IsInRole(required);
    }

    private static Task RefuseAsync(HttpContext http) =>
        http.Response.SendErrorsAsync(
            [new ValidationFailure("", "This coach role was removed; the account is read-only for it.")
            {
                ErrorCode = ErrorCodes.CoachRoleRemoved,
            }],
            StatusCodes.Status403Forbidden,
            cancellation: http.RequestAborted);

    private static GateRule Resolve(Endpoint endpoint)
    {
        var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [];

        var roles = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .SelectMany(data => (data.Roles ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct()
            .ToList();

        var coachRoles = roles.Where(role => role is AppRoles.Trainer or AppRoles.Nutritionist).ToList();
        var hasAdmin = roles.Contains(AppRoles.Admin);
        var allowsClient = roles.Contains(AppRoles.Client);

        // Trainer + Nutritionist + Admin (the Trainers area) is coach-only for an admin-only caller.
        var coachOnlyForAdminOnly = hasAdmin && !allowsClient && coachRoles.Count == 2;

        // One coach role + Admin (the shared libraries) is open to an admin-only caller and that coach role only.
        var singleCoach = coachRoles.Count == 1 && roles.Count == (hasAdmin ? 2 : 1) ? coachRoles[0] : null;
        var requiredCoachRole = hasAdmin ? singleCoach : null;

        if (methods.All(method => ReadMethods.Contains(method, StringComparer.OrdinalIgnoreCase)))
        {
            return GateRule.Open with { CoachOnlyForAdminOnly = coachOnlyForAdminOnly, RequiredCoachRole = requiredCoachRole };
        }

        var attribute = endpoint.Metadata.GetMetadata<EndpointDefinition>()?.EndpointType
            .GetCustomAttribute<RemovedCoachRoleAttribute>();

        if (singleCoach is not null)
        {
            return new GateRule(singleCoach, attribute?.Mode ?? RemovedCoachRoleMode.Refuse, false)
            {
                RequiredCoachRole = requiredCoachRole,
            };
        }

        var dualCoach = roles.Contains(AppRoles.Trainer) && roles.Contains(AppRoles.Nutritionist) && !allowsClient;

        return dualCoach
            ? new GateRule(null, attribute?.Mode ?? RemovedCoachRoleMode.Refuse, hasAdmin)
            {
                CoachOnlyForAdminOnly = coachOnlyForAdminOnly,
            }
            : GateRule.Open;
    }

    private sealed record GateRule(string? Role, RemovedCoachRoleMode? Mode, bool AllowsAdmin)
    {
        public static GateRule Open { get; } = new(null, null, false);

        /// <summary>True when an admin-only caller is refused (both coach roles plus Admin).</summary>
        public bool CoachOnlyForAdminOnly { get; init; }

        /// <summary>The one coach role a non-admin-only caller must hold (that role plus Admin).</summary>
        public string? RequiredCoachRole { get; init; }
    }
}
