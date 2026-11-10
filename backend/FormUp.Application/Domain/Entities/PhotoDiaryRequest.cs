using System.ComponentModel.DataAnnotations;
using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Domain.Entities;

/// <summary>
/// Tracks the lifecycle of a nutritionist's request for a client to submit food-diary photos.
/// A request is either attached to an existing client-professional link (<see cref="LinkId"/>)
/// or bundled with a pending invite (<see cref="PendingInviteId"/>); exactly one must be set.
/// </summary>
/// <remarks>
/// Invariants enforced via PostgreSQL CHECK constraints (see <c>PhotoDiaryRequestConfiguration</c>):
/// <list type="bullet">
///   <item>Exactly one of <see cref="LinkId"/> / <see cref="PendingInviteId"/> is non-null (XOR).</item>
///   <item><see cref="Mode"/> is non-null whenever <see cref="Status"/> is
///     <see cref="PhotoDiaryStatus.Accepted"/>, <see cref="PhotoDiaryStatus.InProgress"/>,
///     or <see cref="PhotoDiaryStatus.Completed"/>; other statuses may carry it too.</item>
///   <item><see cref="DismissReason"/> is non-null only when <see cref="Status"/> is
///     <see cref="PhotoDiaryStatus.Dismissed"/>.</item>
///   <item><see cref="AcceptedAt"/> is non-null whenever <see cref="Status"/> is
///     <see cref="PhotoDiaryStatus.Accepted"/>, <see cref="PhotoDiaryStatus.InProgress"/>,
///     or <see cref="PhotoDiaryStatus.Completed"/>, and null when it is
///     <see cref="PhotoDiaryStatus.Pending"/> or <see cref="PhotoDiaryStatus.Dismissed"/>.
///     A <see cref="PhotoDiaryStatus.Cancelled"/> request keeps whatever it had.</item>
///   <item><see cref="CompletedAt"/> is non-null if and only if <see cref="Status"/> is
///     <see cref="PhotoDiaryStatus.Completed"/>.</item>
///   <item><see cref="DurationDays"/> is between 1 and 30.</item>
/// </list>
/// </remarks>
public class PhotoDiaryRequest
{
    /// <summary>
    /// Primary key — a client-generated or server-generated GUID.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Foreign key to the <see cref="ApplicationUser"/> (nutritionist) who created the request.
    /// </summary>
    public Guid ProfessionalId { get; set; }

    /// <summary>
    /// Foreign key to the <see cref="ClientProfessionalLink"/> this request is attached to.
    /// Mutually exclusive with <see cref="PendingInviteId"/>; exactly one must be set.
    /// </summary>
    public long? LinkId { get; set; }

    /// <summary>
    /// Foreign key to the <see cref="PendingInvite"/> this request is bundled with.
    /// Mutually exclusive with <see cref="LinkId"/>; exactly one must be set.
    /// </summary>
    public long? PendingInviteId { get; set; }

    /// <summary>
    /// The MongoDB external identifier of the nutrition plan this request is scoped to.
    /// Nullable — a request may exist without a specific plan context.
    /// No FK constraint because the plan lives in MongoDB.
    /// </summary>
    public Guid? PlanId { get; set; }

    /// <summary>
    /// How many days the request stays open (default 7). A manual <see cref="PhotoDiaryMode.Bulk"/> request
    /// is created with 1; an automation's one-off Bulk request is open for 7.
    /// Must be between 1 and 30 (enforced by CHECK constraint). The open window ends at
    /// <c>CreatedAt + DurationDays</c>.
    /// </summary>
    public int DurationDays { get; set; } = 7;

    /// <summary>
    /// The upload mode the coach chose when creating the request. Null only on legacy rows
    /// created before the coach picked the mode.
    /// </summary>
    public PhotoDiaryMode? Mode { get; set; }

    /// <summary>
    /// Optional note from the coach to the client (max 500 chars).
    /// </summary>
    [MaxLength(500)]
    public string? Message { get; set; }

    /// <summary>
    /// Current lifecycle status of the request.
    /// </summary>
    public PhotoDiaryStatus Status { get; set; } = PhotoDiaryStatus.Pending;

    /// <summary>
    /// Optional reason provided by the client when dismissing the request (max 500 chars).
    /// Non-null only when <see cref="Status"/> is <see cref="PhotoDiaryStatus.Dismissed"/>.
    /// </summary>
    [MaxLength(500)]
    public string? DismissReason { get; set; }

    /// <summary>
    /// Timestamp when the client accepted the request.
    /// Non-null when <see cref="Status"/> ∈ {Accepted, InProgress, Completed}.
    /// </summary>
    public DateTimeOffset? AcceptedAt { get; set; }

    /// <summary>
    /// Timestamp when the client submitted / finalized the diary.
    /// Non-null iff <see cref="Status"/> is <see cref="PhotoDiaryStatus.Completed"/>.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// Audit timestamp — when the request record was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Audit timestamp — when the request record was last updated.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// End of the open window: <c>CreatedAt + DurationDays</c>, or null for a one-time
    /// (<see cref="PhotoDiaryMode.Bulk"/>) request, which stays open until the client submits or
    /// dismisses it or the coach cancels it. Compute in memory — not part of any EF query.
    /// </summary>
    public DateTimeOffset? EndsAt => Mode == PhotoDiaryMode.Bulk ? null : CreatedAt.AddDays(DurationDays);

    /// <summary>
    /// True when the request has an end date and <paramref name="now"/> is at or past it.
    /// A one-time request never expires.
    /// </summary>
    public bool IsExpiredAt(DateTimeOffset now) => EndsAt is { } endsAt && now >= endsAt;

    /// <summary>
    /// True while the request is Pending, Accepted or InProgress and has not reached <see cref="EndsAt"/>.
    /// </summary>
    public bool IsOpenAt(DateTimeOffset now) => IsOpenStatus(Status) && !IsExpiredAt(now);

    /// <summary>
    /// True for the statuses in which a request can still be open (Pending, Accepted, InProgress).
    /// </summary>
    public static bool IsOpenStatus(PhotoDiaryStatus status) =>
        status is PhotoDiaryStatus.Pending or PhotoDiaryStatus.Accepted or PhotoDiaryStatus.InProgress;

    // ── Navigation properties ─────────────────────────────────────────────────

    /// <summary>
    /// Navigation property to the nutritionist user.
    /// </summary>
    public ApplicationUser Professional { get; set; } = null!;

    /// <summary>
    /// Navigation property to the client-professional link (when <see cref="LinkId"/> is set).
    /// </summary>
    public ClientProfessionalLink? Link { get; set; }

    /// <summary>
    /// Navigation property to the pending invite (when <see cref="PendingInviteId"/> is set).
    /// </summary>
    public PendingInvite? PendingInvite { get; set; }

    /// <summary>
    /// Photos that were uploaded against this diary request.
    /// Populated only when explicitly included; otherwise an empty collection.
    /// </summary>
    public ICollection<PlanPhoto> Photos { get; set; } = [];
}
