using System.Security.Claims;
using FormUp.Application.Domain.Constants;

namespace FormUp.Application.Domain.Extensions;

/// <summary>
/// Role predicates on the authenticated caller shared by the gates and the shared libraries.
/// </summary>
public static class AdminOnlyPrincipalExtensions
{
    /// <summary>
    /// Whether the caller is an Admin who holds no coach role. A removed coach role stays on the account, so an
    /// Admin who is also a (removed) Trainer or Nutritionist is not admin-only.
    /// </summary>
    /// <param name="user">The authenticated caller.</param>
    public static bool IsAdminOnly(this ClaimsPrincipal user) =>
        user.IsInRole(AppRoles.Admin)
        && !user.IsInRole(AppRoles.Trainer)
        && !user.IsInRole(AppRoles.Nutritionist);
}
