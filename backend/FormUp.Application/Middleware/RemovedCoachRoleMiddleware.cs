using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
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
/// Admin on an endpoint that allows Admin passes. An unannotated write fails closed as a refusal.
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

        if (methods.All(method => ReadMethods.Contains(method, StringComparer.OrdinalIgnoreCase)))
        {
            return GateRule.Open;
        }

        var roles = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .SelectMany(data => (data.Roles ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct()
            .ToList();

        var attribute = endpoint.Metadata.GetMetadata<EndpointDefinition>()?.EndpointType
            .GetCustomAttribute<RemovedCoachRoleAttribute>();

        if (roles.Count == 1 && roles[0] is AppRoles.Trainer or AppRoles.Nutritionist)
        {
            return new GateRule(roles[0], attribute?.Mode ?? RemovedCoachRoleMode.Refuse, false);
        }

        var dualCoach = roles.Contains(AppRoles.Trainer) && roles.Contains(AppRoles.Nutritionist) && !roles.Contains(AppRoles.Client);

        return dualCoach
            ? new GateRule(null, attribute?.Mode ?? RemovedCoachRoleMode.Refuse, roles.Contains(AppRoles.Admin))
            : GateRule.Open;
    }

    private sealed record GateRule(string? Role, RemovedCoachRoleMode? Mode, bool AllowsAdmin)
    {
        public static GateRule Open { get; } = new(null, null, false);
    }
}
