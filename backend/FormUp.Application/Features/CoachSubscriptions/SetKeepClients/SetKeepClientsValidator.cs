using FastEndpoints;
using FluentValidation;

namespace FormUp.Application.Features.CoachSubscriptions.SetKeepClients;

/// <summary>
/// Validates the <see cref="SetKeepClientsRequest"/>.
/// </summary>
internal sealed class SetKeepClientsValidator : Validator<SetKeepClientsRequest>
{
    /// <summary>
    /// Initializes validation rules for saving the kept clients.
    /// </summary>
    public SetKeepClientsValidator()
    {
        RuleFor(x => x.ClientIds).NotNull();

        RuleForEach(x => x.ClientIds).NotEmpty();
    }
}
