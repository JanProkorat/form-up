using Microsoft.EntityFrameworkCore;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;

namespace FormUp.Tests.Builders;

/// <summary>
/// Entry point for entity test builders.
/// </summary>
public static class EntityBuilder
{
    /// <summary>
    /// Creates a new <see cref="ApplicationUserBuilder"/>.
    /// </summary>
    public static ApplicationUserBuilder User => new();

    /// <summary>
    /// Creates a new <see cref="ProfessionalProfileBuilder"/>.
    /// </summary>
    public static ProfessionalProfileBuilder ProfessionalProfile => new();

    /// <summary>
    /// Creates a new <see cref="ClientProfileBuilder"/>.
    /// </summary>
    public static ClientProfileBuilder ClientProfile => new();

    /// <summary>
    /// Creates a new <see cref="ClientProfessionalLinkBuilder"/>.
    /// </summary>
    public static ClientProfessionalLinkBuilder ClientProfessionalLink => new();

    /// <summary>
    /// Creates a new <see cref="RefreshTokenBuilder"/>.
    /// </summary>
    public static RefreshTokenBuilder RefreshToken => new();

    /// <summary>
    /// Creates a new <see cref="InvitationTokenBuilder"/>.
    /// </summary>
    public static InvitationTokenBuilder InvitationToken => new();

    /// <summary>
    /// Creates a new <see cref="ClientOnboardingDataBuilder"/>.
    /// </summary>
    public static ClientOnboardingDataBuilder ClientOnboardingData => new();

    /// <summary>
    /// Creates a new <see cref="ClientNutritionTargetsBuilder"/>.
    /// </summary>
    public static ClientNutritionTargetsBuilder ClientNutritionTargets => new();

    /// <summary>
    /// Creates a new <see cref="PendingInviteBuilder"/>.
    /// </summary>
    public static PendingInviteBuilder PendingInvite => new();

    /// <summary>
    /// Creates a new <see cref="PlanPhotoBuilder"/>.
    /// </summary>
    public static PlanPhotoBuilder PlanPhoto => new();

    /// <summary>
    /// Creates a new <see cref="PlanPhotoViewerBuilder"/>.
    /// </summary>
    public static PlanPhotoViewerBuilder PlanPhotoViewer => new();

    /// <summary>
    /// Creates a new <see cref="QuestionnaireBuilder"/>.
    /// </summary>
    public static QuestionnaireBuilder Questionnaire => new();

    /// <summary>
    /// Creates a new <see cref="QuestionnaireResponseBuilder"/>.
    /// </summary>
    public static QuestionnaireResponseBuilder QuestionnaireResponse => new();
}

/// <summary>
/// Builder for onboarding <see cref="Questionnaire"/> test entities.
/// </summary>
public class QuestionnaireBuilder
{
    private Guid _professionalUserId = Guid.NewGuid();
    private string _title = "Onboarding";
    private readonly List<QuestionnaireQuestion> _questions = [];

    /// <summary>
    /// Sets the coach (<c>ApplicationUser.Id</c>) who owns the questionnaire.
    /// </summary>
    public QuestionnaireBuilder WithProfessionalUserId(Guid id) { _professionalUserId = id; return this; }

    /// <summary>
    /// Sets the questionnaire title.
    /// </summary>
    public QuestionnaireBuilder WithTitle(string title) { _title = title; return this; }

    /// <summary>
    /// Adds a question; questions keep the order they are added in.
    /// </summary>
    public QuestionnaireBuilder WithQuestion(string type, string label)
    {
        _questions.Add(new QuestionnaireQuestion
        {
            PublicId = Guid.NewGuid(),
            OrderIndex = _questions.Count,
            Type = type,
            Label = label,
        });
        return this;
    }

    /// <summary>
    /// Builds an active, default onboarding questionnaire.
    /// </summary>
    public Questionnaire Build() => new()
    {
        PublicId = Guid.NewGuid(),
        ProfessionalId = _professionalUserId,
        Title = _title,
        IsDefault = true,
        IsActive = true,
        Type = QuestionnaireType.Onboarding,
        DateCreated = DateTime.UtcNow,
        Questions = _questions,
    };
}

/// <summary>
/// Builder for <see cref="QuestionnaireResponse"/> test entities.
/// </summary>
public class QuestionnaireResponseBuilder
{
    private long _questionnaireId;
    private long _linkId;
    private Guid _clientUserId = Guid.NewGuid();
    private Guid _professionalUserId = Guid.NewGuid();
    private QuestionnaireResponseStatus _status = QuestionnaireResponseStatus.Pending;
    private DateTime? _submittedAt;
    private QuestionnaireResponseKind _kind = QuestionnaireResponseKind.Onboarding;
    private DateOnly? _checkInWeekStart;
    private DateTime? _checkInDueAtUtc;
    private DateTime? _reviewedAtUtc;
    private readonly List<QuestionnaireAnswer> _answers = [];

    /// <summary>
    /// Sets the questionnaire (internal id) being answered.
    /// </summary>
    public QuestionnaireResponseBuilder ForQuestionnaire(long questionnaireId) { _questionnaireId = questionnaireId; return this; }

    /// <summary>
    /// Sets the client-professional link (internal id) the response belongs to.
    /// </summary>
    public QuestionnaireResponseBuilder OnLink(long linkId) { _linkId = linkId; return this; }

    /// <summary>
    /// Sets the client (<c>ApplicationUser.Id</c>) who answers.
    /// </summary>
    public QuestionnaireResponseBuilder FromClient(Guid clientUserId) { _clientUserId = clientUserId; return this; }

