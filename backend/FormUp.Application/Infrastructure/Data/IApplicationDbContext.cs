using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FormUp.Application.Infrastructure.Data;

/// <summary>
/// Abstraction over <see cref="ApplicationDbContext"/> for unit testing.
/// </summary>
public interface IApplicationDbContext
{
    /// <summary>
    /// Application users (from IdentityDbContext).
    /// </summary>
    DbSet<ApplicationUser> Users { get; }

    /// <summary>
    /// User-to-role assignments (from IdentityDbContext).
    /// </summary>
    DbSet<IdentityUserRole<Guid>> UserRoles { get; }

    /// <summary>
    /// Application roles (from IdentityDbContext).
    /// </summary>
    DbSet<ApplicationRole> Roles { get; }

    /// <summary>
    /// Professional profiles (trainers and nutritionists).
    /// </summary>
    DbSet<ProfessionalProfile> ProfessionalProfiles { get; set; }

    /// <summary>
    /// Client profiles.
    /// </summary>
    DbSet<ClientProfile> ClientProfiles { get; set; }

    /// <summary>
    /// Client-professional relationships.
    /// </summary>
    DbSet<ClientProfessionalLink> ClientProfessionalLinks { get; set; }

    /// <summary>
    /// Body measurement records.
    /// </summary>
    DbSet<BodyMeasurement> BodyMeasurements { get; set; }

    /// <summary>
    /// Plan-scoped photos (body progress, food, and free-form).
    /// Replaces the retired ProgressPhoto entity.
    /// </summary>
    DbSet<PlanPhoto> PlanPhotos { get; set; }
    DbSet<PlanPhotoViewer> PlanPhotoViewers { get; set; }

    /// <summary>
    /// Refresh tokens for JWT authentication.
    /// </summary>
    DbSet<RefreshToken> RefreshTokens { get; set; }

    /// <summary>
    /// Invitation tokens for client onboarding.
    /// </summary>
    DbSet<InvitationToken> InvitationTokens { get; set; }

    /// <summary>
    /// Audit log entries for GDPR compliance.
    /// </summary>
    DbSet<AuditLog> AuditLogs { get; set; }

    /// <summary>
    /// Client onboarding questionnaire data.
    /// </summary>
    DbSet<ClientOnboardingData> ClientOnboardingData { get; set; }

    /// <summary>
    /// Computed nutrition targets derived from client onboarding data.
    /// </summary>
    DbSet<ClientNutritionTargets> ClientNutritionTargets { get; set; }

    /// <summary>
    /// Notifications.
    /// </summary>
    DbSet<Notification> Notifications { get; set; }

    /// <summary>
    /// Pending invitations sent by professionals to prospective clients.
    /// </summary>
    DbSet<PendingInvite> PendingInvites { get; set; }

    /// <summary>
    /// Questionnaire templates created by professionals.
    /// </summary>
    DbSet<Questionnaire> Questionnaires { get; set; }

    /// <summary>
    /// Questions within questionnaire templates.
    /// </summary>
    DbSet<QuestionnaireQuestion> QuestionnaireQuestions { get; set; }

    /// <summary>
    /// Client responses to questionnaires.
    /// </summary>
    DbSet<QuestionnaireResponse> QuestionnaireResponses { get; set; }

    /// <summary>
    /// Individual answers within questionnaire responses.
    /// </summary>
    DbSet<QuestionnaireAnswer> QuestionnaireAnswers { get; set; }

    /// <summary>
    /// Client requests to join a professional's roster.
    /// </summary>
    DbSet<ClientRequest> ClientRequests { get; set; }

    /// <summary>
    /// Email verification tokens.
    /// </summary>
    DbSet<EmailVerificationToken> EmailVerificationTokens { get; set; }

    /// <summary>
    /// Device push tokens for mobile notifications.
    /// </summary>
    DbSet<DevicePushToken> DevicePushTokens { get; set; }

    /// <summary>
    /// Saved per-user email and push choices for notification events.
    /// </summary>
    DbSet<NotificationPreference> NotificationPreferences { get; set; }

    /// <summary>
    /// Bursts of events waiting to be sent as one grouped notification email.
    /// </summary>
    DbSet<PendingNotificationEmail> PendingNotificationEmails { get; set; }

    /// <summary>
    /// Messaging conversations between professionals and clients.
    /// </summary>
    DbSet<Conversation> Conversations { get; set; }

    /// <summary>
    /// Chat messages within conversations.
    /// </summary>
    DbSet<ChatMessage> ChatMessages { get; set; }

    /// <summary>
    /// Photo diary requests sent by nutritionists to clients.
    /// </summary>
    DbSet<PhotoDiaryRequest> PhotoDiaryRequests { get; set; }

