using FastEndpoints;
using FluentValidation;

namespace FormUp.Application.Features.Auth.ResolveInviteToken;

/// <summary>
/// Validates the <see cref="ResolveInviteTokenRequest"/>.
/// </summary>
public class ResolveInviteTokenValidator : Validator<ResolveInviteTokenRequest>
{
    /// <summary>
    /// Initializes validation rules for resolving an invitation token.
    /// </summary>
    public ResolveInviteTokenValidator()
    {
        RuleFor(x => x.Token)
            .NotEmpty()
            .MaximumLength(100);
    }
}
