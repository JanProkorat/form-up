namespace FormUp.Application.Domain.Enums;

/// <summary>
/// Why a coach account could not be disabled because of the payment provider.
/// </summary>
public enum CoachAccountDisableFailure
{
    /// <summary>The coach has a live provider subscription and billing is not configured on this server.</summary>
    NotConfigured,

    /// <summary>The provider refused or failed the cancel-at-period-end call.</summary>
    ProviderError,
}
