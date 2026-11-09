using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Services;
using Microsoft.AspNetCore.Authorization;

namespace FormUp.Application.Infrastructure.PlanChoice;

/// <summary>
/// The single global write gate for a coach whose admin-given plan ended and who has not chosen a plan: it answers
/// 403 <see cref="ErrorCodes.PlanChoiceRequired"/> and the request goes no further. Decision order: a caller holding
/// no coach role passes with no database work; reads pass; endpoints on <see cref="PlanChoiceExemptions"/> pass;
/// an endpoint the caller reaches through a non-coach role passes; otherwise one
/// <see cref="EntitlementService"/> lookup decides.
/// </summary>
/// <remarks>
/// Middleware rather than a FastEndpoints global pre-processor: FastEndpoints 8.0.1 runs pre-processors after binding
/// and before validation, and a response sent from one skips the handler but not validation. A failed validation then
/// still throws, and on a DontCatchExceptions endpoint (the MealTemplates and SessionTemplates slices) the throw
/// aborts the already-written 403. Pinned by <c>BlockedWrite_WithAnInvalidBody_AnswersOneCleanProblemBody</c>.
/// </remarks>
/// <param name="next">The next middleware.</param>
public sealed class PlanChoiceWriteGateMiddleware(RequestDelegate next)
{
    private static readonly string[] ReadMethods = ["GET", "HEAD", "OPTIONS"];

    private static readonly string[] CoachRoles = [AppRoles.Trainer, AppRoles.Nutritionist];

    /// <summary>Applies the gate to one request.</summary>
    /// <param name="http">The request context.</param>
    public async Task InvokeAsync(HttpContext http)
    {
        if (CoachRoles.Any(http.User.IsInRole)
            && !ReadMethods.Contains(http.Request.Method, StringComparer.OrdinalIgnoreCase)
            && await IsRefusedAsync(http))
        {
            await http.RespondPlanChoiceRequiredAsync(http.RequestAborted);
            return;
        }

        await next(http);
    }

    private static async Task<bool> IsRefusedAsync(HttpContext http)
    {
        var endpoint = http.GetEndpoint();
        var endpointType = endpoint?.Metadata.GetMetadata<EndpointDefinition>()?.EndpointType;

        if (endpoint is null || endpointType is null || PlanChoiceExemptions.Endpoints.Contains(endpointType))
        {
            return false;
        }

        var allowedRoles = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .SelectMany(data => (data.Roles ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct()
            .ToList();

        if (!ActsAsCoach(http.User, allowedRoles)
            || !Guid.TryParse(http.User.FindFirstValue(AppClaims.UserId), out var userId))
        {
            return false;
        }

        return await http.RequestServices.GetRequiredService<EntitlementService>()
            .IsPlanChoiceRequiredForUserAsync(userId, http.RequestAborted);
    }

    /// <summary>
    /// Whether the caller reaches the endpoint as a coach. A login-only endpoint is a coach call for a coach. An endpoint
    /// that also allows Client is read as a client call when the caller holds Client, so a user who is both a coach and a
    /// client keeps the client side; Admin on an endpoint that allows Admin is not a coach call either.
    /// </summary>
    private static bool ActsAsCoach(ClaimsPrincipal user, IReadOnlyCollection<string> allowedRoles)
    {
        if (allowedRoles.Count == 0)
        {
            return true;
        }

        if (allowedRoles.Contains(AppRoles.Admin) && user.IsInRole(AppRoles.Admin))
        {
            return false;
        }

        if (!CoachRoles.Any(role => allowedRoles.Contains(role) && user.IsInRole(role)))
        {
            return false;
        }

        return !(allowedRoles.Contains(AppRoles.Client) && user.IsInRole(AppRoles.Client));
    }
}
