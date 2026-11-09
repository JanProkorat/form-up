using FluentAssertions;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FormUp.Tests.Domain;

/// <summary>
/// Shared-container Postgres fixture for <see cref="PhotoDiaryRequestTests"/> (#1104 Phase B —
/// see <see cref="SharedTestContainers"/>): creates its own database inside the one shared
/// server instead of its own container, then migrates it — safe without an explicit
/// per-fact reset because every fact seeds its own rows under fresh <c>Guid.NewGuid()</c> ids.
/// </summary>
public class PhotoDiaryRequestContainerFixture(SharedTestContainers sharedContainers) : IAsyncLifetime
{
    public string ConnectionString { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        ConnectionString = await sharedContainers.CreatePostgresDatabaseAsync("photo_diary_request");

        await using var db = BuildContext(ConnectionString);
        await db.Database.MigrateAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public static ApplicationDbContext BuildContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .ConfigureWarnings(w =>
                w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new ApplicationDbContext(options);
    }
}

[CollectionDefinition("PhotoDiaryRequest")]
public class PhotoDiaryRequestCollection : ICollectionFixture<PhotoDiaryRequestContainerFixture>;

/// <summary>
/// Testcontainers integration tests for the <see cref="PhotoDiaryRequest"/> entity.
/// Covers:
/// <list type="bullet">
///   <item>Insert with correct defaults and round-trip read-back.</item>
///   <item>CHECK constraint violations rejected by Postgres.</item>
///   <item>Status transition roundtrip: Pending → Accepted → InProgress → Completed.</item>
///   <item>Dismissed path with reason.</item>
/// </list>
/// </summary>
[Collection("PhotoDiaryRequest")]
public class PhotoDiaryRequestTests(PhotoDiaryRequestContainerFixture containerFixture) : IAsyncLifetime
{
    private string ConnectionString => containerFixture.ConnectionString;

    private ApplicationDbContext _db = null!;

    // ── Lifecycle ────────────────────────────────────────────────────────────

    public ValueTask InitializeAsync()
    {
        _db = BuildContext(ConnectionString);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
    }

    // ── Context factory ──────────────────────────────────────────────────────

    private static ApplicationDbContext BuildContext(string connectionString) =>
        PhotoDiaryRequestContainerFixture.BuildContext(connectionString);

    private async Task RebuildContextAsync()
    {
        await _db.DisposeAsync();
        _db = BuildContext(ConnectionString);
    }

    // ── Seed helpers ─────────────────────────────────────────────────────────

    /// <summary>Creates a minimal ApplicationUser row directly via SQL (bypasses Identity).</summary>
    private async Task<Guid> CreateUserAsync()
    {
        var userId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO users (
                id, user_name, normalized_user_name,
                email, normalized_email, email_confirmed,
                password_hash, security_stamp, concurrency_stamp,
                phone_number_confirmed, two_factor_enabled,
                lockout_enabled, access_failed_count,
                first_name, last_name, is_active, date_created,
                gdpr_consent, verification_emails_sent, time_zone
            ) VALUES (
                @id, @email, @emailUpper,
                @email, @emailUpper, true,
                '', gen_random_uuid()::text, gen_random_uuid()::text,
                false, false,
                true, 0,
                'Test', 'User', true, now(),
                true, 0, 'Europe/Prague'
            )";
        cmd.Parameters.AddWithValue("id", userId);
        cmd.Parameters.AddWithValue("email", $"{userId:N}@diary-test.com");
        cmd.Parameters.AddWithValue("emailUpper", $"{userId:N}@DIARY-TEST.COM");
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return userId;
    }