    /// <summary>
    /// Sets the coach (<c>ApplicationUser.Id</c>) who sent the questionnaire.
    /// </summary>
    public QuestionnaireResponseBuilder FromProfessional(Guid professionalUserId) { _professionalUserId = professionalUserId; return this; }

    /// <summary>
    /// Sets the response status and, for a submitted response, when it was submitted.
    /// </summary>
    public QuestionnaireResponseBuilder WithStatus(QuestionnaireResponseStatus status, DateTime? submittedAt = null)
    {
        _status = status;
        _submittedAt = submittedAt;
        return this;
    }

    /// <summary>
    /// Sets the response kind (defaults to onboarding).
    /// </summary>
    public QuestionnaireResponseBuilder WithKind(QuestionnaireResponseKind kind) { _kind = kind; return this; }

    /// <summary>
    /// Marks the response as a check-in for the given week with its deadline.
    /// </summary>
    public QuestionnaireResponseBuilder AsCheckIn(DateOnly weekStart, DateTime dueAtUtc)
    {
        _kind = QuestionnaireResponseKind.CheckIn;
        _checkInWeekStart = weekStart;
        _checkInDueAtUtc = dueAtUtc;
        return this;
    }

    /// <summary>
    /// Records that the coach already opened the response.
    /// </summary>
    public QuestionnaireResponseBuilder ReviewedAt(DateTime reviewedAtUtc) { _reviewedAtUtc = reviewedAtUtc; return this; }

    /// <summary>
    /// Adds a text answer to the given question (internal id).
    /// </summary>
    public QuestionnaireResponseBuilder WithTextAnswer(long questionId, string text)
    {
        _answers.Add(new QuestionnaireAnswer { PublicId = Guid.NewGuid(), QuestionId = questionId, ValueText = text });
        return this;
    }

    /// <summary>
    /// Builds the <see cref="QuestionnaireResponse"/> instance. DateCreated is stamped on insert.
    /// </summary>
    public QuestionnaireResponse Build() => new()
    {
        PublicId = Guid.NewGuid(),
        QuestionnaireId = _questionnaireId,
        ClientId = _clientUserId,
        ProfessionalId = _professionalUserId,
        LinkId = _linkId,
        Status = _status,
        SubmittedAt = _submittedAt,
        Kind = _kind,
        CheckInWeekStart = _checkInWeekStart,
        CheckInDueAtUtc = _checkInDueAtUtc,
        ReviewedByCoachAtUtc = _reviewedAtUtc,
        Answers = _answers,
    };
}

/// <summary>
/// Builder for plan-less <see cref="PlanPhoto"/> test entities (a free-form progress photo by default).
/// </summary>
public class PlanPhotoBuilder
{
    private long _clientProfileId;
    private Guid _uploadedByUserId = Guid.NewGuid();
    private PlanPhotoCategory _category = PlanPhotoCategory.FreeForm;
    private Guid? _diaryRequestId;
    private DateTime _createdAt = DateTime.UtcNow;

    /// <summary>
    /// Sets the owning client profile (internal id) and the uploading client's user id.
    /// </summary>
    public PlanPhotoBuilder ForClient(long clientProfileId, Guid clientUserId)
    {
        _clientProfileId = clientProfileId;
        _uploadedByUserId = clientUserId;
        return this;
    }

    /// <summary>
    /// Sets the photo category.
    /// </summary>
    public PlanPhotoBuilder WithCategory(PlanPhotoCategory category) { _category = category; return this; }

    /// <summary>
    /// Links the photo to a diary request, making it an answer to a coach's request.
    /// </summary>
    public PlanPhotoBuilder AnsweringRequest(Guid diaryRequestId) { _diaryRequestId = diaryRequestId; return this; }

    /// <summary>
    /// Sets when the photo was uploaded (UTC).
    /// </summary>
    public PlanPhotoBuilder CreatedAt(DateTime createdAt) { _createdAt = createdAt; return this; }

    /// <summary>
    /// Builds the <see cref="PlanPhoto"/> instance.
    /// </summary>
    public PlanPhoto Build() => new()
    {
        PublicId = Guid.NewGuid(),
        ClientProfileId = _clientProfileId,
        Category = _category,
        BlobUrl = $"plan-photos/unlinked/{_uploadedByUserId}/{Guid.NewGuid()}.jpg",
        TakenAt = _createdAt,
        UploadedByUserId = _uploadedByUserId,
        DiaryRequestId = _diaryRequestId,
        DateCreated = _createdAt,
        DateUpdated = _createdAt,
    };
}

/// <summary>
/// Builder for <see cref="PlanPhotoViewer"/> test entities.
/// </summary>
public class PlanPhotoViewerBuilder
{
    private PlanPhoto? _photo;
    private Guid _professionalUserId = Guid.NewGuid();

    /// <summary>
    /// Sets the photo this row grants access to.
    /// </summary>
    public PlanPhotoViewerBuilder ForPhoto(PlanPhoto photo) { _photo = photo; return this; }

    /// <summary>
    /// Sets the coach (<c>ApplicationUser.Id</c>) who may see the photo.
    /// </summary>
    public PlanPhotoViewerBuilder WithProfessionalUserId(Guid id) { _professionalUserId = id; return this; }

    /// <summary>
    /// Builds the <see cref="PlanPhotoViewer"/> instance.
    /// </summary>
    public PlanPhotoViewer Build() => new()
    {
        PlanPhotoId = _photo!.Id,
        PlanPhoto = _photo,
        ProfessionalUserId = _professionalUserId,
        DateCreated = DateTime.UtcNow,
    };
}