    /// <summary>
    /// Idempotency log for the daily photo-diary reminder scheduler.
    /// One row per (DiaryRequestId, ClientLocalDate).
    /// </summary>
    DbSet<PhotoDiaryReminderLog> PhotoDiaryReminderLogs { get; set; }

    /// <summary>
    /// External OAuth provider logins linked to application users.
    /// </summary>
    DbSet<UserExternalLogin> UserExternalLogins { get; set; }

    /// <summary>
    /// Server-issued single-use nonces for social sign-in replay hardening.
    /// </summary>
    DbSet<SocialLoginNonce> SocialLoginNonces { get; set; }

    /// <summary>
    /// Coach subscription tiers: pricing, billing cadence, and feature flags/limits.
    /// </summary>
    DbSet<SubscriptionPlan> SubscriptionPlans { get; set; }

    /// <summary>
    /// Price history of the subscription tiers; one open row per plan.
    /// </summary>
    DbSet<SubscriptionPlanPrice> SubscriptionPlanPrices { get; set; }

    /// <summary>
    /// A professional's subscription to a <see cref="SubscriptionPlan"/>.
    /// </summary>
    DbSet<CoachSubscription> CoachSubscriptions { get; set; }

    /// <summary>
    /// Ids of the payment-provider webhook events already applied, so a repeated delivery is a no-op.
    /// </summary>
    DbSet<ProcessedWebhookEvent> ProcessedWebhookEvents { get; set; }

    /// <summary>
    /// Coach-owned tags used to label clients.
    /// </summary>
    DbSet<ClientTag> ClientTags { get; set; }

    /// <summary>
    /// Assignments of a <see cref="ClientTag"/> to a specific <see cref="ClientProfessionalLink"/>.
    /// </summary>
    DbSet<ClientTagAssignment> ClientTagAssignments { get; set; }

    /// <summary>
    /// Coach-owned automation rules (global and client-only).
    /// </summary>
    DbSet<AutomationRule> AutomationRules { get; set; }

    /// <summary>
    /// Chosen clients of automation rules with a Chosen audience.
    /// </summary>
    DbSet<AutomationRuleAudienceClient> AutomationRuleAudienceClients { get; set; }

    /// <summary>
    /// Per-client customisations and pauses of automation rules.
    /// </summary>
    DbSet<AutomationClientOverride> AutomationClientOverrides { get; set; }

    /// <summary>
    /// Per rule-and-client runtime schedule rows.
    /// </summary>
    DbSet<AutomationSchedule> AutomationSchedules { get; set; }

    /// <summary>
    /// Log of automation occurrences per rule and client.
    /// </summary>
    DbSet<AutomationRun> AutomationRuns { get; set; }

    /// <summary>
    /// Coach payment settings (bank account and payment request defaults), one row per professional.
    /// </summary>
    DbSet<CoachPaymentSettings> CoachPaymentSettings { get; set; }

    /// <summary>
    /// History of bank account changes, carrying the data needed to undo one.
    /// </summary>
    DbSet<CoachBankAccountChange> CoachBankAccountChanges { get; set; }

    /// <summary>
    /// Emailed one-time codes that re-authenticate a coach without a password.
    /// </summary>
    DbSet<CoachReauthCode> CoachReauthCodes { get; set; }

    /// <summary>
    /// Paid offers a coach sells to clients.
    /// </summary>
    DbSet<CoachPackage> CoachPackages { get; set; }

    /// <summary>
    /// Price history of coach packages.
    /// </summary>
    DbSet<CoachPackagePrice> CoachPackagePrices { get; set; }

    /// <summary>
    /// Discounts a coach can apply to payments.
    /// </summary>
    DbSet<CoachDiscount> CoachDiscounts { get; set; }

    /// <summary>
    /// Links between a discount and the packages it is limited to.
    /// </summary>
    DbSet<CoachDiscountPackage> CoachDiscountPackages { get; set; }

    /// <summary>
    /// Each coach's payment relationship with a client (client number, terms, payments).
    /// </summary>
    DbSet<ClientPaymentAssignment> ClientPaymentAssignments { get; set; }

    /// <summary>
    /// History of the terms clients pay under.
    /// </summary>
    DbSet<ClientPaymentTerms> ClientPaymentTerms { get; set; }

    /// <summary>
    /// Payments clients owe coaches.
    /// </summary>
    DbSet<ClientPayment> ClientPayments { get; set; }

