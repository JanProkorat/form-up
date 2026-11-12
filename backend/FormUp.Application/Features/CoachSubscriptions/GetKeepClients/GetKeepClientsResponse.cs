using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Features.CoachSubscriptions.GetKeepClients;

/// <summary>
/// The caller's live clients after a paid plan lapsed, with which ones the Free plan keeps.
/// </summary>
public class GetKeepClientsResponse
{
    /// <summary>How many clients the Free plan keeps; null means unlimited.</summary>
    public int? Cap { get; set; }

    /// <summary>
    /// True once a choice was saved (some live link is kept or released): the kept clients can no longer be swapped,
    /// only free slots can be filled with restricted clients. A new plan change unlocks it again.
    /// </summary>
    public bool IsChoiceLocked { get; set; }

    /// <summary>Kept slots still empty while clients are over the cap; zero otherwise.</summary>
    public int FreeSlotCount { get; set; }

    /// <summary>The caller's live clients, one entry per client even when linked in both roles.</summary>
    public List<KeepClientDto> Clients { get; set; } = [];
}

/// <summary>
/// One live client of a lapsed coach.
/// </summary>
public class KeepClientDto
{
    /// <summary>Public id of the client's profile; the id the keep-clients save expects.</summary>
    public Guid ClientId { get; set; }

    /// <summary>The client's first name.</summary>
    public string FirstName { get; set; } = string.Empty;

    /// <summary>The client's last name.</summary>
    public string LastName { get; set; } = string.Empty;

    /// <summary>The coach roles the client is linked under.</summary>
    public List<UserRole> Roles { get; set; } = [];

    /// <summary>True when the Free plan keeps this client; false when the client is over the cap.</summary>
    public bool IsKept { get; set; }

    /// <summary>Last day of the last published week across the caller's plans for this client; null when none is published.</summary>
    public DateOnly? PublishedWeeksEnd { get; set; }

    /// <summary>What happens to the client from now on.</summary>
    public KeepClientAfterLapse AfterLapse { get; set; }
}

/// <summary>
/// What a lapsed coach can still do for a client.
/// </summary>
public enum KeepClientAfterLapse
{
    /// <summary>The client is kept: everything stays available.</summary>
    FullAccess,

    /// <summary>Over the cap: published weeks keep running until the last one ends, then the client is read-only.</summary>
    RunsUntilEndThenReadOnly,

    /// <summary>Over the cap with no published week left: the client is read-only already.</summary>
    ReadOnly,
}
