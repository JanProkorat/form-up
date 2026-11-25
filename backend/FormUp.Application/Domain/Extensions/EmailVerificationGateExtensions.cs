using FormUp.Application.Domain.Constants;

namespace FormUp.Application.Domain.Extensions;

/// <summary>Responses written by the global email-verification gate.</summary>
public static class EmailVerificationGateExtensions
{
    /// <summary>
    /// Answers 403 <see cref="ErrorCodes.EmailNotVerified"/> as the same coded ProblemDetails shape
    /// <see cref="EndpointErrorExtensions.SendProblemAsync"/> writes, for a caller with no endpoint instance
    /// (the global gate). Does not throw.
    /// </summary>
    /// <param name="http">The request context.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task RespondEmailNotVerifiedAsync(this HttpContext http, CancellationToken ct)
    {
        var problem = new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = StatusCodes.Status403Forbidden,
            Title = "Verify your email address before using this feature.",
            Type = "https://tools.ietf.org/html/rfc7807",
            Instance = http.Request.Path,
            Extensions = { ["errorCode"] = ErrorCodes.EmailNotVerified },
        };

        http.Response.StatusCode = StatusCodes.Status403Forbidden;
        await http.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json", cancellationToken: ct);
    }
}