/// <summary>
/// Builder for <see cref="ApplicationUser"/> test entities.
/// </summary>
public class ApplicationUserBuilder
{
    private Guid _id = Guid.NewGuid();
    private string _email = "test@test.com";
    private string _firstName = "Test";
    private string _lastName = "User";
    private bool _isActive = true;
    private bool _emailConfirmed;
    private DateTime? _dateCreated;

    /// <summary>
    /// Marks the user's email as verified (defaults to unverified).
    /// </summary>
    public ApplicationUserBuilder WithEmailConfirmed(bool confirmed = true) { _emailConfirmed = confirmed; return this; }

    /// <summary>
    /// Overrides the account creation timestamp (defaults to now).
    /// </summary>
    public ApplicationUserBuilder WithDateCreated(DateTime dateCreated) { _dateCreated = dateCreated; return this; }

    /// <summary>
    /// Sets the user ID.
    /// </summary>
    public ApplicationUserBuilder WithId(Guid id) { _id = id; return this; }

    /// <summary>
    /// Sets the email and username.
    /// </summary>
    public ApplicationUserBuilder WithEmail(string email) { _email = email; return this; }

    /// <summary>
    /// Sets the first name.
    /// </summary>
    public ApplicationUserBuilder WithFirstName(string fn) { _firstName = fn; return this; }

    /// <summary>
    /// Sets the last name.
    /// </summary>
    public ApplicationUserBuilder WithLastName(string ln) { _lastName = ln; return this; }

    /// <summary>
    /// Marks the user as inactive.
    /// </summary>
    public ApplicationUserBuilder Inactive() { _isActive = false; return this; }

    /// <summary>
    /// Builds the <see cref="ApplicationUser"/> instance.
    /// </summary>
    public ApplicationUser Build() => new()
    {
        Id = _id, Email = _email, UserName = _email,
        FirstName = _firstName, LastName = _lastName,
        IsActive = _isActive, EmailConfirmed = _emailConfirmed,
        DateCreated = _dateCreated ?? DateTime.UtcNow
    };
}

/// <summary>
/// Builder for <see cref="ProfessionalProfile"/> test entities.
/// </summary>
public class ProfessionalProfileBuilder
{
    private long _id = 1;
    private Guid _userId = Guid.NewGuid();
    private Guid _publicId = Guid.NewGuid();
    private string _bio = "Bio";
    private string _specialization = "Spec";
    private ApplicationUser? _user;

    /// <summary>
    /// Sets the internal ID.
    /// </summary>
    public ProfessionalProfileBuilder WithId(long id) { _id = id; return this; }

    /// <summary>
    /// Sets the user ID.
    /// </summary>
    public ProfessionalProfileBuilder WithUserId(Guid userId) { _userId = userId; return this; }

    /// <summary>
    /// Sets the public ID.
    /// </summary>
    public ProfessionalProfileBuilder WithPublicId(Guid publicId) { _publicId = publicId; return this; }

    /// <summary>
    /// Sets the bio.
    /// </summary>
    public ProfessionalProfileBuilder WithBio(string bio) { _bio = bio; return this; }

    /// <summary>
    /// Sets the specialization.
    /// </summary>
    public ProfessionalProfileBuilder WithSpecialization(string spec) { _specialization = spec; return this; }

    /// <summary>
    /// Sets the User navigation property.
    /// </summary>
    public ProfessionalProfileBuilder WithUser(ApplicationUser user) { _user = user; _userId = user.Id; return this; }

    /// <summary>
    /// Builds the <see cref="ProfessionalProfile"/> instance.
    /// </summary>
    public ProfessionalProfile Build() => new()
    {
        Id = _id, UserId = _userId, PublicId = _publicId,
        Bio = _bio, Specialization = _specialization,
        User = _user!
    };
}

/// <summary>
/// Builder for <see cref="ClientProfile"/> test entities.
/// </summary>
public class ClientProfileBuilder
{
    private long _id = 1;
    private Guid _userId = Guid.NewGuid();
    private Guid _publicId = Guid.NewGuid();
    private ApplicationUser? _user;

    /// <summary>
    /// Sets the internal ID.
    /// </summary>
    public ClientProfileBuilder WithId(long id) { _id = id; return this; }

    /// <summary>
    /// Sets the user ID.
    /// </summary>
    public ClientProfileBuilder WithUserId(Guid userId) { _userId = userId; return this; }

    /// <summary>
    /// Sets the public ID.
    /// </summary>
    public ClientProfileBuilder WithPublicId(Guid publicId) { _publicId = publicId; return this; }

    /// <summary>
    /// Sets the User navigation property.
    /// </summary>
    public ClientProfileBuilder WithUser(ApplicationUser user) { _user = user; _userId = user.Id; return this; }

    /// <summary>
    /// Builds the <see cref="ClientProfile"/> instance.
    /// </summary>
    public ClientProfile Build() => new()
    {
        Id = _id, UserId = _userId, PublicId = _publicId,
        User = _user!
    };
}

/// <summary>
/// Builder for <see cref="ClientProfessionalLink"/> test entities.
/// </summary>
public class ClientProfessionalLinkBuilder
{
    private long _id;
    private long _clientProfileId;
    private long _professionalProfileId;
    private UserRole _professionalRole = UserRole.Trainer;
    private bool _isActive = true;
    private bool _canViewNutritionPlans = true;
    private bool _canViewTrainingPlans = true;
    private ClientProfile? _clientProfile;
    private ProfessionalProfile? _professionalProfile;
    private DateTime _dateCreated = DateTime.UtcNow;
    private DateTime? _activatedAtUtc;

    /// <summary>
    /// Sets when the current collaboration began (defaults to null, a legacy link).
    /// </summary>
    public ClientProfessionalLinkBuilder WithActivatedAtUtc(DateTime activatedAtUtc) { _activatedAtUtc = activatedAtUtc; return this; }

