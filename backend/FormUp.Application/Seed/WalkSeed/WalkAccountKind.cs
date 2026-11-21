namespace FormUp.Application.Seed.WalkSeed;

/// <summary>
/// The billing state a walk account is put into.
/// </summary>
internal enum WalkAccountKind
{
    /// <summary>Admin role only, no coach profile.</summary>
    Admin,

    /// <summary>One-role coach on the Free plan.</summary>
    Free,

    /// <summary>Coach paying Pro through a Stripe sandbox subscription, card without 3D Secure.</summary>
    Pro,

    /// <summary>Coach paying Pro, but the card on file requires 3D Secure on every later payment.</summary>
    ProThreeDSecure,

    /// <summary>Coach on a Pro plan given by Form Up, no Stripe objects.</summary>
    Given,

    /// <summary>Paying coach with more linked clients than the Free plan allows.</summary>
    ManyClients,

    /// <summary>Coach on a Pro plan given by Form Up, with forms, a check-in rule, clients and check-in history.</summary>
    CheckIns,

    /// <summary>Coach on a seed-owned plan with weekly check-ins but no forms and no per-client check-in settings.</summary>
    CheckInsOnly,
}
