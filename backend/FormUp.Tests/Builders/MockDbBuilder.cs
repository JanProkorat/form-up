using FormUp.Application.Domain.Entities;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Storage;
using MockQueryable.NSubstitute;
using NSubstitute;

namespace FormUp.Tests.Builders;

/// <summary>
/// Builder for creating a mocked <see cref="ApplicationDbContext"/> with pre-populated DbSets.
/// </summary>
public class MockDbBuilder
{
    private readonly List<ApplicationUser> _users = [];
    private readonly List<ProfessionalProfile> _professionalProfiles = [];
    private readonly List<ClientProfile> _clientProfiles = [];
    private readonly List<ClientProfessionalLink> _clientProfessionalLinks = [];
    private readonly List<RefreshToken> _refreshTokens = [];
    private readonly List<InvitationToken> _invitationTokens = [];
    private readonly List<BodyMeasurement> _bodyMeasurements = [];
    private readonly List<PlanPhoto> _planPhotos = [];
    private readonly List<PlanPhotoViewer> _planPhotoViewers = [];
    private readonly List<ClientOnboardingData> _clientOnboardingData = [];
    private readonly List<ClientNutritionTargets> _clientNutritionTargets = [];
    private readonly List<PendingInvite> _pendingInvites = [];
    private readonly List<ClientRequest> _clientRequests = [];
    private readonly List<QuestionnaireResponse> _questionnaireResponses = [];
    private readonly List<Questionnaire> _questionnaires = [];
    private readonly List<EmailVerificationToken> _emailVerificationTokens = [];
    private readonly List<DevicePushToken> _devicePushTokens = [];
    private readonly List<PhotoDiaryRequest> _photoDiaryRequests = [];
    private readonly List<UserExternalLogin> _userExternalLogins = [];
    private readonly List<SocialLoginNonce> _socialLoginNonces = [];
    private readonly List<Conversation> _conversations = [];
    private readonly List<ChatMessage> _chatMessages = [];
    private readonly List<ClientTag> _clientTags = [];
    private readonly List<SubscriptionPlan> _subscriptionPlans = [];
    private readonly List<CoachSubscription> _coachSubscriptions = [];

    /// <summary>
    /// Adds an <see cref="ApplicationUser"/> to the mock context.
    /// </summary>
    public MockDbBuilder With(ApplicationUser user) { _users.Add(user); return this; }

    /// <summary>
    /// Adds a <see cref="ProfessionalProfile"/> to the mock context.
    /// </summary>
    public MockDbBuilder With(ProfessionalProfile profile) { _professionalProfiles.Add(profile); return this; }

    /// <summary>
    /// Adds a <see cref="ClientProfile"/> to the mock context.
    /// </summary>
    public MockDbBuilder With(ClientProfile profile) { _clientProfiles.Add(profile); return this; }

    /// <summary>
    /// Adds a <see cref="ClientProfessionalLink"/> to the mock context.
    /// </summary>
    public MockDbBuilder With(ClientProfessionalLink link) { _clientProfessionalLinks.Add(link); return this; }

    /// <summary>
    /// Adds a <see cref="Application.Domain.Entities.RefreshToken"/> to the mock context.
    /// </summary>
    public MockDbBuilder With(Application.Domain.Entities.RefreshToken token) { _refreshTokens.Add(token); return this; }

    /// <summary>
    /// Adds an <see cref="InvitationToken"/> to the mock context.
    /// </summary>
    public MockDbBuilder With(InvitationToken token) { _invitationTokens.Add(token); return this; }

    /// <summary>
    /// Adds a <see cref="BodyMeasurement"/> to the mock context.
    /// </summary>
    public MockDbBuilder With(BodyMeasurement measurement) { _bodyMeasurements.Add(measurement); return this; }

    /// <summary>
    /// Adds a <see cref="PlanPhoto"/> to the mock context.
    /// </summary>
    public MockDbBuilder With(PlanPhoto photo) { _planPhotos.Add(photo); return this; }

    /// <summary>
    /// Adds a <see cref="PlanPhotoViewer"/> to the mock context.
    /// </summary>
    public MockDbBuilder With(PlanPhotoViewer viewer) { _planPhotoViewers.Add(viewer); return this; }

    /// <summary>
    /// Adds a <see cref="PlanPhoto"/> together with a <see cref="PlanPhotoViewer"/> row for the
    /// given coach, as the upload-time writer would have produced it.
    /// </summary>
    public MockDbBuilder WithViewedPhoto(Guid professionalUserId, PlanPhoto photo)
    {
        _planPhotos.Add(photo);
        _planPhotoViewers.Add(EntityBuilder.PlanPhotoViewer
            .ForPhoto(photo)
            .WithProfessionalUserId(professionalUserId)
            .Build());
        return this;
    }

    /// <summary>
    /// Adds a <see cref="ClientOnboardingData"/> to the mock context.
    /// </summary>
    public MockDbBuilder With(ClientOnboardingData data) { _clientOnboardingData.Add(data); return this; }

