using System.Security.Claims;
using FastEndpoints;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Features.Trainers.ClientRequests.AcceptClientRequest;
using FormUp.Tests.Builders;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace FormUp.Tests.Endpoints.Trainers.ClientRequests;

/// <summary>
/// Tests for <see cref="AcceptClientRequestEndpoint"/>, in particular the plan-view
/// permission flags granted on the created/reactivated <see cref="ClientProfessionalLink"/>.
///
/// Regression coverage for #776 — a professional holding BOTH the Trainer and
/// Nutritionist roles must be granted CanViewTrainingPlans AND CanViewNutritionPlans.
/// Previously the endpoint resolved a single, mutually-exclusive "professionalRole"
/// from identity roles (biased toward Nutritionist) and used it to set both flags,
/// which meant accepting ANY client request as a dual-role professional reset
/// CanViewTrainingPlans back to false — even on an existing link that already had it
/// granted true from an earlier Trainer-only acceptance.
/// </summary>
public class AcceptClientRequestEndpointTests
{
    private readonly Guid _professionalUserId = Guid.NewGuid();
    private readonly INotificationService _notificationService = Substitute.For<INotificationService>();
    private readonly IRealtimeNotifier _notifier = Substitute.For<IRealtimeNotifier>();
    private readonly IConversationSeedService _conversationSeedService = Substitute.For<IConversationSeedService>();
    private readonly UserManager<ApplicationUser> _userManager = EndpointTestHelpers.CreateFakeUserManager();

    private static Claim[] MultiRoleClaims(Guid userId, params string[] roles) =>
    [
        new Claim(AppClaims.UserId, userId.ToString()),
        new Claim(AppClaims.Email, "professional@test.com"),
        .. roles.Select(r => new Claim(ClaimTypes.Role, r))
    ];

    private ApplicationUser CreateProfessionalUser() => new()
    {
        Id = _professionalUserId,
        Email = "professional@test.com",
        FirstName = "Pat",
        LastName = "Pro"
    };

