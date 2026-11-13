using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Domain.Entities;

/// <summary>
/// The per-domain capabilities a single <see cref="ClientProfessionalLink"/> grants, lifted out of
/// the entity so a caller can pass "what may this professional see about this client" around
/// without carrying the whole tracked row.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the client-addressed professional routes each answered the question
/// differently: some checked <c>ConnectionState</c> alone, one checked an either-flag helper and then
/// returned single-domain data, and one derived its domain scope from the caller's <b>global
/// roles</b> via <c>User.IsInRole</c> rather than from the link. Global role state must never
/// widen a per-link capability — a dual-role professional whose link was deliberately narrowed to
/// one domain still satisfies <c>IsInRole</c> for both, which is exactly the escalation the
/// stamped-flag model exists to prevent.
/// </para>
/// <para>
/// Capability flags are global-role-derived <i>at stamping time</i> and that is deliberate
/// (#776) — but once stamped, the link is the only authority. Read the flags, never the roles.
/// </para>
/// </remarks>
/// <param name="CanViewNutritionPlans">Whether the link grants the nutrition domain.</param>
/// <param name="CanViewTrainingPlans">Whether the link grants the training domain.</param>
public readonly record struct LinkCapabilities(bool CanViewNutritionPlans, bool CanViewTrainingPlans)
{
    /// <summary>
    /// True when the link grants neither domain. Routes returning per-client plan data deny
    /// outright in this case rather than returning an empty body — this is the deny the
    /// dashboard, timeline and plan-list routes already carry, per the #903 fix.
    /// </summary>
    public bool GrantsNothing => !CanViewNutritionPlans && !CanViewTrainingPlans;

    /// <summary>
    /// The compliance discipline this link may see. Compliance figures blend both domains, so a
    /// single-flag caller must receive their own domain's figure — the combined one leaks the
    /// other domain's adherence by inference.
    /// </summary>
    public ComplianceDiscipline Discipline => (CanViewNutritionPlans, CanViewTrainingPlans) switch
    {
        (true, true) => ComplianceDiscipline.Both,
        (true, false) => ComplianceDiscipline.NutritionOnly,
        (false, true) => ComplianceDiscipline.TrainingOnly,
        // Unreachable for a link that passed the GrantsNothing deny; Both is the conservative
        // read only in the sense that it is what the pre-#903 code did — callers must deny first.
        _ => ComplianceDiscipline.Both,
    };

    /// <summary>
    /// Projects the capabilities carried by a link row.
    /// </summary>
    /// <param name="link">The link to read.</param>
    public static LinkCapabilities FromLink(ClientProfessionalLink link) =>
        new(link.CanViewNutritionPlans, link.CanViewTrainingPlans);
}
