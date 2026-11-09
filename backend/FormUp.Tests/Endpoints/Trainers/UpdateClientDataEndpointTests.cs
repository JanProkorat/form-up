using System.Security.Claims;
using FastEndpoints;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.Trainers.UpdateClientData;
using FormUp.Tests.Builders;
using Microsoft.AspNetCore.Identity;
using NSubstitute;
using FormUp.Application.Domain.Enums;

namespace FormUp.Tests.Endpoints.Trainers;

/// <summary>
/// Tests for <see cref="UpdateClientDataEndpoint"/>, in particular the (#667)
/// identity-field write path — trainers persisting a client's first/last name
/// and email from the Edit Profile dialog.
/// </summary>
public class UpdateClientDataEndpointTests
{
    private readonly Guid _trainerId = Guid.NewGuid();
    private readonly Guid _clientUserId = Guid.NewGuid();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();

    private static (ProfessionalProfile Trainer, ClientProfile Client, ClientProfessionalLink Link) BuildLinkedPair(
        Guid trainerUserId, Guid clientUserId)
    {
        var trainerProfile = new ProfessionalProfile { Id = 1, UserId = trainerUserId, PublicId = Guid.NewGuid() };
        var clientProfile = new ClientProfile { Id = 1, UserId = clientUserId, PublicId = Guid.NewGuid() };
        var link = new ClientProfessionalLink
        {
            ProfessionalProfileId = trainerProfile.Id,
            ClientProfileId = clientProfile.Id,
            ConnectionState = ConnectionState.Connected,
            PublicId = Guid.NewGuid(),
            DateCreated = DateTime.UtcNow
        };
        return (trainerProfile, clientProfile, link);
    }