    /// <summary>
    /// Adds a <see cref="ClientNutritionTargets"/> to the mock context.
    /// </summary>
    public MockDbBuilder With(ClientNutritionTargets targets) { _clientNutritionTargets.Add(targets); return this; }

    /// <summary>
    /// Adds a <see cref="PendingInvite"/> to the mock context.
    /// </summary>
    public MockDbBuilder With(PendingInvite invite) { _pendingInvites.Add(invite); return this; }

    /// <summary>
    /// Adds a <see cref="ClientRequest"/> to the mock context.
    /// </summary>
    public MockDbBuilder With(ClientRequest request) { _clientRequests.Add(request); return this; }

    /// <summary>
    /// Adds a <see cref="QuestionnaireResponse"/> to the mock context.
    /// </summary>
    public MockDbBuilder With(QuestionnaireResponse response) { _questionnaireResponses.Add(response); return this; }

    /// <summary>
    /// Adds a <see cref="Questionnaire"/> to the mock context.
    /// </summary>
    public MockDbBuilder With(Questionnaire questionnaire) { _questionnaires.Add(questionnaire); return this; }

    /// <summary>
    /// Adds an <see cref="EmailVerificationToken"/> to the mock context.
    /// </summary>
    public MockDbBuilder With(EmailVerificationToken token) { _emailVerificationTokens.Add(token); return this; }

    /// <summary>
    /// Adds a <see cref="PhotoDiaryRequest"/> to the mock context.
    /// </summary>
    public MockDbBuilder With(PhotoDiaryRequest request) { _photoDiaryRequests.Add(request); return this; }

    /// <summary>
    /// Adds a <see cref="UserExternalLogin"/> to the mock context.
    /// </summary>
    public MockDbBuilder With(UserExternalLogin externalLogin) { _userExternalLogins.Add(externalLogin); return this; }

    /// <summary>
    /// Adds a <see cref="SocialLoginNonce"/> to the mock context.
    /// </summary>
    public MockDbBuilder With(SocialLoginNonce nonce) { _socialLoginNonces.Add(nonce); return this; }

    /// <summary>
    /// Adds a <see cref="Conversation"/> to the mock context.
    /// </summary>
    public MockDbBuilder With(Conversation conversation) { _conversations.Add(conversation); return this; }

    /// <summary>
    /// Adds a <see cref="ChatMessage"/> to the mock context.
    /// </summary>
    public MockDbBuilder With(ChatMessage message) { _chatMessages.Add(message); return this; }

    /// <summary>
    /// Adds a <see cref="ClientTag"/> to the mock context.
    /// </summary>
    public MockDbBuilder With(ClientTag tag) { _clientTags.Add(tag); return this; }

    /// <summary>
    /// Adds a <see cref="SubscriptionPlan"/> to the mock context. A plan coded <c>free</c> replaces the default Free plan.
    /// </summary>
    public MockDbBuilder With(SubscriptionPlan plan) { _subscriptionPlans.Add(plan); return this; }

    /// <summary>
    /// Adds a <see cref="CoachSubscription"/> to the mock context. Its <c>SubscriptionPlan</c> navigation must be set.
    /// </summary>
    public MockDbBuilder With(CoachSubscription subscription) { _coachSubscriptions.Add(subscription); return this; }

    /// <summary>
    /// The Free plan as the seed writes it, so a coach with no subscription row resolves to it in a unit test.
    /// </summary>
    private static SubscriptionPlan SeededFreePlan() => new()
    {
        Id = -1,
        Code = Application.Domain.Constants.SubscriptionPlanCodes.Free,
        NameCs = "Zdarma",
        NameEn = "Free",
        NameDe = "Kostenlos",
        Currency = "CZK",
        BothRolesIncluded = true,
        CanCreatePlans = true,
        CanMessage = true,
        CanUsePayments = true,
        CanUsePlanTemplates = true,
        CanUseRecipes = true,
        CanUseIngredients = true,
        CanUseProgressPhotos = true,
        CanUseWeightAndMeasurements = true,
        CanUseAutomations = true,
        CanUsePackages = true,
        MaxActiveClients = 2,
    };