    /// <summary>
    /// Sets the internal ID.
    /// </summary>
    public ClientProfessionalLinkBuilder WithId(long id) { _id = id; return this; }

    /// <summary>
    /// Sets the client profile ID.
    /// </summary>
    public ClientProfessionalLinkBuilder WithClientProfileId(long id) { _clientProfileId = id; return this; }

    /// <summary>
    /// Sets the professional profile ID.
    /// </summary>
    public ClientProfessionalLinkBuilder WithProfessionalProfileId(long id) { _professionalProfileId = id; return this; }

    /// <summary>
    /// Sets the professional role.
    /// </summary>
    public ClientProfessionalLinkBuilder WithProfessionalRole(UserRole role) { _professionalRole = role; return this; }

    /// <summary>
    /// Sets the link as inactive.
    /// </summary>
    public ClientProfessionalLinkBuilder Inactive() { _isActive = false; return this; }

    /// <summary>
    /// Sets whether this link permits viewing nutrition plans.
    /// </summary>
    public ClientProfessionalLinkBuilder WithCanViewNutritionPlans(bool value) { _canViewNutritionPlans = value; return this; }

    /// <summary>
    /// Sets whether this link permits viewing training plans.
    /// </summary>
    public ClientProfessionalLinkBuilder WithCanViewTrainingPlans(bool value) { _canViewTrainingPlans = value; return this; }

    /// <summary>
    /// Sets the ClientProfile navigation property.
    /// </summary>
    public ClientProfessionalLinkBuilder WithClientProfile(ClientProfile cp) { _clientProfile = cp; _clientProfileId = cp.Id; return this; }

    /// <summary>
    /// Sets the ProfessionalProfile navigation property.
    /// </summary>
    public ClientProfessionalLinkBuilder WithProfessionalProfile(ProfessionalProfile pp) { _professionalProfile = pp; _professionalProfileId = pp.Id; return this; }

    /// <summary>
    /// Sets the date created.
    /// </summary>
    public ClientProfessionalLinkBuilder WithDateCreated(DateTime dt) { _dateCreated = dt; return this; }

    /// <summary>
    /// Builds the <see cref="ClientProfessionalLink"/> instance.
    /// </summary>
    public ClientProfessionalLink Build() => new()
    {
        Id = _id,
        ClientProfileId = _clientProfileId, ProfessionalProfileId = _professionalProfileId,
        ProfessionalRole = _professionalRole, ConnectionState = _isActive ? ConnectionState.Connected : ConnectionState.Ended,
        CanViewNutritionPlans = _canViewNutritionPlans,
        CanViewTrainingPlans = _canViewTrainingPlans,
        ClientProfile = _clientProfile!, ProfessionalProfile = _professionalProfile!,
        DateCreated = _dateCreated,
        ActivatedAtUtc = _activatedAtUtc
    };
}

/// <summary>
/// Builder for <see cref="Application.Domain.Entities.RefreshToken"/> test entities.
/// </summary>
public class RefreshTokenBuilder
{
    private Guid _userId = Guid.NewGuid();
    private string _token = "test-token";
    private DateTime _expiresAt = DateTime.UtcNow.AddDays(7);
    private DateTime? _revokedAt;
    private string? _replacedByToken;
    private ApplicationUser? _user;

    /// <summary>
    /// Sets the user ID.
    /// </summary>
    public RefreshTokenBuilder WithUserId(Guid userId) { _userId = userId; return this; }

    /// <summary>
    /// Sets the token string.
    /// </summary>
    public RefreshTokenBuilder WithToken(string token) { _token = token; return this; }

    /// <summary>
    /// Sets the expiration date.
    /// </summary>
    public RefreshTokenBuilder WithExpiresAt(DateTime dt) { _expiresAt = dt; return this; }

    /// <summary>
    /// Marks the token as expired.
    /// </summary>
    public RefreshTokenBuilder Expired() { _expiresAt = DateTime.UtcNow.AddDays(-1); return this; }

    /// <summary>
    /// Marks the token as revoked.
    /// </summary>
    public RefreshTokenBuilder Revoked() { _revokedAt = DateTime.UtcNow.AddMinutes(-5); return this; }

    /// <summary>
    /// Sets an explicit revocation timestamp — used to place a revoked+rotated
    /// token inside or outside the reuse grace window in tests.
    /// </summary>
    public RefreshTokenBuilder WithRevokedAt(DateTime revokedAt) { _revokedAt = revokedAt; return this; }

    /// <summary>
    /// Sets the replacement token string.
    /// </summary>
    public RefreshTokenBuilder WithReplacedByToken(string? token) { _replacedByToken = token; return this; }

    /// <summary>
    /// Sets the User navigation property.
    /// </summary>
    public RefreshTokenBuilder WithUser(ApplicationUser user) { _user = user; _userId = user.Id; return this; }

    /// <summary>
    /// Builds the <see cref="Application.Domain.Entities.RefreshToken"/> instance.
    /// </summary>
    public RefreshToken Build() => new()
    {
        UserId = _userId, Token = _token, ExpiresAt = _expiresAt,
        RevokedAt = _revokedAt, ReplacedByToken = _replacedByToken,
        User = _user!
    };
}

/// <summary>
/// Builder for <see cref="Application.Domain.Entities.InvitationToken"/> test entities.
/// </summary>
public class InvitationTokenBuilder
{
    private long _professionalProfileId = 1;
    private string _email = "client@test.com";
    private string _token = "invite-token";
    private DateTime _expiresAt = DateTime.UtcNow.AddDays(7);
    private bool _isUsed;
    private LinkCapabilityScope? _requestedScope;
    private ProfessionalProfile? _professionalProfile;

