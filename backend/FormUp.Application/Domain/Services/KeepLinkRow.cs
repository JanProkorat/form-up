using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Domain.Services;

/// <summary>
/// The fields of one live client link that decide which clients a lapsed coach keeps.
/// </summary>
/// <param name="Id">The link's internal id, the last tie-break.</param>
/// <param name="ClientProfileId">The client's <c>ClientProfile.Id</c>.</param>
/// <param name="State">The link's connection state; never <see cref="ConnectionState.Ended"/>.</param>
/// <param name="ActivatedAtUtc">When the collaboration began; null on a link that predates the column.</param>
/// <param name="DateCreated">When the link row was created, the stand-in for a missing activation time.</param>
public sealed record KeepLinkRow(
    long Id,
    long ClientProfileId,
    ConnectionState State,
    DateTime? ActivatedAtUtc,
    DateTime DateCreated);