    /// <summary>
    /// Builds a mocked <see cref="IApplicationDbContext"/> with all registered entities as queryable DbSets.
    /// </summary>
    public IApplicationDbContext Build()
    {
        // Build all mock DbSets BEFORE configuring Returns to avoid
        // NSubstitute's "substitute inside Returns()" pitfall.
        var usersSet = _users.BuildMockDbSet();
        var professionalProfilesSet = _professionalProfiles.BuildMockDbSet();
        var clientProfilesSet = _clientProfiles.BuildMockDbSet();
        var clientProfessionalLinksSet = _clientProfessionalLinks.BuildMockDbSet();
        var refreshTokensSet = _refreshTokens.BuildMockDbSet();
        var invitationTokensSet = _invitationTokens.BuildMockDbSet();
        var bodyMeasurementsSet = _bodyMeasurements.BuildMockDbSet();
        var planPhotosSet = _planPhotos.BuildMockDbSet();
        var planPhotoViewersSet = _planPhotoViewers.BuildMockDbSet();
        var clientOnboardingDataSet = _clientOnboardingData.BuildMockDbSet();
        var clientNutritionTargetsSet = _clientNutritionTargets.BuildMockDbSet();
        var pendingInvitesSet = _pendingInvites.BuildMockDbSet();
        var clientRequestsSet = _clientRequests.BuildMockDbSet();
        var questionnaireResponsesSet = _questionnaireResponses.BuildMockDbSet();
        var questionnairesSet = _questionnaires.BuildMockDbSet();
        var emailVerificationTokensSet = _emailVerificationTokens.BuildMockDbSet();
        var devicePushTokensSet = _devicePushTokens.BuildMockDbSet();
        var photoDiaryRequestsSet = _photoDiaryRequests.BuildMockDbSet();
        var userExternalLoginsSet = _userExternalLogins.BuildMockDbSet();
        var socialLoginNoncesSet = _socialLoginNonces.BuildMockDbSet();
        var conversationsSet = _conversations.BuildMockDbSet();
        var chatMessagesSet = _chatMessages.BuildMockDbSet();
        var clientTagsSet = _clientTags.BuildMockDbSet();
        var clientPaymentAssignmentsSet = new List<ClientPaymentAssignment>().BuildMockDbSet();

        if (!_subscriptionPlans.Any(plan => plan.Code == Application.Domain.Constants.SubscriptionPlanCodes.Free))
        {
            _subscriptionPlans.Add(SeededFreePlan());
        }

        var subscriptionPlansSet = _subscriptionPlans.BuildMockDbSet();
        var coachSubscriptionsSet = _coachSubscriptions.BuildMockDbSet();

        // Built before the Returns() below, per the same NSubstitute pitfall noted above.
        var transaction = Substitute.For<IDbContextTransaction>();

        var db = Substitute.For<IApplicationDbContext>();

        db.Users.Returns(usersSet);
        db.ProfessionalProfiles.Returns(professionalProfilesSet);
        db.ClientProfiles.Returns(clientProfilesSet);
        db.ClientProfessionalLinks.Returns(clientProfessionalLinksSet);
        db.RefreshTokens.Returns(refreshTokensSet);
        db.InvitationTokens.Returns(invitationTokensSet);
        db.BodyMeasurements.Returns(bodyMeasurementsSet);
        db.PlanPhotos.Returns(planPhotosSet);
        db.PlanPhotoViewers.Returns(planPhotoViewersSet);
        db.ClientOnboardingData.Returns(clientOnboardingDataSet);
        db.ClientNutritionTargets.Returns(clientNutritionTargetsSet);
        db.PendingInvites.Returns(pendingInvitesSet);
        db.ClientRequests.Returns(clientRequestsSet);
        db.QuestionnaireResponses.Returns(questionnaireResponsesSet);
        db.Questionnaires.Returns(questionnairesSet);
        db.EmailVerificationTokens.Returns(emailVerificationTokensSet);
        db.DevicePushTokens.Returns(devicePushTokensSet);
        db.PhotoDiaryRequests.Returns(photoDiaryRequestsSet);
        db.UserExternalLogins.Returns(userExternalLoginsSet);
        db.SocialLoginNonces.Returns(socialLoginNoncesSet);
        db.Conversations.Returns(conversationsSet);
        db.ChatMessages.Returns(chatMessagesSet);
        db.ClientTags.Returns(clientTagsSet);
        db.ClientPaymentAssignments.Returns(clientPaymentAssignmentsSet);
        db.SubscriptionPlans.Returns(subscriptionPlansSet);
        db.CoachSubscriptions.Returns(coachSubscriptionsSet);

        db.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(1);

        // Default: consuming any nonce succeeds (1 row updated).
        // Tests that exercise the "already consumed / expired / not found" path
        // override this with db.ConsumeNonceAsync(...).Returns(0).
        db.ConsumeNonceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(1);

        // Default: the atomic refresh-token rotation succeeds (this caller wins
        // the race). Tests that exercise the concurrent-loser path override this
        // with db.RotateRefreshTokenAsync(...).Returns(0), typically alongside a
        // callback that mutates the seeded token to simulate the winner's commit.
        db.RotateRefreshTokenAsync(Arg.Any<string>(), Arg.Any<RefreshToken>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(1);
        db.RevokeRefreshTokenFamilyAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(0);

        // The link-creation endpoints wrap their profession-slot check in a transaction (#1009).
        // Without this, NSubstitute hands back a Task whose result is null and the endpoint NREs
        // on CommitAsync. A no-op transaction is the right stub here: what the lock actually
        // serializes is only observable against a real Postgres container, so that is where it is
        // tested (ProfessionSlotRaceTests), not in these single-threaded unit tests.
        db.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(transaction);

        return db;
    }
}