    /// <summary>
    /// Sets the professional profile ID.
    /// </summary>
    public InvitationTokenBuilder WithProfessionalProfileId(long id) { _professionalProfileId = id; return this; }

    /// <summary>
    /// Sets the invited email.
    /// </summary>
    public InvitationTokenBuilder WithEmail(string email) { _email = email; return this; }

    /// <summary>
    /// Sets the token string.
    /// </summary>
    public InvitationTokenBuilder WithToken(string token) { _token = token; return this; }

    /// <summary>
    /// Sets the expiration date.
    /// </summary>
    public InvitationTokenBuilder WithExpiresAt(DateTime dt) { _expiresAt = dt; return this; }

    /// <summary>
    /// Marks the token as expired.
    /// </summary>
    public InvitationTokenBuilder Expired() { _expiresAt = DateTime.UtcNow.AddDays(-1); return this; }

    /// <summary>
    /// Marks the token as already used.
    /// </summary>
    public InvitationTokenBuilder Used() { _isUsed = true; return this; }

    /// <summary>
    /// Sets the ProfessionalProfile navigation property.
    /// </summary>
    public InvitationTokenBuilder WithProfessionalProfile(ProfessionalProfile pp) { _professionalProfile = pp; _professionalProfileId = pp.Id; return this; }

    /// <summary>
    /// Sets the requested link capability scope.
    /// </summary>
    public InvitationTokenBuilder WithRequestedScope(LinkCapabilityScope? scope) { _requestedScope = scope; return this; }

    /// <summary>
    /// Builds the <see cref="Application.Domain.Entities.InvitationToken"/> instance.
    /// </summary>
    public InvitationToken Build() => new()
    {
        ProfessionalProfileId = _professionalProfileId, Email = _email, Token = _token,
        ExpiresAt = _expiresAt, IsUsed = _isUsed, RequestedScope = _requestedScope,
        ProfessionalProfile = _professionalProfile!
    };
}

/// <summary>
/// Builder for <see cref="Application.Domain.Entities.PendingInvite"/> test entities.
/// </summary>
public class PendingInviteBuilder
{
    private long _id;
    private long _professionalProfileId = 1;
    private string _email = "invited@test.com";
    private DateTime _sentAt = DateTime.UtcNow;
    private PendingInviteStatus _status = PendingInviteStatus.Pending;
    private LinkCapabilityScope? _requestedScope;
    private ProfessionalProfile? _professionalProfile;

    /// <summary>
    /// Sets the internal ID.
    /// </summary>
    public PendingInviteBuilder WithId(long id) { _id = id; return this; }

    /// <summary>
    /// Sets the professional profile ID.
    /// </summary>
    public PendingInviteBuilder WithProfessionalProfileId(long id) { _professionalProfileId = id; return this; }

    /// <summary>
    /// Sets the email.
    /// </summary>
    public PendingInviteBuilder WithEmail(string email) { _email = email; return this; }

    /// <summary>
    /// Marks the invite as accepted.
    /// </summary>
    public PendingInviteBuilder Accepted() { _status = PendingInviteStatus.Accepted; return this; }

    /// <summary>
    /// Sets the stored status.
    /// </summary>
    public PendingInviteBuilder WithStatus(PendingInviteStatus status) { _status = status; return this; }

    /// <summary>
    /// Sets when the invite was sent.
    /// </summary>
    public PendingInviteBuilder WithSentAt(DateTime sentAt) { _sentAt = sentAt; return this; }

    /// <summary>
    /// Sets the ProfessionalProfile navigation property.
    /// </summary>
    public PendingInviteBuilder WithProfessionalProfile(ProfessionalProfile pp) { _professionalProfile = pp; _professionalProfileId = pp.Id; return this; }

    /// <summary>
    /// Sets the requested link capability scope.
    /// </summary>
    public PendingInviteBuilder WithRequestedScope(LinkCapabilityScope? scope) { _requestedScope = scope; return this; }

    /// <summary>
    /// Builds the <see cref="Application.Domain.Entities.PendingInvite"/> instance.
    /// </summary>
    public Application.Domain.Entities.PendingInvite Build() => new()
    {
        Id = _id,
        ProfessionalProfileId = _professionalProfileId,
        Email = _email,
        SentAt = _sentAt,
        Status = _status,
        RequestedScope = _requestedScope,
        ProfessionalProfile = _professionalProfile!
    };
}

/// <summary>
/// Builder for <see cref="ClientOnboardingData"/> test entities.
/// </summary>
public class ClientOnboardingDataBuilder
{
    private readonly ClientOnboardingData _entity = new()
    {
        Id = 1,
        ClientProfileId = 1,
        DateOfBirth = new DateTime(2000, 1, 1),
        Sex = BiologicalSex.Male,
        HeightCm = 180,
        WeightKg = 80,
        BodyType = BodyType.Mesomorph,
        PrimaryGoal = PrimaryGoal.GainMuscle,
        TimeHorizon = TimeHorizon.SixMonths,
        JobType = JobType.Sedentary,
        SleepHours = 7,
        StressLevel = 3,
        CurrentTrainingFrequency = CurrentTrainingFrequency.Regular,
        DesiredTrainingFrequency = DesiredTrainingFrequency.FourPerWeek,
        FitnessRating = 6,
        GymAccess = GymAccess.Yes,
        PreferredActivities = "strength,cardio",
        Injuries = "none",
        MealsPerDay = MealsPerDay.FourToFive,
        DietaryStyle = DietaryStyle.Standard,
        Allergies = "none",
        DietRating = 3,
        PlanExperience = PlanExperience.TriedFailed,
        PastBlockers = "time,motivation",
        PrimaryMotivation = PrimaryMotivation.Appearance,
    };

