using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Features.Trainers.GetClients;

/// <summary>
/// Response model for the trainer's client list.
/// </summary>
public class GetClientsResponse
{
    /// <summary>
    /// List of client summaries for the requested page.
    /// </summary>
    public List<ClientSummary> Clients { get; set; } = [];

    /// <summary>
    /// Total number of clients matching the current tab, search, tags AND filter chip.
    /// </summary>
    public int TotalCount { get; set; }

    /// <summary>
    /// Current page number.
    /// </summary>
    public int Page { get; set; }

    /// <summary>
    /// Number of items per page.
    /// </summary>
    public int PageSize { get; set; }

    /// <summary>
    /// Counts of clients per status tab, computed with <c>search</c> and <c>tagIds</c> applied
    /// (so the counts track what the caller is currently narrowing down), but never with the
    /// tab or the filter chip itself applied — that would make comparing tabs meaningless.
    /// <see cref="ClientTabCounts.Pending"/> is not narrowed by search or tags: those describe
    /// existing linked clients, and pending rows are not clients yet.
    /// </summary>
    public ClientTabCounts TabCounts { get; set; } = new();

    /// <summary>
    /// Counts of clients per filter chip, computed over the current tab with <c>search</c> and
    /// <c>tagIds</c> applied but the chip filter itself NOT applied — this is what lets the web
    /// client grey out a chip whose count is zero without losing the ability to select it.
    /// </summary>
    public ClientFilterCounts FilterCounts { get; set; } = new();
}

/// <summary>
/// Per-tab client counts. See <see cref="GetClientsResponse.TabCounts"/> for scoping rules.
/// </summary>
public class ClientTabCounts
{
    /// <summary>Number of clients in the Active tab.</summary>
    public int Active { get; set; }

    /// <summary>Number of clients in the Paused tab.</summary>
    public int Paused { get; set; }

    /// <summary>Number of clients in the Archived tab.</summary>
    public int Archived { get; set; }

    /// <summary>
    /// Number of live clients outside the plan in force (over its client cap), across Active and
    /// Paused, with search and tags applied. Archived rows are never counted.
    /// </summary>
    public int OutsidePlan { get; set; }

    /// <summary>
    /// Number of pending rows (unaccepted invites plus incoming pending requests) for the
    /// caller. Unfiltered by search or tags — see <see cref="GetClientsResponse.TabCounts"/>.
    /// </summary>
    public int Pending { get; set; }
}

/// <summary>
/// Per-chip client counts over the current tab. See
/// <see cref="GetClientsResponse.FilterCounts"/> for scoping rules.
/// </summary>
public class ClientFilterCounts
{
    /// <summary>Total rows in the current tab (search and tags applied).</summary>
    public int All { get; set; }

    /// <summary>Rows with at least one unread message from the client.</summary>
    public int UnreadMessages { get; set; }

    /// <summary>Rows with no conversation, or a conversation with zero messages.</summary>
    public int NoMessages { get; set; }

    /// <summary>Rows with a responded, not-yet-reviewed weekly check-in.</summary>
    public int NewCheckIns { get; set; }

    /// <summary>Rows with an expired or overdue-unanswered weekly check-in.</summary>
    public int MissingCheckIns { get; set; }

    /// <summary>Rows whose active plan window ends within 14 days.</summary>
    public int EndingSoon { get; set; }
}

/// <summary>
/// Summary of a client in the trainer's client list.
/// </summary>
public class ClientSummary
{
    /// <summary>
    /// Client profile's public ID.
    /// </summary>
    public Guid PublicId { get; set; }

    /// <summary>
    /// The client's <c>ApplicationUser.Id</c> — the join key shared with Mongo plan documents
    /// (<c>NutritionPlan.ClientId</c> / <c>TrainingPlan.ClientId</c>, #840) and with messaging
    /// (<c>Conversation.ClientUserId</c>). Distinct from <see cref="PublicId"/>, which is
    /// <c>ClientProfile.PublicId</c> and is not a valid join key against either.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Client's email address.
    /// </summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// Client's first name.
    /// </summary>
    public string FirstName { get; set; } = string.Empty;

    /// <summary>
    /// Client's last name.
    /// </summary>
    public string LastName { get; set; } = string.Empty;

    /// <summary>
    /// Whether this trainer-client relationship is active.
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// Date when the trainer-client relationship was established.
    /// </summary>
    public DateTime LinkedAt { get; set; }

    /// <summary>
    /// Derived tab status — see <see cref="ClientListStatus"/>.
    /// </summary>
    public ClientListStatus Status { get; set; }

    /// <summary>
    /// The client's avatar, if uploaded. Read straight off <c>ApplicationUser.AvatarBlobUrl</c> —
    /// same as <c>GetDashboardSummaryEndpoint</c>, no presigning at this layer.
    /// </summary>
    public string? AvatarBlobUrl { get; set; }

    /// <summary>
    /// The caller's own tags assigned to this client, via the caller's own link.
    /// </summary>
    public List<ClientTagSummaryDto> Tags { get; set; } = [];

    /// <summary>
    /// Number of unread messages sent BY the client to the caller.
    /// </summary>
    public int UnreadMessageCount { get; set; }

    /// <summary>
    /// Whether the client has an in-window Active nutrition plan. Always <c>false</c> when the
    /// link does not grant <c>CanViewNutritionPlans</c> — never computed for a domain the caller
    /// cannot see.
    /// </summary>
    public bool HasActiveNutritionPlan { get; set; }

    /// <summary>
    /// Whether the client has an in-window Active training plan. Always <c>false</c> when the
    /// link does not grant <c>CanViewTrainingPlans</c>.
    /// </summary>
    public bool HasActiveTrainingPlan { get; set; }

    /// <summary>
    /// The client's currently in-window Active plans, one per visible domain, for the row's
    /// hover popover. Omits any domain the link does not grant.
    /// </summary>
    public List<ClientActivePlanDto> ActivePlans { get; set; } = [];

    /// <summary>
    /// True when this live client is outside the plan in force (over its client cap): the coach can
    /// still see them but not work on them. Never true for an Archived row.
    /// </summary>
    public bool IsOutsidePlan { get; set; }
}

/// <summary>
/// A coach-owned tag assigned to a client, as surfaced on the clients list. Declared locally to
/// this slice rather than reusing <c>Features/ClientTags/Shared/ClientTagDto</c> — the list needs
/// only the display fields, and importing a sibling feature's DTO would cross a feature boundary.
/// </summary>
public class ClientTagSummaryDto
{
    /// <summary>Public identifier of the tag.</summary>
    public Guid TagId { get; set; }

    /// <summary>Tag label.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Display color as a lowercase 6-digit hex string.</summary>
    public string ColorHex { get; set; } = string.Empty;
}

/// <summary>
/// A client's currently in-window Active plan, for the clients list hover popover.
/// </summary>
public class ClientActivePlanDto
{
    /// <summary>Plan display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Which domain this plan belongs to.</summary>
    public Profession Type { get; set; }

    /// <summary>The plan's start date.</summary>
    public DateTime? StartDate { get; set; }
}
