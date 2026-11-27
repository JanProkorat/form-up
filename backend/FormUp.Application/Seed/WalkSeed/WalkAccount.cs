namespace FormUp.Application.Seed.WalkSeed;

/// <summary>
/// One walk account: the stable key the walk driver uses, its email, and a plain-English description of its state.
/// </summary>
/// <param name="Key">Short key; also the session file name.</param>
/// <param name="Kind">The billing state to prepare.</param>
/// <param name="Email">Sign-in email.</param>
/// <param name="Note">The state in plain English.</param>
internal sealed record WalkAccount(string Key, WalkAccountKind Kind, string Email, string Note)
{
    /// <summary>True for the accounts that hold a coach profile.</summary>
    public bool IsCoach => Kind != WalkAccountKind.Admin;

    /// <summary>True for the accounts that get forms, a check-in rule, linked clients and check-in history.</summary>
    public bool HasCheckInData => Kind is WalkAccountKind.CheckIns or WalkAccountKind.CheckInsOnly;

    /// <summary>True for the accounts whose plan is given by Form Up, with no Stripe objects.</summary>
    public bool IsGivenByFormUp => Kind is WalkAccountKind.Given or WalkAccountKind.CheckIns or WalkAccountKind.CheckInsOnly;

    /// <summary>True for the accounts backed by a Stripe sandbox subscription.</summary>
    public bool UsesStripe => Kind is WalkAccountKind.Pro or WalkAccountKind.ProThreeDSecure or WalkAccountKind.ManyClients;
}
