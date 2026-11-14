using System.Text.Json;
using FormUp.Application.Domain.Common;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace FormUp.Application.Infrastructure.Data;

/// <summary>
/// Main database context for Form Up, extending ASP.NET Identity context.
/// </summary>
/// <param name="options">Database context options.</param>
public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>(options), IApplicationDbContext
{

    /// <summary>
    /// Professional profiles (trainers and nutritionists).
    /// </summary>
    public virtual DbSet<ProfessionalProfile> ProfessionalProfiles { get; set; } = null!;

    /// <summary>
    /// Client profiles.
    /// </summary>
    public virtual DbSet<ClientProfile> ClientProfiles { get; set; } = null!;

    /// <summary>
    /// Client-professional relationships.
    /// </summary>
    public virtual DbSet<ClientProfessionalLink> ClientProfessionalLinks { get; set; } = null!;

    /// <summary>
    /// Body measurement records.
    /// </summary>
    public virtual DbSet<BodyMeasurement> BodyMeasurements { get; set; } = null!;

    /// <summary>
    /// Plan-scoped photos (body progress, food, and free-form).
    /// Replaces the retired ProgressPhoto entity.
    /// </summary>
    public virtual DbSet<PlanPhoto> PlanPhotos { get; set; } = null!;
    public virtual DbSet<PlanPhotoViewer> PlanPhotoViewers { get; set; } = null!;

    /// <summary>
    /// Refresh tokens for JWT authentication.
    /// </summary>
    public virtual DbSet<RefreshToken> RefreshTokens { get; set; } = null!;

    /// <summary>
    /// Invitation tokens for client onboarding.
    /// </summary>
    public virtual DbSet<InvitationToken> InvitationTokens { get; set; } = null!;

    /// <summary>
    /// Audit log entries for GDPR compliance.
    /// </summary>
    public virtual DbSet<AuditLog> AuditLogs { get; set; } = null!;

    /// <summary>
    /// Client onboarding questionnaire data.
    /// </summary>
    public virtual DbSet<ClientOnboardingData> ClientOnboardingData { get; set; } = null!;

    /// <summary>
    /// Computed nutrition targets derived from client onboarding data.
    /// </summary>
    public virtual DbSet<ClientNutritionTargets> ClientNutritionTargets { get; set; } = null!;

    /// <inheritdoc />
    public virtual DbSet<Notification> Notifications { get; set; } = null!;

    /// <summary>
    /// Pending invitations sent by professionals to prospective clients.
    /// </summary>
    public virtual DbSet<PendingInvite> PendingInvites { get; set; } = null!;

    /// <summary>
    /// Questionnaire templates created by professionals.
    /// </summary>
    public virtual DbSet<Questionnaire> Questionnaires { get; set; } = null!;

    /// <summary>
    /// Questions within questionnaire templates.
    /// </summary>
    public virtual DbSet<QuestionnaireQuestion> QuestionnaireQuestions { get; set; } = null!;

    /// <summary>
    /// Client responses to questionnaires.
    /// </summary>
    public virtual DbSet<QuestionnaireResponse> QuestionnaireResponses { get; set; } = null!;

    /// <summary>
    /// Individual answers within questionnaire responses.
    /// </summary>
    public virtual DbSet<QuestionnaireAnswer> QuestionnaireAnswers { get; set; } = null!;

    /// <summary>
    /// Client requests to join a professional's roster.
    /// </summary>
    public virtual DbSet<ClientRequest> ClientRequests { get; set; } = null!;

    /// <summary>
    /// Email verification tokens.
    /// </summary>
    public virtual DbSet<EmailVerificationToken> EmailVerificationTokens { get; set; } = null!;

    /// <inheritdoc />
    public virtual DbSet<DevicePushToken> DevicePushTokens { get; set; } = null!;

    /// <inheritdoc />
    public virtual DbSet<NotificationPreference> NotificationPreferences { get; set; } = null!;

    /// <inheritdoc />
    public virtual DbSet<PendingNotificationEmail> PendingNotificationEmails { get; set; } = null!;

    /// <inheritdoc />
    public virtual DbSet<Conversation> Conversations { get; set; } = null!;

    /// <inheritdoc />
    public virtual DbSet<ChatMessage> ChatMessages { get; set; } = null!;

    /// <summary>
    /// Photo diary requests sent by nutritionists to clients.
    /// </summary>
    public virtual DbSet<PhotoDiaryRequest> PhotoDiaryRequests { get; set; } = null!;

    /// <summary>
    /// Idempotency log for the daily photo-diary reminder scheduler.
    /// </summary>
    public virtual DbSet<PhotoDiaryReminderLog> PhotoDiaryReminderLogs { get; set; } = null!;

    /// <summary>
    /// External OAuth provider logins linked to application users.
    /// </summary>
    public virtual DbSet<UserExternalLogin> UserExternalLogins { get; set; } = null!;

    /// <summary>
    /// Server-issued single-use nonces for social sign-in replay hardening.
    /// </summary>
    public virtual DbSet<SocialLoginNonce> SocialLoginNonces { get; set; } = null!;

    /// <summary>
    /// Coach subscription tiers: pricing, billing cadence, and feature flags/limits.
    /// </summary>
    public virtual DbSet<SubscriptionPlan> SubscriptionPlans { get; set; } = null!;

    /// <summary>
    /// Price history of the subscription tiers; one open row per plan.
    /// </summary>
    public virtual DbSet<SubscriptionPlanPrice> SubscriptionPlanPrices { get; set; } = null!;

    /// <summary>
    /// A professional's subscription to a <see cref="SubscriptionPlan"/>.
    /// </summary>
    public virtual DbSet<CoachSubscription> CoachSubscriptions { get; set; } = null!;

    /// <summary>
    /// Ids of the payment-provider webhook events already applied, so a repeated delivery is a no-op.
    /// </summary>
    public virtual DbSet<ProcessedWebhookEvent> ProcessedWebhookEvents { get; set; } = null!;

    /// <summary>
    /// Coach-owned tags used to label clients.
    /// </summary>
    public virtual DbSet<ClientTag> ClientTags { get; set; } = null!;

    /// <summary>
    /// Assignments of a <see cref="ClientTag"/> to a specific <see cref="ClientProfessionalLink"/>.
    /// </summary>
    public virtual DbSet<ClientTagAssignment> ClientTagAssignments { get; set; } = null!;

    /// <summary>
    /// Coach-owned automation rules (global and client-only).
    /// </summary>
    public virtual DbSet<AutomationRule> AutomationRules { get; set; } = null!;

    /// <summary>
    /// Chosen clients of automation rules with a Chosen audience.
    /// </summary>
    public virtual DbSet<AutomationRuleAudienceClient> AutomationRuleAudienceClients { get; set; } = null!;

    /// <summary>
    /// Per-client customisations and pauses of automation rules.
    /// </summary>
    public virtual DbSet<AutomationClientOverride> AutomationClientOverrides { get; set; } = null!;

    /// <summary>
    /// Per rule-and-client runtime schedule rows.
    /// </summary>
    public virtual DbSet<AutomationSchedule> AutomationSchedules { get; set; } = null!;

    /// <summary>
    /// Log of automation occurrences per rule and client.
    /// </summary>
    public virtual DbSet<AutomationRun> AutomationRuns { get; set; } = null!;

    /// <inheritdoc />
    public virtual DbSet<CoachPaymentSettings> CoachPaymentSettings { get; set; } = null!;

    /// <inheritdoc />
    public virtual DbSet<CoachBankAccountChange> CoachBankAccountChanges { get; set; } = null!;

    /// <inheritdoc />
    public virtual DbSet<CoachReauthCode> CoachReauthCodes { get; set; } = null!;

    /// <inheritdoc />
    public virtual DbSet<CoachPackage> CoachPackages { get; set; } = null!;

    /// <inheritdoc />
    public virtual DbSet<CoachPackagePrice> CoachPackagePrices { get; set; } = null!;

    /// <inheritdoc />
    public virtual DbSet<CoachDiscount> CoachDiscounts { get; set; } = null!;

    /// <inheritdoc />
    public virtual DbSet<CoachDiscountPackage> CoachDiscountPackages { get; set; } = null!;

    /// <inheritdoc />
    public virtual DbSet<ClientPaymentAssignment> ClientPaymentAssignments { get; set; } = null!;

    /// <inheritdoc />
    public virtual DbSet<ClientPaymentTerms> ClientPaymentTerms { get; set; } = null!;

    /// <inheritdoc />
    public virtual DbSet<ClientPayment> ClientPayments { get; set; } = null!;

    /// <inheritdoc />
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        optionsBuilder.ConfigureWarnings(w =>
            w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Remap ASP.NET Identity tables to snake_case
        builder.Entity<ApplicationUser>().ToTable("users");
        builder.Entity<ApplicationRole>().ToTable("roles");
        builder.Entity<IdentityUserRole<Guid>>().ToTable("user_roles");
        builder.Entity<IdentityUserClaim<Guid>>().ToTable("user_claims");
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins");
        builder.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens");
        builder.Entity<IdentityRoleClaim<Guid>>().ToTable("role_claims");

        builder.Entity<Questionnaire>()
            .HasIndex(q => q.ProfessionalId);

        builder.Entity<Conversation>(e =>
        {
            e.HasIndex(c => new { c.ProfessionalUserId, c.ClientUserId }).IsUnique();
            e.HasOne(c => c.Professional).WithMany().HasForeignKey(c => c.ProfessionalUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(c => c.Client).WithMany().HasForeignKey(c => c.ClientUserId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<ChatMessage>(e =>
        {
            e.HasOne(m => m.Conversation).WithMany(c => c.Messages).HasForeignKey(m => m.ConversationId);
            e.HasOne(m => m.Sender).WithMany().HasForeignKey(m => m.SenderUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(m => new { m.ConversationId, m.DateCreated });

            // Deduplicates a re-processed cooperation event (double-accept, retried
            // withdraw) — only one Event row per (conversation, type, source) can exist.
            // Partial: plain text rows carry a null EventSourceId and are unconstrained.
            e.HasIndex(m => new { m.ConversationId, m.EventType, m.EventSourceId })
                .IsUnique()
                .HasFilter("event_source_id IS NOT NULL");
        });

        builder.Entity<PlanPhoto>(e =>
        {
            e.Property(p => p.Category).HasConversion<string>();
            e.Property(p => p.PlanType).HasConversion<string>();

            e.HasOne(p => p.ClientProfile)
                .WithMany(cp => cp.PlanPhotos)
                .HasForeignKey(p => p.ClientProfileId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(p => p.UploadedByUser)
                .WithMany()
                .HasForeignKey(p => p.UploadedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(p => new { p.ClientProfileId, p.Category });
            e.HasIndex(p => p.PlanId);
            e.HasIndex(p => p.DiaryRequestId)
                .HasDatabaseName("ix_plan_photos_diary_request_id");
        });

        builder.Entity<PlanPhotoViewer>(e =>
        {
            e.HasOne(v => v.PlanPhoto)
                .WithMany()
                .HasForeignKey(v => v.PlanPhotoId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(v => v.ProfessionalUser)
                .WithMany()
                .HasForeignKey(v => v.ProfessionalUserId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(v => new { v.PlanPhotoId, v.ProfessionalUserId }).IsUnique();
            e.HasIndex(v => v.ProfessionalUserId);
        });

        builder.Entity<PhotoDiaryReminderLog>(e =>
        {
            e.HasKey(l => l.Id);
            e.Property(l => l.ClientLocalDate).HasColumnType("date");

            e.HasOne(l => l.DiaryRequest)
                .WithMany()
                .HasForeignKey(l => l.DiaryRequestId)
                .OnDelete(DeleteBehavior.Cascade);

            // Uniqueness constraint: at most one reminder log per (request, calendar day).
            e.HasIndex(l => new { l.DiaryRequestId, l.ClientLocalDate })
                .IsUnique()
                .HasDatabaseName("ix_photo_diary_reminder_logs_request_date");
        });

        builder.Entity<NotificationPreference>(e =>
        {
            e.HasIndex(p => new { p.UserId, p.Event }).IsUnique();
            e.HasOne(p => p.User)
                .WithMany()
                .HasForeignKey(p => p.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<CoachPaymentSettings>(e =>
        {
            e.HasIndex(s => s.ProfessionalProfileId).IsUnique();
            e.Property(s => s.PaymentDueDays).HasDefaultValue(7);
            e.HasOne(s => s.ProfessionalProfile)
                .WithOne()
                .HasForeignKey<CoachPaymentSettings>(s => s.ProfessionalProfileId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<CoachPackage>(e =>
        {
            e.HasIndex(p => p.ProfessionalProfileId);
            e.Property(p => p.Price).HasPrecision(18, 2);
            e.HasOne(p => p.ProfessionalProfile)
                .WithMany()
                .HasForeignKey(p => p.ProfessionalProfileId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<CoachPackagePrice>(e =>
        {
            e.HasIndex(p => new { p.CoachPackageId, p.EffectiveFromUtc });
            e.Property(p => p.Price).HasPrecision(18, 2);
            e.HasOne(p => p.CoachPackage)
                .WithMany(p => p.Prices)
                .HasForeignKey(p => p.CoachPackageId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<CoachDiscount>(e =>
        {
            e.HasIndex(d => d.ProfessionalProfileId);
            e.Property(d => d.Value).HasPrecision(18, 2);
            e.HasOne(d => d.ProfessionalProfile)
                .WithMany()
                .HasForeignKey(d => d.ProfessionalProfileId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<CoachDiscountPackage>(e =>
        {
            e.HasIndex(l => new { l.CoachDiscountId, l.CoachPackageId }).IsUnique();
            e.HasIndex(l => l.CoachPackageId);
            e.HasOne(l => l.CoachDiscount)
                .WithMany(d => d.Packages)
                .HasForeignKey(l => l.CoachDiscountId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(l => l.CoachPackage)
                .WithMany()
                .HasForeignKey(l => l.CoachPackageId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ClientPaymentAssignment>(e =>
        {
            e.HasIndex(a => new { a.ProfessionalProfileId, a.ClientProfileId }).IsUnique();
            e.HasIndex(a => new { a.ProfessionalProfileId, a.ClientNumber }).IsUnique();
            e.HasIndex(a => a.ClientProfileId);
            e.HasOne(a => a.ProfessionalProfile)
                .WithMany()
                .HasForeignKey(a => a.ProfessionalProfileId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(a => a.ClientProfile)
                .WithMany()
                .HasForeignKey(a => a.ClientProfileId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ClientPaymentTerms>(e =>
        {
            e.HasIndex(t => new { t.ClientPaymentAssignmentId, t.EffectiveFromUtc });
            e.HasIndex(t => t.CoachPackageId);
            e.HasIndex(t => t.CoachDiscountId);
            e.Property(t => t.CustomPrice).HasPrecision(18, 2);
            e.HasOne(t => t.ClientPaymentAssignment)
                .WithMany(a => a.Terms)
                .HasForeignKey(t => t.ClientPaymentAssignmentId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(t => t.CoachPackage)
                .WithMany()
                .HasForeignKey(t => t.CoachPackageId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(t => t.CoachDiscount)
                .WithMany()
                .HasForeignKey(t => t.CoachDiscountId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<ClientPayment>(e =>
        {
            e.HasIndex(p => new { p.ClientPaymentAssignmentId, p.DueDate });
            e.HasIndex(p => new { p.ClientPaymentAssignmentId, p.PeriodStart })
                .IsUnique()
                .HasFilter("period_start IS NOT NULL");
            e.HasIndex(p => new { p.ProfessionalProfileId, p.VariableSymbol })
                .IsUnique()
                .HasFilter("variable_symbol IS NOT NULL");
            e.Property(p => p.Amount).HasPrecision(18, 2);
            e.Property(p => p.BasePrice).HasPrecision(18, 2);
            e.HasOne(p => p.ClientPaymentAssignment)
                .WithMany(a => a.Payments)
                .HasForeignKey(p => p.ClientPaymentAssignmentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Payment history reads the audit rows of one assignment by entity id.
        builder.Entity<AuditLog>(e => e.HasIndex(a => a.EntityId));

        builder.Entity<CoachBankAccountChange>(e =>
        {
            e.HasIndex(c => c.ProfessionalProfileId);
            e.HasIndex(c => c.UndoTokenHash)
                .IsUnique()
                .HasFilter("undo_token_hash IS NOT NULL");
            e.HasOne(c => c.ProfessionalProfile)
                .WithMany()
                .HasForeignKey(c => c.ProfessionalProfileId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // A deleted package or discount leaves the request without terms rather than removing it.
        builder.Entity<ClientRequest>(e =>
        {
            e.HasIndex(r => r.CoachPackageId);
            e.HasIndex(r => r.CoachDiscountId);
            e.HasOne(r => r.CoachPackage)
                .WithMany()
                .HasForeignKey(r => r.CoachPackageId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasOne(r => r.CoachDiscount)
                .WithMany()
                .HasForeignKey(r => r.CoachDiscountId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(r => r.ProposedPackageId);
            e.HasIndex(r => r.ProposedDiscountId);
            e.HasOne(r => r.ProposedPackage)
                .WithMany()
                .HasForeignKey(r => r.ProposedPackageId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasOne(r => r.ProposedDiscount)
                .WithMany()
                .HasForeignKey(r => r.ProposedDiscountId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<CoachReauthCode>(e =>
        {
            e.HasIndex(c => new { c.UserId, c.ExpiresAt });
            e.HasOne(c => c.User)
                .WithMany()
                .HasForeignKey(c => c.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);
    }

    /// <inheritdoc />
    public override int SaveChanges()
    {
        ApplyTimestamps();
        return base.SaveChanges();
    }

    /// <inheritdoc />
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        ApplyTimestamps();
        return base.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> ConsumeNonceAsync(string nonce, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        return SocialLoginNonces
            .Where(n => n.Nonce == nonce && n.ConsumedAt == null && n.ExpiresAt >= now)
            .ExecuteUpdateAsync(
                s => s.SetProperty(n => n.ConsumedAt, now),
                cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> RotateRefreshTokenAsync(string token, RefreshToken successorToken, DateTime revokedAt, CancellationToken cancellationToken = default)
    {
        // No retry-on-failure is configured for the Npgsql provider (see Program.cs),
        // so a plain BeginTransactionAsync/CommitAsync is sufficient here — no
        // execution-strategy wrapping needed to guard against a transient-retry
        // replaying the transaction.
        await using var transaction = await Database.BeginTransactionAsync(cancellationToken);

        var rowsAffected = await RefreshTokens
            .Where(rt => rt.Token == token && rt.RevokedAt == null)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(rt => rt.RevokedAt, revokedAt)
                    .SetProperty(rt => rt.ReplacedByToken, successorToken.Token),
                cancellationToken);

        if (rowsAffected > 0)
        {
            // Only insert the successor when this caller actually won the
            // conditional update — a losing caller must not create an orphaned
            // successor row nobody points to.
            RefreshTokens.Add(successorToken);
            await SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return rowsAffected;
    }

    /// <inheritdoc />
    public Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
        Database.BeginTransactionAsync(cancellationToken);

    /// <inheritdoc />
    public Task LockClientProfileAsync(long clientProfileId, CancellationToken cancellationToken = default)
    {
        // FOR NO KEY UPDATE rather than FOR UPDATE: the stronger mode would also block the
        // FOR KEY SHARE lock that any concurrent insert of a row referencing this client takes
        // on its FK parent, which is unrelated traffic this lock has no business serializing.
        //
        // The table and key-column identifiers come from the EF model rather than being spelled
        // out, so a rename cannot silently desync this string from the schema. They are also the
        // only parts spliced into the SQL, and can never originate from request input —
        // clientProfileId is passed as a real parameter, never concatenated.
        //
        // Assembled by concatenation rather than interpolation to keep EF1002 honest: the
        // ExecuteSqlAsync overload that warning recommends would parameterize the identifiers
        // too, and an identifier cannot be a bind parameter.
        var entityType = Model.FindEntityType(typeof(ClientProfile))!;
        var table = entityType.GetSchema() is { } schema
            ? QuoteIdentifier(schema) + "." + QuoteIdentifier(entityType.GetTableName()!)
            : QuoteIdentifier(entityType.GetTableName()!);
        var keyColumn = QuoteIdentifier(entityType.FindPrimaryKey()!.Properties[0].GetColumnName()!);

        var sql = "SELECT " + keyColumn + " FROM " + table
                  + " WHERE " + keyColumn + " = {0} FOR NO KEY UPDATE";

        return Database.ExecuteSqlRawAsync(sql, [clientProfileId], cancellationToken);
    }

    /// <inheritdoc />
    public Task LockProfessionalProfileAsync(long professionalProfileId, CancellationToken cancellationToken = default)
    {
        // Identifiers come from the EF model, never from input; the id is a real parameter.
        // See LockClientProfileAsync for why the statement is concatenated, not interpolated.
        var entityType = Model.FindEntityType(typeof(ProfessionalProfile))!;
        var table = entityType.GetSchema() is { } schema
            ? QuoteIdentifier(schema) + "." + QuoteIdentifier(entityType.GetTableName()!)
            : QuoteIdentifier(entityType.GetTableName()!);
        var keyColumn = QuoteIdentifier(entityType.FindPrimaryKey()!.Properties[0].GetColumnName()!);

        var sql = "SELECT " + keyColumn + " FROM " + table
                  + " WHERE " + keyColumn + " = {0} FOR NO KEY UPDATE";

        return Database.ExecuteSqlRawAsync(sql, [professionalProfileId], cancellationToken);
    }

    /// <inheritdoc />
    public async Task<AutomationSchedule?> ClaimDueAutomationScheduleAsync(
        DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        // Table and column identifiers come from the EF model, never from input; the instant is a
        // real parameter. See LockClientProfileAsync for why this is concatenated, not interpolated.
        var entityType = Model.FindEntityType(typeof(AutomationSchedule))!;
        var table = entityType.GetSchema() is { } schema
            ? QuoteIdentifier(schema) + "." + QuoteIdentifier(entityType.GetTableName()!)
            : QuoteIdentifier(entityType.GetTableName()!);
        var nextRunColumn = QuoteIdentifier(entityType.FindProperty(nameof(AutomationSchedule.NextRunAt))!.GetColumnName()!);
        var keyColumn = QuoteIdentifier(entityType.FindPrimaryKey()!.Properties[0].GetColumnName()!);

        var sql = "SELECT * FROM " + table
                  + " WHERE " + nextRunColumn + " <= {0}"
                  + " ORDER BY " + nextRunColumn + ", " + keyColumn
                  + " LIMIT 1 FOR UPDATE SKIP LOCKED";

        var claimed = await AutomationSchedules
            .FromSqlRaw(sql, DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc))
            .ToListAsync(cancellationToken);

        return claimed.SingleOrDefault();
    }

    /// <inheritdoc />
    public Task UpsertPendingNotificationEmailAsync(
        Guid recipientUserId,
        NotificationEvent notificationEvent,
        Guid groupId,
        DateTime eventAtUtc,
        CancellationToken cancellationToken = default)
    {
        // Every value is a bound parameter; the table and column names are fixed text. The window lengths
        // come from NotificationEmailGrouping and are passed as seconds.
        var eventAt = DateTime.SpecifyKind(eventAtUtc, DateTimeKind.Utc);
        var quietSeconds = NotificationEmailGrouping.QuietWindow(notificationEvent).TotalSeconds;
        var maxSeconds = NotificationEmailGrouping.MaxWindow(notificationEvent).TotalSeconds;
        var firstDueAt = NotificationEmailGrouping.DueAt(notificationEvent, eventAt, eventAt);

        const string sql =
            "INSERT INTO pending_notification_emails"
            + " (recipient_user_id, event, group_id, first_event_at, last_event_at, due_at, date_created)"
            + " VALUES ({0}, {1}, {2}, {3}, {3}, {4}, {3})"
            + " ON CONFLICT (recipient_user_id, event, group_id) DO UPDATE SET"
            + " last_event_at = GREATEST(pending_notification_emails.last_event_at, EXCLUDED.last_event_at),"
            + " due_at = LEAST("
            + "GREATEST(pending_notification_emails.last_event_at, EXCLUDED.last_event_at) + make_interval(secs => {5}),"
            + " pending_notification_emails.first_event_at + make_interval(secs => {6})),"
            + " date_updated = {3}";

        var parameters = new List<object>
        {
            recipientUserId, (int)notificationEvent, groupId, eventAt, firstDueAt, quietSeconds, maxSeconds
        };

        return Database.ExecuteSqlRawAsync(sql, parameters, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<PendingNotificationEmail?> ClaimDuePendingNotificationEmailAsync(
        DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        // Identifiers come from the EF model, never from input; the instant is a real parameter. See
        // LockClientProfileAsync for why this is concatenated, not interpolated.
        var entityType = Model.FindEntityType(typeof(PendingNotificationEmail))!;
        var table = entityType.GetSchema() is { } schema
            ? QuoteIdentifier(schema) + "." + QuoteIdentifier(entityType.GetTableName()!)
            : QuoteIdentifier(entityType.GetTableName()!);
        var dueColumn = QuoteIdentifier(entityType.FindProperty(nameof(PendingNotificationEmail.DueAt))!.GetColumnName()!);
        var keyColumn = QuoteIdentifier(entityType.FindPrimaryKey()!.Properties[0].GetColumnName()!);

        var sql = "SELECT * FROM " + table
                  + " WHERE " + dueColumn + " <= {0}"
                  + " ORDER BY " + dueColumn + ", " + keyColumn
                  + " LIMIT 1 FOR UPDATE SKIP LOCKED";

        var claimed = await PendingNotificationEmails
            .FromSqlRaw(sql, DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc))
            .ToListAsync(cancellationToken);

        return claimed.SingleOrDefault();
    }

    /// <inheritdoc />
    public Task UpsertNotificationPreferencesAsync(
        Guid userId,
        IReadOnlyCollection<(NotificationEvent Event, bool EmailEnabled, bool PushEnabled)> preferences,
        CancellationToken cancellationToken = default)
    {
        if (preferences.Count == 0)
        {
            return Task.CompletedTask;
        }

        // Rows go in a fixed event order so two concurrent saves lock the conflicting keys in the
        // same sequence. Only placeholder indexes are spliced into the SQL; every value is bound.
        var now = DateTime.UtcNow;
        var parameters = new List<object> { userId, now };
        var rows = new List<string>();

        foreach (var preference in preferences.OrderBy(p => (int)p.Event))
        {
            var next = parameters.Count;
            rows.Add("({0}, {" + next + "}, {" + (next + 1) + "}, {" + (next + 2) + "}, {1})");
            parameters.Add((int)preference.Event);
            parameters.Add(preference.EmailEnabled);
            parameters.Add(preference.PushEnabled);
        }

        var sql = "INSERT INTO notification_preferences (user_id, event, email_enabled, push_enabled, date_created) VALUES "
                  + string.Join(", ", rows)
                  + " ON CONFLICT (user_id, event) DO UPDATE SET email_enabled = EXCLUDED.email_enabled,"
                  + " push_enabled = EXCLUDED.push_enabled, date_updated = {1}";

        return Database.ExecuteSqlRawAsync(sql, parameters, cancellationToken);
    }

    /// <inheritdoc />
    public Task UpsertNotificationEmailEnabledAsync(
        Guid userId,
        NotificationEvent notificationEvent,
        bool emailEnabled,
        bool pushEnabledIfNew,
        CancellationToken cancellationToken = default)
    {
        // Every value is bound; the conflict branch deliberately leaves push_enabled alone.
        const string sql = "INSERT INTO notification_preferences (user_id, event, email_enabled, push_enabled, date_created) "
                           + "VALUES ({0}, {1}, {2}, {3}, {4}) "
                           + "ON CONFLICT (user_id, event) DO UPDATE SET email_enabled = EXCLUDED.email_enabled, date_updated = {4}";

        return Database.ExecuteSqlRawAsync(
            sql,
            [userId, (int)notificationEvent, emailEnabled, pushEnabledIfNew, DateTime.UtcNow],
            cancellationToken);
    }

    /// <summary>
    /// Wraps a schema identifier in double quotes, doubling any embedded quote.
    /// </summary>
    private static string QuoteIdentifier(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"") + "\"";

    /// <inheritdoc />
    public Task<int> RevokeRefreshTokenFamilyAsync(Guid userId, DateTime revokedAt, CancellationToken cancellationToken = default)
    {
        return RefreshTokens
            .Where(rt => rt.UserId == userId && rt.RevokedAt == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(rt => rt.RevokedAt, revokedAt),
                cancellationToken);
    }

    /// <summary>
    /// Automatically sets DateCreated and DateUpdated on tracked entities.
    /// </summary>
    private void ApplyTimestamps()
    {
        var entries = ChangeTracker.Entries<ITimestampable>();

        // Guarantees strictly increasing DateCreated across entities Added in the same
        // batch, tie-broken by add-order — ties are compared at whole-microsecond
        // precision (Postgres' column precision), needed for GetMessagesEndpoint's
        // (DateCreated, Id) ordering (#1100).
        DateTime? lastAssignedDateCreated = null;

        foreach (var entry in entries)
        {
            if (entry.State == EntityState.Added)
            {
                var dateCreated = NextDateCreated(DateTime.UtcNow, lastAssignedDateCreated);

                entry.Entity.DateCreated = dateCreated;
                lastAssignedDateCreated = dateCreated;
            }

            if (entry.State == EntityState.Modified)
            {
                entry.Entity.DateUpdated = DateTime.UtcNow;
            }
        }

        var publicEntries = ChangeTracker.Entries<IPublicEntity>();

        foreach (var entry in publicEntries)
        {
            if (entry.State == EntityState.Added && entry.Entity.PublicId == Guid.Empty)
            {
                entry.Entity.PublicId = Guid.NewGuid();
            }
        }
    }

    /// <summary>
    /// Truncates <paramref name="candidate"/> to whole microseconds and bumps it past
    /// <paramref name="previous"/> when the truncated value would otherwise tie or precede it.
    /// </summary>
    internal static DateTime NextDateCreated(DateTime candidate, DateTime? previous)
    {
        var truncated = new DateTime(candidate.Ticks - candidate.Ticks % TimeSpan.TicksPerMicrosecond, candidate.Kind);

        if (previous is { } previousValue && truncated <= previousValue)
        {
            return previousValue.AddTicks(TimeSpan.TicksPerMicrosecond);
        }

        return truncated;
    }
}