    /// <summary>Creates a ProfessionalProfile row and returns its internal id.</summary>
    private async Task<long> CreateProfessionalProfileAsync(Guid userId)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO professional_profiles
                (user_id, public_id, date_created)
            VALUES
                (@userId, @publicId, now())
            RETURNING id";
        cmd.Parameters.AddWithValue("userId", userId);
        cmd.Parameters.AddWithValue("publicId", Guid.NewGuid());
        return (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    /// <summary>Creates a ClientProfile row and returns its internal id.</summary>
    private async Task<long> CreateClientProfileAsync(Guid userId)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO client_profiles
                (user_id, public_id, date_created, is_onboarding_complete)
            VALUES
                (@userId, @publicId, now(), false)
            RETURNING id";
        cmd.Parameters.AddWithValue("userId", userId);
        cmd.Parameters.AddWithValue("publicId", Guid.NewGuid());
        return (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    /// <summary>Creates a ClientProfessionalLink and returns its internal id.</summary>
    private async Task<long> CreateLinkAsync(long clientProfileId, long professionalProfileId)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = conn.CreateCommand();
        // professional_role is stored as integer: Nutritionist = 2; connection_state 0 = Connected
        cmd.CommandText = @"
            INSERT INTO client_professional_links
                (client_profile_id, professional_profile_id, professional_role,
                 connection_state, can_view_nutrition_plans, can_view_training_plans,
                 public_id, date_created)
            VALUES
                (@clientProfileId, @professionalProfileId, 2,
                 0, true, false,
                 @publicId, now())
            RETURNING id";
        cmd.Parameters.AddWithValue("clientProfileId", clientProfileId);
        cmd.Parameters.AddWithValue("professionalProfileId", professionalProfileId);
        cmd.Parameters.AddWithValue("publicId", Guid.NewGuid());
        return (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    /// <summary>Creates a PendingInvite row and returns its internal id.</summary>
    private async Task<long> CreatePendingInviteAsync(long professionalProfileId)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO pending_invites
                (professional_profile_id, email,
                 sent_at, status, public_id, date_created)
            VALUES
                (@professionalProfileId, @email,
                 now(), 0, @publicId, now())
            RETURNING id";
        cmd.Parameters.AddWithValue("professionalProfileId", professionalProfileId);
        cmd.Parameters.AddWithValue("email", $"{Guid.NewGuid():N}@invite-test.com");
        cmd.Parameters.AddWithValue("publicId", Guid.NewGuid());
        return (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    // ── Helper: insert a valid pending request directly via EF ───────────────

    private async Task<(Guid requestId, Guid professionalUserId, long linkId)>
        CreatePendingRequestViaLinkAsync()
    {
        var ct = TestContext.Current.CancellationToken;

        var profUserId = await CreateUserAsync();
        var clientUserId = await CreateUserAsync();
        var profProfileId = await CreateProfessionalProfileAsync(profUserId);
        var clientProfileId = await CreateClientProfileAsync(clientUserId);
        var linkId = await CreateLinkAsync(clientProfileId, profProfileId);

        var requestId = Guid.NewGuid();
        var request = new PhotoDiaryRequest
        {
            Id = requestId,
            ProfessionalId = profUserId,
            LinkId = linkId,
            PendingInviteId = null,
            DurationDays = 7,
            Status = PhotoDiaryStatus.Pending,
            Mode = null,
            DismissReason = null,
            AcceptedAt = null,
            CompletedAt = null,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        _db.PhotoDiaryRequests.Add(request);
        await _db.SaveChangesAsync(ct);

        return (requestId, profUserId, linkId);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Insert_PendingRequest_ReadsBackWithCorrectDefaults()
    {
        var ct = TestContext.Current.CancellationToken;
        var (requestId, _, _) = await CreatePendingRequestViaLinkAsync();

        await RebuildContextAsync();

        var loaded = await _db.PhotoDiaryRequests
            .AsNoTracking()
            .SingleAsync(r => r.Id == requestId, ct);

        loaded.Status.Should().Be(PhotoDiaryStatus.Pending);
        loaded.DurationDays.Should().Be(7);
        loaded.Mode.Should().BeNull("mode is not set on a pending request");
        loaded.DismissReason.Should().BeNull();
        loaded.AcceptedAt.Should().BeNull();
        loaded.CompletedAt.Should().BeNull();
        loaded.PendingInviteId.Should().BeNull();
    }

    [Fact]
    public async Task CheckConstraint_BothLinkAndInvite_Rejected()
    {
        var ct = TestContext.Current.CancellationToken;

        var profUserId = await CreateUserAsync();
        var clientUserId = await CreateUserAsync();
        var profProfileId = await CreateProfessionalProfileAsync(profUserId);
        var clientProfileId = await CreateClientProfileAsync(clientUserId);
        var linkId = await CreateLinkAsync(clientProfileId, profProfileId);
        var inviteId = await CreatePendingInviteAsync(profProfileId);

        var request = new PhotoDiaryRequest
        {
            Id = Guid.NewGuid(),
            ProfessionalId = profUserId,
            LinkId = linkId,
            PendingInviteId = inviteId, // Both set — violates XOR constraint
            Status = PhotoDiaryStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        _db.PhotoDiaryRequests.Add(request);

        var act = async () => await _db.SaveChangesAsync(ct);
        await act.Should().ThrowAsync<Exception>(
            "setting both LinkId and PendingInviteId violates ck_photo_diary_requests_link_xor_invite");
    }

    [Fact]
    public async Task CheckConstraint_NeitherLinkNorInvite_Rejected()
    {
        var ct = TestContext.Current.CancellationToken;

        var profUserId = await CreateUserAsync();
        await CreateProfessionalProfileAsync(profUserId);

        var request = new PhotoDiaryRequest
        {
            Id = Guid.NewGuid(),
            ProfessionalId = profUserId,
            LinkId = null,
            PendingInviteId = null, // Neither set — violates XOR constraint
            Status = PhotoDiaryStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        _db.PhotoDiaryRequests.Add(request);

        var act = async () => await _db.SaveChangesAsync(ct);
        await act.Should().ThrowAsync<Exception>(
            "setting neither LinkId nor PendingInviteId violates ck_photo_diary_requests_link_xor_invite");
    }

    [Fact]
    public async Task CheckConstraint_AcceptedStatus_RequiresMode()
    {
        var ct = TestContext.Current.CancellationToken;

        var profUserId = await CreateUserAsync();
        var clientUserId = await CreateUserAsync();
        var profProfileId = await CreateProfessionalProfileAsync(profUserId);
        var clientProfileId = await CreateClientProfileAsync(clientUserId);
        var linkId = await CreateLinkAsync(clientProfileId, profProfileId);

        var request = new PhotoDiaryRequest
        {
            Id = Guid.NewGuid(),
            ProfessionalId = profUserId,
            LinkId = linkId,
            Status = PhotoDiaryStatus.Accepted,
            Mode = null, // Accepted without Mode — violates constraint
            AcceptedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        _db.PhotoDiaryRequests.Add(request);

        var act = async () => await _db.SaveChangesAsync(ct);
        await act.Should().ThrowAsync<Exception>(
            "Accepted status without Mode violates ck_photo_diary_requests_mode_with_accepted_status");
    }

    [Fact]
    public async Task CheckConstraint_PendingStatus_WithModeAndMessage_Accepted()
    {
        var ct = TestContext.Current.CancellationToken;

        var profUserId = await CreateUserAsync();
        var clientUserId = await CreateUserAsync();
        var profProfileId = await CreateProfessionalProfileAsync(profUserId);
        var clientProfileId = await CreateClientProfileAsync(clientUserId);
        var linkId = await CreateLinkAsync(clientProfileId, profProfileId);

        var request = new PhotoDiaryRequest
        {
            Id = Guid.NewGuid(),
            ProfessionalId = profUserId,
            LinkId = linkId,
            Status = PhotoDiaryStatus.Pending,
            Mode = PhotoDiaryMode.Bulk, // The coach picks the mode at creation, so Pending carries one.
            Message = "Please send a photo",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        _db.PhotoDiaryRequests.Add(request);
        await _db.SaveChangesAsync(ct);

        await RebuildContextAsync();
        var loaded = await _db.PhotoDiaryRequests.AsNoTracking().SingleAsync(r => r.Id == request.Id, ct);
        loaded.Mode.Should().Be(PhotoDiaryMode.Bulk);
        loaded.Message.Should().Be("Please send a photo");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CheckConstraint_CancelledStatus_AcceptedAtIsFree(bool withAcceptedAt)
    {
        var ct = TestContext.Current.CancellationToken;

        var profUserId = await CreateUserAsync();
        var clientUserId = await CreateUserAsync();
        var profProfileId = await CreateProfessionalProfileAsync(profUserId);
        var clientProfileId = await CreateClientProfileAsync(clientUserId);
        var linkId = await CreateLinkAsync(clientProfileId, profProfileId);

        _db.PhotoDiaryRequests.Add(new PhotoDiaryRequest
        {
            Id = Guid.NewGuid(),
            ProfessionalId = profUserId,
            LinkId = linkId,
            Status = PhotoDiaryStatus.Cancelled,
            Mode = PhotoDiaryMode.Workflow,
            AcceptedAt = withAcceptedAt ? DateTimeOffset.UtcNow : null,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        var act = async () => await _db.SaveChangesAsync(ct);
        await act.Should().NotThrowAsync("a Cancelled request may keep or lack accepted_at");
    }

    [Fact]
    public async Task CheckConstraint_DismissedStatus_WithAcceptedAt_Rejected()
    {
        var ct = TestContext.Current.CancellationToken;

        var profUserId = await CreateUserAsync();
        var clientUserId = await CreateUserAsync();
        var profProfileId = await CreateProfessionalProfileAsync(profUserId);
        var clientProfileId = await CreateClientProfileAsync(clientUserId);
        var linkId = await CreateLinkAsync(clientProfileId, profProfileId);

        _db.PhotoDiaryRequests.Add(new PhotoDiaryRequest
        {
            Id = Guid.NewGuid(),
            ProfessionalId = profUserId,
            LinkId = linkId,
            Status = PhotoDiaryStatus.Dismissed,
            Mode = PhotoDiaryMode.Workflow,
            AcceptedAt = DateTimeOffset.UtcNow, // Dismissed requests were never accepted
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        var act = async () => await _db.SaveChangesAsync(ct);
        await act.Should().ThrowAsync<Exception>(
            "AcceptedAt on Dismissed violates ck_photo_diary_requests_accepted_at_with_accepted_status");
    }

    [Fact]
    public void EndsAt_IsCreatedAtPlusDurationDays_AndIsOpenAtHonoursStatusAndEndDate()
    {
        var created = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
        var request = new PhotoDiaryRequest { CreatedAt = created, DurationDays = 7, Status = PhotoDiaryStatus.Pending };

        request.EndsAt.Should().Be(created.AddDays(7));
        request.IsOpenAt(created.AddDays(7).AddTicks(-1)).Should().BeTrue();
        request.IsOpenAt(created.AddDays(7)).Should().BeFalse("the window ends at CreatedAt + DurationDays");

        request.Status = PhotoDiaryStatus.Cancelled;
        request.IsOpenAt(created).Should().BeFalse();
    }

    [Fact]
    public async Task CheckConstraint_DismissReason_WithoutDismissedStatus_Rejected()
    {
        var ct = TestContext.Current.CancellationToken;

        var profUserId = await CreateUserAsync();
        var clientUserId = await CreateUserAsync();
        var profProfileId = await CreateProfessionalProfileAsync(profUserId);
        var clientProfileId = await CreateClientProfileAsync(clientUserId);
        var linkId = await CreateLinkAsync(clientProfileId, profProfileId);

        var request = new PhotoDiaryRequest
        {
            Id = Guid.NewGuid(),
            ProfessionalId = profUserId,
            LinkId = linkId,
            Status = PhotoDiaryStatus.Pending,
            Mode = null,
            DismissReason = "Not interested", // DismissReason on Pending — violates constraint
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        _db.PhotoDiaryRequests.Add(request);

        var act = async () => await _db.SaveChangesAsync(ct);
        await act.Should().ThrowAsync<Exception>(
            "DismissReason on Pending status violates ck_photo_diary_requests_dismiss_reason_only_when_dismissed");
    }

    [Fact]
    public async Task CheckConstraint_AcceptedAtWithoutAcceptedStatus_Rejected()
    {
        var ct = TestContext.Current.CancellationToken;

        var profUserId = await CreateUserAsync();
        var clientUserId = await CreateUserAsync();
        var profProfileId = await CreateProfessionalProfileAsync(profUserId);
        var clientProfileId = await CreateClientProfileAsync(clientUserId);
        var linkId = await CreateLinkAsync(clientProfileId, profProfileId);

        var request = new PhotoDiaryRequest
        {
            Id = Guid.NewGuid(),
            ProfessionalId = profUserId,
            LinkId = linkId,
            Status = PhotoDiaryStatus.Pending,
            Mode = null,
            AcceptedAt = DateTimeOffset.UtcNow, // AcceptedAt set on Pending — violates constraint
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        _db.PhotoDiaryRequests.Add(request);

        var act = async () => await _db.SaveChangesAsync(ct);
        await act.Should().ThrowAsync<Exception>(
            "AcceptedAt set on Pending status violates ck_photo_diary_requests_accepted_at_with_accepted_status");
    }

    [Fact]
    public async Task CheckConstraint_CompletedAtWithoutCompletedStatus_Rejected()
    {
        var ct = TestContext.Current.CancellationToken;

        var profUserId = await CreateUserAsync();
        var clientUserId = await CreateUserAsync();
        var profProfileId = await CreateProfessionalProfileAsync(profUserId);
        var clientProfileId = await CreateClientProfileAsync(clientUserId);
        var linkId = await CreateLinkAsync(clientProfileId, profProfileId);

        var request = new PhotoDiaryRequest
        {
            Id = Guid.NewGuid(),
            ProfessionalId = profUserId,
            LinkId = linkId,
            Status = PhotoDiaryStatus.Accepted,
            Mode = PhotoDiaryMode.Workflow,
            AcceptedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow, // CompletedAt on Accepted — violates constraint
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        _db.PhotoDiaryRequests.Add(request);

        var act = async () => await _db.SaveChangesAsync(ct);
        await act.Should().ThrowAsync<Exception>(
            "CompletedAt set on Accepted status violates ck_photo_diary_requests_completed_at_only_when_completed");
    }

    [Fact]
    public async Task CheckConstraint_DurationDays_OutOfRange_Rejected()
    {
        var ct = TestContext.Current.CancellationToken;

        var profUserId = await CreateUserAsync();
        var clientUserId = await CreateUserAsync();
        var profProfileId = await CreateProfessionalProfileAsync(profUserId);
        var clientProfileId = await CreateClientProfileAsync(clientUserId);
        var linkId = await CreateLinkAsync(clientProfileId, profProfileId);

        var request = new PhotoDiaryRequest
        {
            Id = Guid.NewGuid(),
            ProfessionalId = profUserId,
            LinkId = linkId,
            Status = PhotoDiaryStatus.Pending,
            DurationDays = 31, // Out of range — violates ck_photo_diary_requests_duration_days_range
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        _db.PhotoDiaryRequests.Add(request);

        var act = async () => await _db.SaveChangesAsync(ct);
        await act.Should().ThrowAsync<Exception>(
            "DurationDays=31 violates ck_photo_diary_requests_duration_days_range");
    }

    [Fact]
    public async Task StatusTransition_Pending_Accepted_InProgress_Completed_RoundTrip()
    {
        var ct = TestContext.Current.CancellationToken;
        var (requestId, _, _) = await CreatePendingRequestViaLinkAsync();

        // ── Accept ────────────────────────────────────────────────────────────
        var acceptedAt = DateTimeOffset.UtcNow;
        var request = await _db.PhotoDiaryRequests.SingleAsync(r => r.Id == requestId, ct);
        request.Status = PhotoDiaryStatus.Accepted;
        request.Mode = PhotoDiaryMode.Workflow;
        request.AcceptedAt = acceptedAt;
        request.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        await RebuildContextAsync();
        var accepted = await _db.PhotoDiaryRequests.AsNoTracking()
            .SingleAsync(r => r.Id == requestId, ct);
        accepted.Status.Should().Be(PhotoDiaryStatus.Accepted);
        accepted.Mode.Should().Be(PhotoDiaryMode.Workflow);
        accepted.AcceptedAt.Should().BeCloseTo(acceptedAt, TimeSpan.FromSeconds(1));
        accepted.CompletedAt.Should().BeNull();

        // ── Transition to InProgress ──────────────────────────────────────────
        request = await _db.PhotoDiaryRequests.SingleAsync(r => r.Id == requestId, ct);
        request.Status = PhotoDiaryStatus.InProgress;
        request.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        await RebuildContextAsync();
        var inProgress = await _db.PhotoDiaryRequests.AsNoTracking()
            .SingleAsync(r => r.Id == requestId, ct);
        inProgress.Status.Should().Be(PhotoDiaryStatus.InProgress);
        inProgress.Mode.Should().Be(PhotoDiaryMode.Workflow, "mode must persist across status transitions");
        inProgress.AcceptedAt.Should().NotBeNull();
        inProgress.CompletedAt.Should().BeNull();

        // ── Complete ──────────────────────────────────────────────────────────
        var completedAt = DateTimeOffset.UtcNow;
        request = await _db.PhotoDiaryRequests.SingleAsync(r => r.Id == requestId, ct);
        request.Status = PhotoDiaryStatus.Completed;
        request.CompletedAt = completedAt;
        request.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        await RebuildContextAsync();
        var completed = await _db.PhotoDiaryRequests.AsNoTracking()
            .SingleAsync(r => r.Id == requestId, ct);
        completed.Status.Should().Be(PhotoDiaryStatus.Completed);
        completed.Mode.Should().Be(PhotoDiaryMode.Workflow);
        completed.AcceptedAt.Should().NotBeNull();
        completed.CompletedAt.Should().BeCloseTo(completedAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task StatusTransition_Pending_Dismissed_WithReason_Persists()
    {
        var ct = TestContext.Current.CancellationToken;
        var (requestId, _, _) = await CreatePendingRequestViaLinkAsync();

        const string reason = "I prefer not to share photos right now.";

        var request = await _db.PhotoDiaryRequests.SingleAsync(r => r.Id == requestId, ct);
        request.Status = PhotoDiaryStatus.Dismissed;
        request.DismissReason = reason;
        request.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        await RebuildContextAsync();
        var dismissed = await _db.PhotoDiaryRequests.AsNoTracking()
            .SingleAsync(r => r.Id == requestId, ct);
        dismissed.Status.Should().Be(PhotoDiaryStatus.Dismissed);
        dismissed.DismissReason.Should().Be(reason);
        dismissed.Mode.Should().BeNull("mode is not set when dismissed");
        dismissed.AcceptedAt.Should().BeNull();
        dismissed.CompletedAt.Should().BeNull();
    }

    [Fact]
    public async Task Insert_ViaInvite_ReadsBackWithCorrectLinkFields()
    {
        var ct = TestContext.Current.CancellationToken;

        var profUserId = await CreateUserAsync();
        var profProfileId = await CreateProfessionalProfileAsync(profUserId);
        var inviteId = await CreatePendingInviteAsync(profProfileId);

        var requestId = Guid.NewGuid();
        var request = new PhotoDiaryRequest
        {
            Id = requestId,
            ProfessionalId = profUserId,
            LinkId = null,
            PendingInviteId = inviteId,
            DurationDays = 14,
            Status = PhotoDiaryStatus.Pending,
            Mode = null,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        _db.PhotoDiaryRequests.Add(request);
        await _db.SaveChangesAsync(ct);

        await RebuildContextAsync();

        var loaded = await _db.PhotoDiaryRequests.AsNoTracking()
            .SingleAsync(r => r.Id == requestId, ct);

        loaded.PendingInviteId.Should().Be(inviteId);
        loaded.LinkId.Should().BeNull();
        loaded.DurationDays.Should().Be(14);
        loaded.Status.Should().Be(PhotoDiaryStatus.Pending);
    }
}