    /// <summary>
    /// Saves all changes made in this context to the database.
    /// </summary>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically marks a nonce as consumed by issuing a single UPDATE statement
    /// conditional on the nonce being currently unconsumed and not expired.
    /// Returns the number of rows updated (1 = consumed; 0 = already consumed,
    /// expired, or not found — the caller lost the consume race).
    /// </summary>
    Task<int> ConsumeNonceAsync(string nonce, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically rotates a refresh token AND inserts its successor row in a
    /// single database transaction: a conditional UPDATE
    /// (<c>WHERE Token = @token AND RevokedAt IS NULL</c>) that closes the
    /// read-then-write race where two concurrent <c>/auth/refresh</c> calls for
    /// the same token could both observe it as active, followed — only if that
    /// update wins — by inserting <paramref name="successorToken"/>, both
    /// committed together. Previously these were two separate autocommitting
    /// writes; a crash (or a concurrent reader) between them could observe
    /// <c>ReplacedByToken</c> set on the predecessor with no successor row
    /// present yet, or ever if the process crashed (#694).
    /// Returns the number of rows updated by the conditional UPDATE (1 = this
    /// caller won the rotation race and the successor row is now durably
    /// present; 0 = the token was already revoked by a concurrent request — no
    /// successor was inserted by this call, and the caller must re-read the row
    /// and route into the reuse/reconcile discriminator).
    /// </summary>
    Task<int> RotateRefreshTokenAsync(string token, RefreshToken successorToken, DateTime revokedAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically revokes every currently-active refresh token belonging to a
    /// user (a "token family" invalidation). Used when a rotated refresh token
    /// is replayed outside the reuse grace window — treated as theft, so the
    /// whole family is burned rather than just the one token. Returns the
    /// number of rows revoked.
    /// </summary>
    Task<int> RevokeRefreshTokenFamilyAsync(Guid userId, DateTime revokedAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a database transaction, for a read and a dependent write that must be serialized
    /// against a concurrent request. Pair it with <see cref="LockClientProfileAsync"/>.
    /// </summary>
    Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes a row-level <c>FOR NO KEY UPDATE</c> lock on one client-profile row, serializing
    /// every link-creation path that must first check that client's profession slots (#1009).
    /// Call it inside a transaction from <see cref="BeginTransactionAsync"/>, and after any
    /// find-or-create save that produces the row — locking a row that does not exist yet is a
    /// no-op.
    /// </summary>
    Task LockClientProfileAsync(long clientProfileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes a row-level <c>FOR NO KEY UPDATE</c> lock on one professional-profile row, serializing the
    /// writes that allocate a coach-wide unique value (a client number, a variable symbol). Call it inside
    /// a transaction from <see cref="BeginTransactionAsync"/>.
    /// </summary>
    Task LockProfessionalProfileAsync(long professionalProfileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims the earliest automation schedule row that is due at <paramref name="nowUtc"/> with
    /// <c>FOR UPDATE SKIP LOCKED</c>: a row another worker already holds is passed over instead of
    /// waited for, so two workers never run the same row. Call it inside a transaction from
    /// <see cref="BeginTransactionAsync"/>; the lock lasts until that transaction ends. Returns the
    /// tracked row, or null when nothing is due.
    /// </summary>
    Task<AutomationSchedule?> ClaimDueAutomationScheduleAsync(DateTime nowUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens or extends the grouped-email burst for one recipient, event and group with a single
    /// <c>INSERT … ON CONFLICT DO UPDATE</c>: a new burst is due after the event's quiet window, an open one
    /// moves its last event and due time on, never past the event's maximum wait. Timing only; no content.
    /// </summary>
    Task UpsertPendingNotificationEmailAsync(
        Guid recipientUserId,
        NotificationEvent notificationEvent,
        Guid groupId,
        DateTime eventAtUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims the earliest pending notification email that is due at <paramref name="nowUtc"/> with
    /// <c>FOR UPDATE SKIP LOCKED</c>, so two workers never send the same burst. Call it inside a transaction
    /// from <see cref="BeginTransactionAsync"/>; the lock lasts until that transaction ends. Returns the
    /// tracked row, or null when nothing is due.
    /// </summary>
    Task<PendingNotificationEmail?> ClaimDuePendingNotificationEmailAsync(DateTime nowUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves one user's notification choices with a single <c>INSERT … ON CONFLICT (user_id, event)
    /// DO UPDATE</c>, so two concurrent first saves cannot collide on the unique index. The events
    /// must be distinct.
    /// </summary>
    Task UpsertNotificationPreferencesAsync(
        Guid userId,
        IReadOnlyCollection<(NotificationEvent Event, bool EmailEnabled, bool PushEnabled)> preferences,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets only the email switch of one event with a single <c>INSERT … ON CONFLICT (user_id, event) DO UPDATE</c>.
    /// A user with no saved row gets one holding <paramref name="pushEnabledIfNew"/>; a saved push value is never touched.
    /// Repeating the call with the same value changes nothing but the update stamp.
    /// </summary>
    Task UpsertNotificationEmailEnabledAsync(
        Guid userId,
        NotificationEvent notificationEvent,
        bool emailEnabled,
        bool pushEnabledIfNew,
        CancellationToken cancellationToken = default);
}