    /// <summary>
    /// Sets the client profile ID.
    /// </summary>
    public ClientOnboardingDataBuilder WithClientProfileId(long id) { _entity.ClientProfileId = id; return this; }

    /// <summary>
    /// Prepares the entity for a real database: clears the fixed test Id so one is generated on
    /// insert, and marks the date of birth as UTC as Npgsql requires.
    /// </summary>
    public ClientOnboardingDataBuilder WithGeneratedId()
    {
        _entity.Id = 0;
        _entity.DateOfBirth = DateTime.SpecifyKind(_entity.DateOfBirth, DateTimeKind.Utc);
        return this;
    }

    /// <summary>
    /// Sets the disliked foods.
    /// </summary>
    public ClientOnboardingDataBuilder WithDislikes(params string[] dislikes) { _entity.Dislikes = [.. dislikes]; return this; }

    /// <summary>
    /// Sets the eating habits.
    /// </summary>
    public ClientOnboardingDataBuilder WithHabits(params string[] habits) { _entity.Habits = [.. habits]; return this; }

    /// <summary>
    /// Builds the <see cref="ClientOnboardingData"/> instance.
    /// </summary>
    public ClientOnboardingData Build() => _entity;
}

/// <summary>
/// Builder for <see cref="ClientNutritionTargets"/> test entities.
/// </summary>
public class ClientNutritionTargetsBuilder
{
    private readonly ClientNutritionTargets _entity = new()
    {
        Id = 1,
        ClientOnboardingDataId = 1,
        DerivedActivityLevel = ActivityLevel.ModeratelyActive,
        DerivedNutritionGoal = NutritionGoal.Bulk,
        Bmr = 1800,
        Tdee = 2500,
        AdjustedKcal = 2750,
        ProteinGrams = 206,
        CarbsGrams = 309,
        FatGrams = 76,
    };

    /// <summary>
    /// Sets the client onboarding data ID.
    /// </summary>
    public ClientNutritionTargetsBuilder WithClientOnboardingDataId(long id) { _entity.ClientOnboardingDataId = id; return this; }

    /// <summary>
    /// Clears the fixed test Id so a real database generates one on insert.
    /// </summary>
    public ClientNutritionTargetsBuilder WithGeneratedId() { _entity.Id = 0; return this; }

    /// <summary>
    /// Sets the fibre target in grams.
    /// </summary>
    public ClientNutritionTargetsBuilder WithFibreGrams(decimal? fibreGrams) { _entity.FibreGrams = fibreGrams; return this; }

    /// <summary>
    /// Builds the <see cref="ClientNutritionTargets"/> instance.
    /// </summary>
    public ClientNutritionTargets Build() => _entity;
}

/// <summary>
/// A coach, a client, the live link between them and the coach's check-in form: what a check-in
/// response needs to exist.
/// </summary>
/// <param name="Coach">The professional who sends the check-in.</param>
/// <param name="Client">The client who answers it.</param>
/// <param name="LinkId">Internal id of the link.</param>
/// <param name="QuestionnaireId">Internal id of the coach's check-in form.</param>
public record CheckInScenario(Actor Coach, Actor Client, long LinkId, long QuestionnaireId);

/// <summary>
/// Writes check-in responses (questionnaire responses of kind check-in) straight to the database.
/// </summary>
public static class CheckInFixtures
{
    /// <summary>
    /// Creates a coach (or uses the given one), a client, a live link and a check-in form owned by the coach.
    /// </summary>
    /// <param name="factory">The test host.</param>
    /// <param name="coach">An existing coach; a new trainer when null.</param>
    /// <param name="client">An existing client; a new one when null.</param>
    /// <param name="activatedAtUtc">When the collaboration began; null is a legacy link.</param>
    /// <param name="role">The professional role recorded on the link.</param>
    public static async Task<CheckInScenario> CreateScenarioAsync(
        FitnessApiFactory factory,
        Actor? coach = null,
        Actor? client = null,
        DateTime? activatedAtUtc = null,
        UserRole role = UserRole.Trainer)
    {
        var ct = TestContext.Current.CancellationToken;
        coach ??= await TestActors.Trainer(factory).CreateAsync(ct);
        client ??= await TestActors.Client(factory).CreateAsync(ct);

        var link = TestActors.Link(factory, coach, client).AsRole(role);

        if (activatedAtUtc.HasValue)
        {
            link.WithActivatedAtUtc(activatedAtUtc.Value);
        }

        return await ForLinkAsync(factory, coach, client, await link.CreateAsync(ct));
    }

    /// <summary>
    /// Gives an existing link a check-in form owned by the coach.
    /// </summary>
    /// <param name="factory">The test host.</param>
    /// <param name="coach">The professional on the link.</param>
    /// <param name="client">The client on the link.</param>
    /// <param name="linkId">Internal id of the link.</param>
    public static async Task<CheckInScenario> ForLinkAsync(FitnessApiFactory factory, Actor coach, Actor client, long linkId)
    {
        var ct = TestContext.Current.CancellationToken;

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var form = new Questionnaire
        {
            PublicId = Guid.NewGuid(),
            ProfessionalId = coach.UserId,
            Title = "Weekly check-in",
            IsActive = true,
            Type = QuestionnaireType.CheckIn,
            DateCreated = DateTime.UtcNow,
        };
        db.Questionnaires.Add(form);
        await db.SaveChangesAsync(ct);

        return new CheckInScenario(coach, client, linkId, form.Id);
    }

