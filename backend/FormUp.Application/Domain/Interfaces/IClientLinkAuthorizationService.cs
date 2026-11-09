using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Domain.Interfaces;

/// <summary>
/// Single entry point for answering "what may this professional see about this client",
/// replacing the seven near-synonymous checks call sites used to hand-roll individually.
/// </summary>
/// <remarks>
/// <para>
/// <c>null</c> and <see cref="LinkCapabilities.GrantsNothing"/> are deliberately distinct
/// return values: <c>null</c> means there is no professional profile, no client profile, or no
/// active link at all — the caller should treat the resource as not found. A non-null result
/// whose <see cref="LinkCapabilities.GrantsNothing"/> is <see langword="true"/> means an active
/// link exists but grants neither domain — the caller should deny with forbidden, not not-found.
/// Collapsing the two together reopens the escalation class this service exists to close.
/// </para>
/// </remarks>
public interface IClientLinkAuthorizationService
{
    /// <summary>
    /// Loads the capabilities the caller's active link to the client (addressed by
    /// <c>ClientProfile.PublicId</c>) grants.
    /// </summary>
    /// <param name="professionalUserId">The professional's ApplicationUser.Id from JWT.</param>
    /// <param name="clientPublicId">The client's ClientProfile.PublicId from the API request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The link's capabilities, or <see langword="null"/> when no professional profile, no client
    /// profile, or no active link exists.
    /// </returns>
    Task<LinkCapabilities?> GetCapabilitiesByClientPublicIdAsync(
        Guid professionalUserId, Guid clientPublicId, CancellationToken ct);

    /// <summary>
    /// Loads the capabilities the caller's active link to the client grants, addressed by the
    /// client's <c>ApplicationUser.Id</c> — the storage key every Mongo plan document's
    /// <c>ClientId</c> carries since #840. This is the plan-addressed counterpart of
    /// <see cref="GetCapabilitiesByClientPublicIdAsync"/>; plan routes hold only the document's
    /// <c>ClientId</c>, so keying on it directly removes the
    /// <c>ApplicationUser.Id → ClientProfile.PublicId</c> round-trip each call site would
    /// otherwise hand-roll before it could ask the question.
    /// </summary>
    /// <param name="professionalUserId">The professional's ApplicationUser.Id from JWT.</param>
    /// <param name="clientUserId">The client's ApplicationUser.Id, as stored on the plan document.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The link's capabilities, or <see langword="null"/> when no professional profile, no client
    /// profile, or no active link exists.
    /// </returns>
    Task<LinkCapabilities?> GetCapabilitiesByClientUserIdAsync(
        Guid professionalUserId, Guid clientUserId, CancellationToken ct);

    /// <summary>
    /// Returns every client the professional currently has an active link to, together with the
    /// capabilities that link grants. Used by the plan LIST routes, which are not addressed by a
    /// single plan and so cannot gate per document — they scope the query itself to the caller's
    /// live link set instead.
    /// </summary>
    /// <param name="professionalUserId">The professional's ApplicationUser.Id from JWT.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="requiredScope">
    /// When <see langword="null"/> (the default), every active link is returned regardless of
    /// which capability it grants — including one that grants neither domain
    /// (<see cref="LinkCapabilities.GrantsNothing"/>). This is deliberately distinct from
    /// <see cref="LinkCapabilityScope.Both"/>: <see langword="null"/> applies no capability
    /// predicate at all (gates on <c>ConnectionState</c> only), while <see cref="LinkCapabilityScope.Both"/>
    /// requires <c>CanViewTrainingPlans AND CanViewNutritionPlans</c>. Collapsing the two would
    /// silently drop every <see cref="LinkCapabilities.GrantsNothing"/> row from the unfiltered
    /// query. When <see cref="LinkCapabilityScope.TrainingOnly"/>, the predicate pushes
    /// <c>CanViewTrainingPlans</c> down into the query; when
    /// <see cref="LinkCapabilityScope.NutritionOnly"/>, <c>CanViewNutritionPlans</c>; when
    /// <see cref="LinkCapabilityScope.Both"/>, both flags (AND). A caller that only ever wants one
    /// domain's client set should pass the scope rather than filtering the unfiltered list itself,
    /// so the database — not the application — drops the non-matching rows.
    /// </param>
    /// <returns>
    /// The accessible clients' <c>ApplicationUser.Id</c> paired with their link's capabilities;
    /// empty when the professional has no profile or no active links. A link whose
    /// <c>ClientProfileId</c> has no surviving <c>ClientProfile</c> is dropped by the inner join,
    /// matching the pre-existing behaviour.
    /// </returns>
    Task<IReadOnlyList<(Guid ClientUserId, LinkCapabilities Capabilities)>> GetAccessibleClientsAsync(
        Guid professionalUserId, CancellationToken ct, LinkCapabilityScope? requiredScope = null);
}
