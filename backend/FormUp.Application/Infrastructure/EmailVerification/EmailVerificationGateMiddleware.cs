using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Infrastructure.EmailVerification;

/// <summary>
/// The single global gate for accounts whose email is not confirmed: every authenticated endpoint except those on
/// <see cref="EmailVerificationExemptions"/> answers 403 <see cref="ErrorCodes.EmailNotVerified"/>. Endpoints with no
/// authorization metadata (health checks), anonymous endpoints and unauthenticated callers pass untouched; a token
/// whose user no longer exists answers 401.
/// </summary>
/// <param name="next">The next middleware.</param>
public sealed class EmailVerificationGateMiddleware(RequestDelegate next)
{
    /// <summary>Applies the gate to one request.</summary>
    /// <param name="http">The request context.</param>
    public async Task InvokeAsync(HttpContext http)
    {
        if (!IsGated(http) || http.User.Identity?.IsAuthenticated != true)
        {
            await next(http);
            return;
        }

        if (!Guid.TryParse(http.User.FindFirstValue(AppClaims.UserId), out var userId))
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var emailConfirmed = await http.RequestServices.GetRequiredService<IApplicationDbContext>().Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => (bool?)user.EmailConfirmed)
            .FirstOrDefaultAsync(http.RequestAborted);

        if (emailConfirmed is null)
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (emailConfirmed == false)
        {
            await http.RespondEmailNotVerifiedAsync(http.RequestAborted);
            return;
        }

        await next(http);
    }

    private static bool IsGated(HttpContext http)
    {
        var endpoint = http.GetEndpoint();

        if (endpoint is null
            || endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null
            || endpoint.Metadata.GetMetadata<IAuthorizeData>() is null)
        {
            return false;
        }

        var endpointType = endpoint.Metadata.GetMetadata<EndpointDefinition>()?.EndpointType;

        return endpointType is null || !EmailVerificationExemptions.Endpoints.Contains(endpointType);
    }
}