    /// <summary>
    /// Adds one check-in response and returns its public id.
    /// </summary>
    /// <param name="factory">The test host.</param>
    /// <param name="scenario">The coach, client, link and form.</param>
    /// <param name="weekStart">The Monday the check-in covers.</param>
    /// <param name="dueAtUtc">When the check-in is due.</param>
    /// <param name="status">The response status.</param>
    /// <param name="submittedAt">When it was submitted, for a submitted response.</param>
    /// <param name="reviewedAt">When the coach opened it, if they did.</param>
    public static async Task<Guid> AddCheckInAsync(
        FitnessApiFactory factory,
        CheckInScenario scenario,
        DateOnly weekStart,
        DateTime dueAtUtc,
        QuestionnaireResponseStatus status = QuestionnaireResponseStatus.Pending,
        DateTime? submittedAt = null,
        DateTime? reviewedAt = null)
    {
        var builder = EntityBuilder.QuestionnaireResponse
            .ForQuestionnaire(scenario.QuestionnaireId)
            .OnLink(scenario.LinkId)
            .FromClient(scenario.Client.UserId)
            .FromProfessional(scenario.Coach.UserId)
            .WithStatus(status, submittedAt)
            .AsCheckIn(weekStart, dueAtUtc);

        if (reviewedAt.HasValue)
        {
            builder.ReviewedAt(reviewedAt.Value);
        }

        var response = builder.Build();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.QuestionnaireResponses.Add(response);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return response.PublicId;
    }

    /// <summary>
    /// Adds a check-in response, due yesterday, for an existing link given by user ids; the coach gets a
    /// check-in form on the way. For tests that only hold user ids.
    /// </summary>
    /// <param name="factory">The test host.</param>
    /// <param name="professionalUserId">The coach's user id.</param>
    /// <param name="clientUserId">The client's user id.</param>
    /// <param name="submitted">Whether the client submitted it.</param>
    /// <param name="reviewed">Whether the coach already opened it.</param>
    public static async Task AddForUsersAsync(
        FitnessApiFactory factory, Guid professionalUserId, Guid clientUserId, bool submitted, bool reviewed = false)
    {
        var ct = TestContext.Current.CancellationToken;

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var linkId = await db.ClientProfessionalLinks
            .Where(l => l.ClientProfile.UserId == clientUserId && l.ProfessionalProfile.UserId == professionalUserId)
            .Select(l => l.Id)
            .FirstAsync(ct);
        var form = new Questionnaire
        {
            PublicId = Guid.NewGuid(),
            ProfessionalId = professionalUserId,
            Title = "Weekly check-in",
            IsActive = true,
            Type = QuestionnaireType.CheckIn,
            DateCreated = DateTime.UtcNow,
        };
        db.Questionnaires.Add(form);
        await db.SaveChangesAsync(ct);

        var builder = EntityBuilder.QuestionnaireResponse
            .ForQuestionnaire(form.Id)
            .OnLink(linkId)
            .FromClient(clientUserId)
            .FromProfessional(professionalUserId)
            .WithStatus(
                submitted ? QuestionnaireResponseStatus.Submitted : QuestionnaireResponseStatus.Pending,
                submitted ? DateTime.UtcNow.AddDays(-2) : null)
            .AsCheckIn(Monday(0), DateTime.UtcNow.AddDays(-1));

        if (reviewed)
        {
            builder.ReviewedAt(DateTime.UtcNow.AddDays(-1));
        }

        db.QuestionnaireResponses.Add(builder.Build());
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The Monday of the week that is <paramref name="weeksAgo"/> weeks before today (UTC).</summary>
    /// <param name="weeksAgo">How many weeks back; 0 is this week.</param>
    public static DateOnly Monday(int weeksAgo)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var daysFromMonday = ((int)today.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        return today.AddDays(-daysFromMonday - (7 * weeksAgo));
    }
}

/// <summary>
/// Gives a test plan the open price row every plan has in production.
/// </summary>
public static class SubscriptionPlanPriceBuilder
{
    /// <summary>
    /// Adds an open price row mirroring the plan's current price fields and returns it. The row
    /// is saved together with the plan.
    /// </summary>
    /// <param name="plan">The plan to give a price row.</param>
    public static SubscriptionPlanPrice OpenPriceFor(SubscriptionPlan plan)
    {
        var price = new SubscriptionPlanPrice
        {
            SubscriptionPlanId = 0,
            SubscriptionPlan = plan,
            PriceMinorUnits = plan.PriceMinorUnits,
            SecondRoleAddOnPriceMinorUnits = plan.SecondRoleAddOnPriceMinorUnits,
            Currency = plan.Currency,
            ValidFrom = DateTime.UtcNow,
        };
        plan.Prices.Add(price);
        return price;
    }