    private UpdateClientDataEndpoint CreateEndpoint(
        FormUp.Application.Infrastructure.Data.IApplicationDbContext db,
        UserManager<ApplicationUser> userManager) =>
        Factory.Create<UpdateClientDataEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(_trainerId, AppRoles.Trainer))),
            db, userManager, _audit, new ClientLinkAuthorizationService(db));

    [Fact]
    public async Task HandleAsync_ValidNameAndEmail_UpdatesIdentityFields_Returns200()
    {
        var (trainerProfile, clientProfile, link) = BuildLinkedPair(_trainerId, _clientUserId);
        var db = new MockDbBuilder()
            .With(trainerProfile).With(clientProfile).With(link)
            .Build();

        var clientUser = new ApplicationUser
        {
            Id = _clientUserId, Email = "old@test.com", UserName = "old@test.com",
            FirstName = "Old", LastName = "Name"
        };
        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        userManager.FindByIdAsync(_clientUserId.ToString()).Returns(clientUser);
        userManager.SetEmailAsync(clientUser, "new@test.com").Returns(IdentityResult.Success);
        userManager.SetUserNameAsync(clientUser, "new@test.com").Returns(IdentityResult.Success);
        userManager.UpdateAsync(clientUser).Returns(IdentityResult.Success);

        var ep = CreateEndpoint(db, userManager);

        await ep.HandleAsync(new UpdateClientDataRequest
        {
            ClientId = clientProfile.PublicId,
            FirstName = "New",
            LastName = "Name",
            Email = "new@test.com"
        }, TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(200);
        clientUser.FirstName.Should().Be("New");
        clientUser.LastName.Should().Be("Name");

        await userManager.Received(1).SetEmailAsync(clientUser, "new@test.com");
        await userManager.Received(1).SetUserNameAsync(clientUser, "new@test.com");
        await userManager.Received(1).UpdateAsync(clientUser);
    }

    [Fact]
    public async Task HandleAsync_DuplicateEmail_ThrowsValidationError_DoesNotSilentlyNoOpOr500()
    {
        var (trainerProfile, clientProfile, link) = BuildLinkedPair(_trainerId, _clientUserId);
        var db = new MockDbBuilder()
            .With(trainerProfile).With(clientProfile).With(link)
            .Build();

        var clientUser = new ApplicationUser
        {
            Id = _clientUserId, Email = "old@test.com", UserName = "old@test.com",
            FirstName = "Old", LastName = "Name"
        };
        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        userManager.FindByIdAsync(_clientUserId.ToString()).Returns(clientUser);
        userManager.SetEmailAsync(clientUser, "taken@test.com")
            .Returns(IdentityResult.Failed(new IdentityError { Code = "DuplicateEmail", Description = "Email 'taken@test.com' is already taken." }));

        var ep = CreateEndpoint(db, userManager);

        var act = () => ep.HandleAsync(new UpdateClientDataRequest
        {
            ClientId = clientProfile.PublicId,
            Email = "taken@test.com"
        }, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ValidationFailureException>();

        // The username must never be touched when the email uniqueness check fails —
        // no partial/silent write.
        await userManager.DidNotReceive().SetUserNameAsync(Arg.Any<ApplicationUser>(), Arg.Any<string>());
    }

    [Fact]
    public async Task HandleAsync_NoActiveLink_Returns404_AndDoesNotTouchIdentity()
    {
        var (trainerProfile, clientProfile, _) = BuildLinkedPair(_trainerId, _clientUserId);
        // No link added — the trainer has no active relationship to this client.
        var db = new MockDbBuilder()
            .With(trainerProfile).With(clientProfile)
            .Build();

        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        var ep = CreateEndpoint(db, userManager);

        await ep.HandleAsync(new UpdateClientDataRequest
        {
            ClientId = clientProfile.PublicId,
            FirstName = "New"
        }, TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(404);
        await userManager.DidNotReceive().FindByIdAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task HandleAsync_SameEmailCasingDifference_DoesNotCallSetEmailAsync()
    {
        var (trainerProfile, clientProfile, link) = BuildLinkedPair(_trainerId, _clientUserId);
        var db = new MockDbBuilder()
            .With(trainerProfile).With(clientProfile).With(link)
            .Build();

        var clientUser = new ApplicationUser
        {
            Id = _clientUserId, Email = "same@test.com", UserName = "same@test.com",
            FirstName = "Old", LastName = "Name"
        };
        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        userManager.FindByIdAsync(_clientUserId.ToString()).Returns(clientUser);

        var ep = CreateEndpoint(db, userManager);

        await ep.HandleAsync(new UpdateClientDataRequest
        {
            ClientId = clientProfile.PublicId,
            Email = "SAME@TEST.COM"
        }, TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(200);
        await userManager.DidNotReceive().SetEmailAsync(Arg.Any<ApplicationUser>(), Arg.Any<string>());
    }

    /// <summary>
    /// The highest-risk path in the #1036 nutrition-targets split: a client whose
    /// <see cref="ClientOnboardingData"/> row exists but whose <see cref="ClientNutritionTargets"/>
    /// child row does not. A patch touching a single target field (Bmr) must create the row
    /// on demand and actually persist the value — not silently no-op, which would be
    /// indistinguishable from success to the caller.
    /// </summary>
    [Fact]
    public async Task HandleAsync_BmrOnlyPatch_NutritionTargetsRowAbsent_CreatesRowAndPersistsValue()
    {
        var (trainerProfile, clientProfile, link) = BuildLinkedPair(_trainerId, _clientUserId);
        link.CanViewNutritionPlans = true;
        var onboardingData = new ClientOnboardingData { Id = 10, ClientProfileId = clientProfile.Id };
        clientProfile.OnboardingData = onboardingData;

        var db = new MockDbBuilder()
            .With(trainerProfile).With(clientProfile).With(link)
            .Build();

        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        var ep = CreateEndpoint(db, userManager);

        await ep.HandleAsync(new UpdateClientDataRequest
        {
            ClientId = clientProfile.PublicId,
            Bmr = 1900m,
        }, TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(200);

        onboardingData.NutritionTargets.Should().NotBeNull(
            "a Bmr-only patch must create the child row rather than silently no-op");
        onboardingData.NutritionTargets!.Bmr.Should().Be(1900m);
        db.ClientNutritionTargets.Received(1).Add(Arg.Is<ClientNutritionTargets>(t => t.Bmr == 1900m));
    }

    /// <summary>
    /// Complements the row-absent case above: when the child row already exists, a target
    /// patch must mutate it in place, not create (and stage for insert) a second one — that
    /// would violate the unique index on <c>ClientOnboardingDataId</c> at save time.
    /// </summary>
    [Fact]
    public async Task HandleAsync_BmrOnlyPatch_NutritionTargetsRowExists_MutatesInPlace_DoesNotAddDuplicate()
    {
        var (trainerProfile, clientProfile, link) = BuildLinkedPair(_trainerId, _clientUserId);
        link.CanViewNutritionPlans = true;
        var onboardingData = new ClientOnboardingData { Id = 11, ClientProfileId = clientProfile.Id };
        var existingTargets = EntityBuilder.ClientNutritionTargets
            .WithClientOnboardingDataId(onboardingData.Id)
            .Build();
        onboardingData.NutritionTargets = existingTargets;
        clientProfile.OnboardingData = onboardingData;

        var db = new MockDbBuilder()
            .With(trainerProfile).With(clientProfile).With(link).With(existingTargets)
            .Build();

        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        var ep = CreateEndpoint(db, userManager);

        await ep.HandleAsync(new UpdateClientDataRequest
        {
            ClientId = clientProfile.PublicId,
            Bmr = 2000m,
        }, TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(200);

        existingTargets.Bmr.Should().Be(2000m);
        onboardingData.NutritionTargets.Should().BeSameAs(existingTargets,
            "the existing row must be mutated in place, not replaced with a new instance");
        db.ClientNutritionTargets.DidNotReceive().Add(Arg.Any<ClientNutritionTargets>());
    }

    [Fact]
    public async Task HandleAsync_TrainingOnlyLink_MacroFields_Returns403_TargetsUnchanged_NoIdentityWrite()
    {
        var (trainerProfile, clientProfile, link) = BuildLinkedPair(_trainerId, _clientUserId);
        link.CanViewTrainingPlans = true;
        link.CanViewNutritionPlans = false;
        var onboardingData = new ClientOnboardingData { Id = 12, ClientProfileId = clientProfile.Id };
        var existingTargets = EntityBuilder.ClientNutritionTargets
            .WithClientOnboardingDataId(onboardingData.Id)
            .Build();
        onboardingData.NutritionTargets = existingTargets;
        clientProfile.OnboardingData = onboardingData;
        var originalBmr = existingTargets.Bmr;
        var originalProtein = existingTargets.ProteinGrams;

        var db = new MockDbBuilder()
            .With(trainerProfile).With(clientProfile).With(link).With(existingTargets)
            .Build();
        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        var ep = CreateEndpoint(db, userManager);

        await ep.HandleAsync(new UpdateClientDataRequest
        {
            ClientId = clientProfile.PublicId,
            Bmr = 9999m,
            ProteinGrams = 999m,
        }, TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(403);
        existingTargets.Bmr.Should().Be(originalBmr);
        existingTargets.ProteinGrams.Should().Be(originalProtein);
        await userManager.DidNotReceive().FindByIdAsync(Arg.Any<string>());
        await userManager.DidNotReceive().UpdateAsync(Arg.Any<ApplicationUser>());
        await db.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_TrainingOnlyLink_MacroFieldWithoutOnboardingData_Returns403()
    {
        var (trainerProfile, clientProfile, link) = BuildLinkedPair(_trainerId, _clientUserId);
        link.CanViewTrainingPlans = true;
        var db = new MockDbBuilder()
            .With(trainerProfile).With(clientProfile).With(link)
            .Build();
        var ep = CreateEndpoint(db, EndpointTestHelpers.CreateFakeUserManager());

        await ep.HandleAsync(new UpdateClientDataRequest
        {
            ClientId = clientProfile.PublicId,
            DerivedNutritionGoal = "LoseWeight",
        }, TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task HandleAsync_TrainingOnlyLink_MacroFieldsAndFirstName_Returns403_NameUnchanged()
    {
        var (trainerProfile, clientProfile, link) = BuildLinkedPair(_trainerId, _clientUserId);
        link.CanViewTrainingPlans = true;
        var db = new MockDbBuilder()
            .With(trainerProfile).With(clientProfile).With(link)
            .Build();
        var clientUser = new ApplicationUser
        {
            Id = _clientUserId, Email = "old@test.com", UserName = "old@test.com",
            FirstName = "Old", LastName = "Name"
        };
        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        userManager.FindByIdAsync(_clientUserId.ToString()).Returns(clientUser);
        var ep = CreateEndpoint(db, userManager);

        await ep.HandleAsync(new UpdateClientDataRequest
        {
            ClientId = clientProfile.PublicId,
            FirstName = "New",
            Bmr = 1800m,
        }, TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(403);
        clientUser.FirstName.Should().Be("Old");
        await userManager.DidNotReceive().UpdateAsync(Arg.Any<ApplicationUser>());
        await userManager.DidNotReceive().SetEmailAsync(Arg.Any<ApplicationUser>(), Arg.Any<string>());
    }

    [Fact]
    public async Task HandleAsync_TrainingOnlyLink_OnlyProfileAndIdentityFields_Returns200()
    {
        var (trainerProfile, clientProfile, link) = BuildLinkedPair(_trainerId, _clientUserId);
        link.CanViewTrainingPlans = true;
        var db = new MockDbBuilder()
            .With(trainerProfile).With(clientProfile).With(link)
            .Build();
        var clientUser = new ApplicationUser
        {
            Id = _clientUserId, Email = "old@test.com", UserName = "old@test.com",
            FirstName = "Old", LastName = "Name"
        };
        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        userManager.FindByIdAsync(_clientUserId.ToString()).Returns(clientUser);
        userManager.UpdateAsync(clientUser).Returns(IdentityResult.Success);
        var ep = CreateEndpoint(db, userManager);

        await ep.HandleAsync(new UpdateClientDataRequest
        {
            ClientId = clientProfile.PublicId,
            FirstName = "New",
            WeightKg = 80m,
        }, TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(200);
        clientUser.FirstName.Should().Be("New");
        clientProfile.WeightKg.Should().Be(80m);
    }

    [Fact]
    public async Task HandleAsync_NutritionLink_MacroTargets_Returns200_Saved()
    {
        var (trainerProfile, clientProfile, link) = BuildLinkedPair(_trainerId, _clientUserId);
        link.CanViewNutritionPlans = true;
        var onboardingData = new ClientOnboardingData { Id = 13, ClientProfileId = clientProfile.Id };
        clientProfile.OnboardingData = onboardingData;
        var db = new MockDbBuilder()
            .With(trainerProfile).With(clientProfile).With(link)
            .Build();
        var ep = CreateEndpoint(db, EndpointTestHelpers.CreateFakeUserManager());

        await ep.HandleAsync(new UpdateClientDataRequest
        {
            ClientId = clientProfile.PublicId,
            Bmr = 1700m,
            ProteinGrams = 140m,
        }, TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(200);
        onboardingData.NutritionTargets!.Bmr.Should().Be(1700m);
        onboardingData.NutritionTargets.ProteinGrams.Should().Be(140m);
        await db.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