    private AcceptClientRequestEndpoint CreateEndpoint(
        FormUp.Application.Infrastructure.Data.IApplicationDbContext db,
        params string[] callerRoles) =>
        Factory.Create<AcceptClientRequestEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(MultiRoleClaims(_professionalUserId, callerRoles))),
            db, _notifier, _notificationService, _conversationSeedService, _userManager,
            EndpointTestHelpers.CreateInviteTermsApplier(db),
            Substitute.For<ILogger<AcceptClientRequestEndpoint>>());

    /// <summary>
    /// Existing link already has CanViewTrainingPlans=true from a prior Trainer-only
    /// acceptance. The professional now also holds the Nutritionist role and accepts a
    /// second (or re-processed) request for the same client — the training flag must
    /// survive, and the nutrition flag must now also be granted.
    /// </summary>
    [Fact]
    public async Task Accept_DualRoleProfessional_ExistingTrainerLink_GrantsBothFlags_KeepsTrainingAccess()
    {
        var professionalUser = CreateProfessionalUser();
        var professionalProfile = EntityBuilder.ProfessionalProfile.WithId(1).WithUser(professionalUser).Build();

        var clientUser = EntityBuilder.User.WithEmail("client@test.com")
            .WithFirstName("C").WithLastName("Lient").Build();
        var clientProfile = EntityBuilder.ClientProfile.WithId(1).WithUser(clientUser).Build();

        var existingLink = EntityBuilder.ClientProfessionalLink
            .WithClientProfile(clientProfile)
            .WithProfessionalProfile(professionalProfile)
            .WithProfessionalRole(UserRole.Trainer)
            .WithCanViewTrainingPlans(true)
            .WithCanViewNutritionPlans(false)
            .Build();

        var clientRequest = new ClientRequest
        {
            PublicId = Guid.NewGuid(),
            ClientProfileId = clientProfile.Id,
            ClientProfile = clientProfile,
            ProfessionalProfileId = professionalProfile.Id,
            ProfessionalProfile = professionalProfile,
            Status = ClientRequestStatus.Pending
        };

        var db = new MockDbBuilder()
            .With(professionalUser)
            .With(professionalProfile)
            .With(clientProfile)
            .With(existingLink)
            .With(clientRequest)
            .Build();

        var ep = CreateEndpoint(db, AppRoles.Trainer, AppRoles.Nutritionist);

        await ep.HandleAsync(
            new AcceptClientRequestRequest { PublicId = clientRequest.PublicId },
            TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(204);
        existingLink.CanViewTrainingPlans.Should().BeTrue(
            "the professional still holds the Trainer role and must not lose training-plan access");
        existingLink.CanViewNutritionPlans.Should().BeTrue(
            "the professional now also holds the Nutritionist role and must gain nutrition-plan access");
    }

    /// <summary>
    /// New link, single-role (Trainer only) professional — unaffected by the fix,
    /// preserves prior single-role behavior.
    /// </summary>
    [Fact]
    public async Task Accept_TrainerOnlyProfessional_NewLink_GrantsTrainingAccessOnly()
    {
        var professionalUser = CreateProfessionalUser();
        var professionalProfile = EntityBuilder.ProfessionalProfile.WithId(1).WithUser(professionalUser).Build();

        var clientUser = EntityBuilder.User.WithEmail("client2@test.com")
            .WithFirstName("C").WithLastName("Lient").Build();
        var clientProfile = EntityBuilder.ClientProfile.WithId(1).WithUser(clientUser).Build();

        var clientRequest = new ClientRequest
        {
            PublicId = Guid.NewGuid(),
            ClientProfileId = clientProfile.Id,
            ClientProfile = clientProfile,
            ProfessionalProfileId = professionalProfile.Id,
            ProfessionalProfile = professionalProfile,
            Status = ClientRequestStatus.Pending
        };

        var db = new MockDbBuilder()
            .With(professionalUser)
            .With(professionalProfile)
            .With(clientProfile)
            .With(clientRequest)
            .Build();

        var ep = CreateEndpoint(db, AppRoles.Trainer);

        await ep.HandleAsync(
            new AcceptClientRequestRequest { PublicId = clientRequest.PublicId },
            TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(204);
        db.ClientProfessionalLinks.Received(1).Add(Arg.Is<ClientProfessionalLink>(
            l => l.CanViewTrainingPlans && !l.CanViewNutritionPlans));
    }

    /// <summary>
    /// Dual-role professional explicitly narrows the new link to nutrition only, even
    /// though they also hold the Trainer role — the explicit scope wins over the
    /// full-held-role default.
    /// </summary>
    [Fact]
    public async Task Accept_DualRoleProfessional_ExplicitNutritionOnlyScope_GrantsNutritionOnly()
    {
        var professionalUser = CreateProfessionalUser();
        var professionalProfile = EntityBuilder.ProfessionalProfile.WithId(1).WithUser(professionalUser).Build();

        var clientUser = EntityBuilder.User.WithEmail("client3@test.com")
            .WithFirstName("C").WithLastName("Lient").Build();
        var clientProfile = EntityBuilder.ClientProfile.WithId(1).WithUser(clientUser).Build();

        var clientRequest = new ClientRequest
        {
            PublicId = Guid.NewGuid(),
            ClientProfileId = clientProfile.Id,
            ClientProfile = clientProfile,
            ProfessionalProfileId = professionalProfile.Id,
            ProfessionalProfile = professionalProfile,
            Status = ClientRequestStatus.Pending
        };

        var db = new MockDbBuilder()
            .With(professionalUser)
            .With(professionalProfile)
            .With(clientProfile)
            .With(clientRequest)
            .Build();

        var ep = CreateEndpoint(db, AppRoles.Trainer, AppRoles.Nutritionist);

        await ep.HandleAsync(
            new AcceptClientRequestRequest
            {
                PublicId = clientRequest.PublicId,
                RequestedScope = LinkCapabilityScope.NutritionOnly
            },
            TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(204);
        db.ClientProfessionalLinks.Received(1).Add(Arg.Is<ClientProfessionalLink>(
            l => l.CanViewNutritionPlans && !l.CanViewTrainingPlans));
    }

    /// <summary>
    /// Reactivate path: an existing link already grants both flags from a prior
    /// dual-role acceptance. Explicitly requesting TrainingOnly on reactivation must
    /// overwrite the stale nutrition flag to false, not merge/preserve it.
    /// </summary>
    [Fact]
    public async Task Accept_DualRoleProfessional_ReactivateWithExplicitTrainingOnlyScope_NarrowsAwayStaleNutritionFlag()
    {
        var professionalUser = CreateProfessionalUser();
        var professionalProfile = EntityBuilder.ProfessionalProfile.WithId(1).WithUser(professionalUser).Build();

        var clientUser = EntityBuilder.User.WithEmail("client4@test.com")
            .WithFirstName("C").WithLastName("Lient").Build();
        var clientProfile = EntityBuilder.ClientProfile.WithId(1).WithUser(clientUser).Build();

        var existingLink = EntityBuilder.ClientProfessionalLink
            .WithClientProfile(clientProfile)
            .WithProfessionalProfile(professionalProfile)
            .WithProfessionalRole(UserRole.Nutritionist)
            .WithCanViewTrainingPlans(true)
            .WithCanViewNutritionPlans(true)
            .Build();

        var clientRequest = new ClientRequest
        {
            PublicId = Guid.NewGuid(),
            ClientProfileId = clientProfile.Id,
            ClientProfile = clientProfile,
            ProfessionalProfileId = professionalProfile.Id,
            ProfessionalProfile = professionalProfile,
            Status = ClientRequestStatus.Pending
        };

        var db = new MockDbBuilder()
            .With(professionalUser)
            .With(professionalProfile)
            .With(clientProfile)
            .With(existingLink)
            .With(clientRequest)
            .Build();

        var ep = CreateEndpoint(db, AppRoles.Trainer, AppRoles.Nutritionist);

        await ep.HandleAsync(
            new AcceptClientRequestRequest
            {
                PublicId = clientRequest.PublicId,
                RequestedScope = LinkCapabilityScope.TrainingOnly
            },
            TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(204);
        existingLink.CanViewTrainingPlans.Should().BeTrue();
        existingLink.CanViewNutritionPlans.Should().BeFalse(
            "the explicit TrainingOnly scope must overwrite the stale nutrition flag, not preserve it");
    }

    /// <summary>
    /// Security invariant (#917): a Trainer-only professional cannot request
    /// NutritionOnly scope for themselves — the requested scope must be validated as a
    /// subset of the caller's actually-held roles. Deleting the subset check makes this
    /// test fail (proven and reverted — see PR description).
    /// </summary>
    [Fact]
    public async Task Accept_TrainerOnlyProfessional_RequestsNutritionOnlyScope_Returns400WithErrorCode()
    {
        var professionalUser = CreateProfessionalUser();
        var professionalProfile = EntityBuilder.ProfessionalProfile.WithId(1).WithUser(professionalUser).Build();

        var clientUser = EntityBuilder.User.WithEmail("client5@test.com")
            .WithFirstName("C").WithLastName("Lient").Build();
        var clientProfile = EntityBuilder.ClientProfile.WithId(1).WithUser(clientUser).Build();

        var clientRequest = new ClientRequest
        {
            PublicId = Guid.NewGuid(),
            ClientProfileId = clientProfile.Id,
            ClientProfile = clientProfile,
            ProfessionalProfileId = professionalProfile.Id,
            ProfessionalProfile = professionalProfile,
            Status = ClientRequestStatus.Pending
        };

        var db = new MockDbBuilder()
            .With(professionalUser)
            .With(professionalProfile)
            .With(clientProfile)
            .With(clientRequest)
            .Build();

        var ep = CreateEndpoint(db, AppRoles.Trainer);

        var act = () => ep.HandleAsync(
            new AcceptClientRequestRequest
            {
                PublicId = clientRequest.PublicId,
                RequestedScope = LinkCapabilityScope.NutritionOnly
            },
            TestContext.Current.CancellationToken);

        var exception = await act.Should().ThrowAsync<ValidationFailureException>();
        exception.Which.Failures.Should().ContainSingle(
            f => f.ErrorCode == ErrorCodes.RequestedScopeExceedsHeldRoles);
        db.ClientProfessionalLinks.DidNotReceive().Add(Arg.Any<ClientProfessionalLink>());
    }

    /// <summary>
    /// Insert branch (#980): accepting would give the client a SECOND active
    /// nutritionist — a different professional already holds an active link with
    /// CanViewNutritionPlans. A client may hold at most one active coach per profession.
    /// </summary>
    [Fact]
    public async Task Accept_ClientHasActiveNutritionistLink_AnotherNutritionistAccepts_Returns400WithErrorCode()
    {
        var professionalUser = CreateProfessionalUser();
        var professionalProfile = EntityBuilder.ProfessionalProfile.WithId(1).WithUser(professionalUser).Build();

        var otherNutritionistUser = EntityBuilder.User.WithEmail("other-nutritionist@test.com")
            .WithFirstName("O").WithLastName("N").Build();
        var otherNutritionistProfile = EntityBuilder.ProfessionalProfile.WithId(2).WithUser(otherNutritionistUser).Build();

        var clientUser = EntityBuilder.User.WithEmail("client-occupied@test.com")
            .WithFirstName("C").WithLastName("Lient").Build();
        var clientProfile = EntityBuilder.ClientProfile.WithId(1).WithUser(clientUser).Build();

        var occupyingLink = EntityBuilder.ClientProfessionalLink
            .WithClientProfile(clientProfile)
            .WithProfessionalProfile(otherNutritionistProfile)
            .WithCanViewNutritionPlans(true)
            .WithCanViewTrainingPlans(false)
            .Build();

        var clientRequest = new ClientRequest
        {
            PublicId = Guid.NewGuid(),
            ClientProfileId = clientProfile.Id,
            ClientProfile = clientProfile,
            ProfessionalProfileId = professionalProfile.Id,
            ProfessionalProfile = professionalProfile,
            Status = ClientRequestStatus.Pending
        };

        var db = new MockDbBuilder()
            .With(professionalUser)
            .With(professionalProfile)
            .With(otherNutritionistProfile)
            .With(clientProfile)
            .With(occupyingLink)
            .With(clientRequest)
            .Build();

        var ep = CreateEndpoint(db, AppRoles.Nutritionist);

        var act = () => ep.HandleAsync(
            new AcceptClientRequestRequest { PublicId = clientRequest.PublicId },
            TestContext.Current.CancellationToken);

        var exception = await act.Should().ThrowAsync<ValidationFailureException>();
        exception.Which.Failures.Should().ContainSingle(
            f => f.ErrorCode == ErrorCodes.ProfessionAlreadyOccupied);
        clientRequest.Status.Should().Be(ClientRequestStatus.Pending);
        db.ClientProfessionalLinks.DidNotReceive().Add(Arg.Any<ClientProfessionalLink>());
    }

    /// <summary>
    /// Reactivation branch (#980): an inactive link belonging to the ACCEPTING
    /// professional already exists, but a different professional now holds the
    /// training slot the reactivation would re-claim. Must be rejected too — not just
    /// the insert branch above.
    /// </summary>
    [Fact]
    public async Task Accept_ReactivatingOwnInactiveLink_ClientHasActiveTrainerLink_Returns400WithErrorCode()
    {
        var professionalUser = CreateProfessionalUser();
        var professionalProfile = EntityBuilder.ProfessionalProfile.WithId(1).WithUser(professionalUser).Build();

        var otherTrainerUser = EntityBuilder.User.WithEmail("other-trainer@test.com")
            .WithFirstName("O").WithLastName("T").Build();
        var otherTrainerProfile = EntityBuilder.ProfessionalProfile.WithId(2).WithUser(otherTrainerUser).Build();

        var clientUser = EntityBuilder.User.WithEmail("client-reactivate@test.com")
            .WithFirstName("C").WithLastName("Lient").Build();
        var clientProfile = EntityBuilder.ClientProfile.WithId(1).WithUser(clientUser).Build();

        // The accepting professional's own link already exists but is inactive.
        var ownInactiveLink = EntityBuilder.ClientProfessionalLink
            .WithClientProfile(clientProfile)
            .WithProfessionalProfile(professionalProfile)
            .WithCanViewTrainingPlans(true)
            .WithCanViewNutritionPlans(false)
            .Inactive()
            .Build();

        // A DIFFERENT trainer now holds the training slot.
        var occupyingLink = EntityBuilder.ClientProfessionalLink
            .WithClientProfile(clientProfile)
            .WithProfessionalProfile(otherTrainerProfile)
            .WithCanViewTrainingPlans(true)
            .WithCanViewNutritionPlans(false)
            .Build();

        var clientRequest = new ClientRequest
        {
            PublicId = Guid.NewGuid(),
            ClientProfileId = clientProfile.Id,
            ClientProfile = clientProfile,
            ProfessionalProfileId = professionalProfile.Id,
            ProfessionalProfile = professionalProfile,
            Status = ClientRequestStatus.Pending
        };

        var db = new MockDbBuilder()
            .With(professionalUser)
            .With(professionalProfile)
            .With(otherTrainerProfile)
            .With(clientProfile)
            .With(ownInactiveLink)
            .With(occupyingLink)
            .With(clientRequest)
            .Build();

        var ep = CreateEndpoint(db, AppRoles.Trainer);

        var act = () => ep.HandleAsync(
            new AcceptClientRequestRequest { PublicId = clientRequest.PublicId },
            TestContext.Current.CancellationToken);

        var exception = await act.Should().ThrowAsync<ValidationFailureException>();
        exception.Which.Failures.Should().ContainSingle(
            f => f.ErrorCode == ErrorCodes.ProfessionAlreadyOccupied);
        ownInactiveLink.ConnectionState.Should().Be(ConnectionState.Ended,
            "the reactivation must not go through when the slot is already occupied by another professional");
    }

    /// <summary>
    /// Negative control (#980): a client already has an active TRAINER link; accepting
    /// a NUTRITIONIST-only request for a DIFFERENT professional must succeed — the two
    /// profession slots are independent and do not block each other.
    /// </summary>
    [Fact]
    public async Task Accept_ClientHasActiveTrainerLink_NutritionistOnlyAccept_Returns204()
    {
        var professionalUser = CreateProfessionalUser();
        var professionalProfile = EntityBuilder.ProfessionalProfile.WithId(1).WithUser(professionalUser).Build();

        var trainerUser = EntityBuilder.User.WithEmail("trainer-coexist@test.com")
            .WithFirstName("T").WithLastName("R").Build();
        var trainerProfile = EntityBuilder.ProfessionalProfile.WithId(2).WithUser(trainerUser).Build();

        var clientUser = EntityBuilder.User.WithEmail("client-coexist@test.com")
            .WithFirstName("C").WithLastName("Lient").Build();
        var clientProfile = EntityBuilder.ClientProfile.WithId(1).WithUser(clientUser).Build();

        var trainerLink = EntityBuilder.ClientProfessionalLink
            .WithClientProfile(clientProfile)
            .WithProfessionalProfile(trainerProfile)
            .WithCanViewTrainingPlans(true)
            .WithCanViewNutritionPlans(false)
            .Build();

        var clientRequest = new ClientRequest
        {
            PublicId = Guid.NewGuid(),
            ClientProfileId = clientProfile.Id,
            ClientProfile = clientProfile,
            ProfessionalProfileId = professionalProfile.Id,
            ProfessionalProfile = professionalProfile,
            Status = ClientRequestStatus.Pending
        };

        var db = new MockDbBuilder()
            .With(professionalUser)
            .With(professionalProfile)
            .With(trainerProfile)
            .With(clientProfile)
            .With(trainerLink)
            .With(clientRequest)
            .Build();

        var ep = CreateEndpoint(db, AppRoles.Nutritionist);

        await ep.HandleAsync(
            new AcceptClientRequestRequest { PublicId = clientRequest.PublicId },
            TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(204);
        db.ClientProfessionalLinks.Received(1).Add(Arg.Is<ClientProfessionalLink>(
            l => l.CanViewNutritionPlans && !l.CanViewTrainingPlans));
    }
}