    /// <summary>
    /// Makes sure the Free plan exists (price 0, active) — the shared test database only has it
    /// after something ran the seed.
    /// </summary>
    /// <param name="factory">The test host.</param>
    public static async Task EnsureFreePlanAsync(FitnessApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        if (await db.SubscriptionPlans.AnyAsync(p => p.Code == "free", TestContext.Current.CancellationToken))
        {
            return;
        }

        var plan = new SubscriptionPlan
        {
            Code = "free",
            NameCs = "Zdarma",
            NameEn = "Free",
            NameDe = "Kostenlos",
            BothRolesIncluded = true,
            MaxActiveClients = 2,
            PriceMinorUnits = 0,
            Currency = "CZK",
            BillingInterval = BillingInterval.Monthly,
            IsActive = true,
        };
        OpenPriceFor(plan);
        db.SubscriptionPlans.Add(plan);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}

/// <summary>
/// Subscribes a coach to a fresh plan with the given check-in flags.
/// </summary>
public static class CoachSubscriptionBuilder
{
    /// <summary>
    /// Subscribes the coach to an existing plan, on the plan's open price row.
    /// </summary>
    /// <param name="factory">The test host.</param>
    /// <param name="coach">The coach to subscribe.</param>
    /// <param name="planCode">Code of the plan to subscribe to.</param>
    /// <param name="status">The subscription status.</param>
    /// <param name="freeUntil">While now is before this instant the plan's features apply whatever the status is.</param>
    /// <param name="paysSecondRoleAddOn">True when the coach pays for the second-role add-on.</param>
    public static async Task SubscribeToPlanAsync(
        FitnessApiFactory factory,
        Actor coach,
        string planCode,
        SubscriptionStatus status = SubscriptionStatus.Active,
        DateTimeOffset? freeUntil = null,
        bool paysSecondRoleAddOn = false)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var price = await db.SubscriptionPlanPrices
            .Where(p => p.SubscriptionPlan.Code == planCode && p.ValidTo == null)
            .SingleAsync(TestContext.Current.CancellationToken);

        await ReplaceSubscriptionAsync(db, coach);
        db.CoachSubscriptions.Add(new CoachSubscription
        {
            ProfessionalProfileId = coach.ProfileId,
            SubscriptionPlanId = price.SubscriptionPlanId,
            SubscriptionPlanPriceId = price.Id,
            Status = status,
            FreeUntil = freeUntil,
            PaysSecondRoleAddOn = paysSecondRoleAddOn,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Writes a plan with the given check-in flags and a subscription of the coach to it.
    /// </summary>
    /// <param name="factory">The test host.</param>
    /// <param name="coach">The coach to subscribe.</param>
    /// <param name="canUseWeeklyCheckIns">The plan's weekly check-in flag.</param>
    /// <param name="canUsePerClientCheckInConfig">The plan's per-client check-in configuration flag.</param>
    /// <param name="status">The subscription status.</param>
    /// <param name="maxActiveClients">The plan's active-client limit; null means unlimited.</param>
    public static async Task SubscribeAsync(
        FitnessApiFactory factory,
        Actor coach,
        bool canUseWeeklyCheckIns,
        bool canUsePerClientCheckInConfig,
        SubscriptionStatus status = SubscriptionStatus.Active,
        int? maxActiveClients = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var plan = new SubscriptionPlan
        {
            Code = $"check-ins-{Guid.NewGuid():N}",
            NameCs = "Plán",
            NameEn = "Plan",
            NameDe = "Plan",
            BothRolesIncluded = true,
            CanUseWeeklyCheckIns = canUseWeeklyCheckIns,
            CanUsePerClientCheckInConfig = canUsePerClientCheckInConfig,
            MaxActiveClients = maxActiveClients,
            PriceMinorUnits = 99900,
            Currency = "CZK",
            BillingInterval = BillingInterval.Monthly,
            IsActive = true,
        };
        var price = SubscriptionPlanPriceBuilder.OpenPriceFor(plan);
        db.SubscriptionPlans.Add(plan);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await ReplaceSubscriptionAsync(db, coach);
        db.CoachSubscriptions.Add(new CoachSubscription
        {
            ProfessionalProfileId = coach.ProfileId,
            SubscriptionPlanId = plan.Id,
            SubscriptionPlanPriceId = price.Id,
            Status = status,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Moves the coach to a fresh active plan whose payments feature is off. A canceled subscription would not do
    /// for that: it falls back to the Free plan, which includes payments.
    /// </summary>
    /// <param name="factory">The test host.</param>
    /// <param name="coach">The coach to move.</param>
    public static async Task MoveToPlanWithoutPaymentsAsync(FitnessApiFactory factory, Actor coach)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var plan = new SubscriptionPlan
        {
            Code = $"no-payments-{Guid.NewGuid():N}",
            NameCs = "Plán",
            NameEn = "Plan",
            NameDe = "Plan",
            BothRolesIncluded = true,
            CanUsePayments = false,
            PriceMinorUnits = 99900,
            Currency = "CZK",
            BillingInterval = BillingInterval.Monthly,
            IsActive = true,
        };
        var price = SubscriptionPlanPriceBuilder.OpenPriceFor(plan);
        db.SubscriptionPlans.Add(plan);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await ReplaceSubscriptionAsync(db, coach);
        db.CoachSubscriptions.Add(new CoachSubscription
        {
            ProfessionalProfileId = coach.ProfileId,
            SubscriptionPlanId = plan.Id,
            SubscriptionPlanPriceId = price.Id,
            Status = SubscriptionStatus.Active,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A coach has at most one subscription row, so subscribing again replaces the earlier one.
    /// </summary>
    private static Task<int> ReplaceSubscriptionAsync(ApplicationDbContext db, Actor coach) =>
        db.CoachSubscriptions
            .Where(cs => cs.ProfessionalProfileId == coach.ProfileId)
            .ExecuteDeleteAsync(TestContext.Current.CancellationToken);
}

/// <summary>
/// Builds the <see cref="FormUp.Application.Domain.Services.EntitlementService"/> a unit test passes to an endpoint.
/// </summary>
public static class EntitlementServiceBuilder
{
    /// <summary>
    /// A service over <paramref name="db"/> with the system clock and a null logger.
    /// </summary>
    /// <param name="db">The database context the service reads.</param>
    public static FormUp.Application.Domain.Services.EntitlementService For(IApplicationDbContext db) =>
        new(db, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<FormUp.Application.Domain.Services.EntitlementService>.Instance);
}
