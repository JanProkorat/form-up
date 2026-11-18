using FastEndpoints;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Features.Users.AddRole;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using NSubstitute;

namespace FormUp.Tests.Endpoints.Users;

public class AddRoleEndpointTests
{
    private readonly UserManager<ApplicationUser> _userManager = EndpointTestHelpers.CreateFakeUserManager();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();

    private static IConfiguration CreateFakeConfig()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ConfigKeys.JwtSecret] = "super-secret-key-that-is-at-least-32-characters-long-for-hmac",
                [ConfigKeys.JwtAccessTokenExpirationMinutes] = "15",
                [ConfigKeys.JwtRefreshTokenExpirationDays] = "7"
            })
            .Build();
    }

    [Fact]
    public async Task HandleAsync_TrainerAddsNutritionist_ReturnsTokensWithBothRoles()
    {
        var userId = Guid.NewGuid();
        var user = new ApplicationUser { Id = userId, Email = "trainer@test.com", UserName = "trainer@test.com" };
        var db = new MockDbBuilder()
            .With(new ProfessionalProfile { UserId = userId })
            .Build();

        _userManager.FindByIdAsync(userId.ToString()).Returns(user);
        _userManager.GetRolesAsync(user)
            .Returns(
                new List<string> { AppRoles.Trainer },
                new List<string> { AppRoles.Trainer, AppRoles.Nutritionist });
        _userManager.AddToRoleAsync(user, AppRoles.Nutritionist).Returns(IdentityResult.Success);

        var ep = Factory.Create<AddRoleEndpoint>(
            _userManager, db, CreateFakeConfig(), _audit, Substitute.For<ISubscriptionService>(), TimeProvider.System);
        ep.HttpContext.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                EndpointTestHelpers.FakeUserClaims(userId, AppRoles.Trainer)));

        await ep.HandleAsync(new AddRoleRequest { Role = AppRoles.Nutritionist }, CancellationToken.None);

        ep.HttpContext.Response.StatusCode.Should().Be(200);
        ep.Response.AddedRole.Should().Be(AppRoles.Nutritionist);
        ep.Response.AccessToken.Should().NotBeNullOrEmpty();
        ep.Response.RefreshToken.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task HandleAsync_NutritionistAddsTrainer_CreatesProfessionalProfile()
    {
        var userId = Guid.NewGuid();
        var user = new ApplicationUser { Id = userId, Email = "nutri@test.com", UserName = "nutri@test.com" };
        var db = new MockDbBuilder().Build(); // No ProfessionalProfile

        _userManager.FindByIdAsync(userId.ToString()).Returns(user);
        _userManager.GetRolesAsync(user)
            .Returns(
                new List<string> { AppRoles.Nutritionist },
                new List<string> { AppRoles.Nutritionist, AppRoles.Trainer });
        _userManager.AddToRoleAsync(user, AppRoles.Trainer).Returns(IdentityResult.Success);

        var ep = Factory.Create<AddRoleEndpoint>(
            _userManager, db, CreateFakeConfig(), _audit, Substitute.For<ISubscriptionService>(), TimeProvider.System);
        ep.HttpContext.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                EndpointTestHelpers.FakeUserClaims(userId, AppRoles.Nutritionist)));

        await ep.HandleAsync(new AddRoleRequest { Role = AppRoles.Trainer }, CancellationToken.None);

        ep.Response.AddedRole.Should().Be(AppRoles.Trainer);
        db.ProfessionalProfiles.Received(1).Add(Arg.Is<ProfessionalProfile>(p => p.UserId == userId));
    }

    [Fact]
    public async Task HandleAsync_AlreadyHasRole_ThrowsValidationError()
    {
        var userId = Guid.NewGuid();
        var user = new ApplicationUser { Id = userId, Email = "both@test.com", UserName = "both@test.com" };
        var db = new MockDbBuilder()
            .With(new ProfessionalProfile { UserId = userId })
            .Build();

        _userManager.FindByIdAsync(userId.ToString()).Returns(user);
        _userManager.GetRolesAsync(user)
            .Returns(new List<string> { AppRoles.Trainer, AppRoles.Nutritionist });

        var ep = Factory.Create<AddRoleEndpoint>(
            _userManager, db, CreateFakeConfig(), _audit, Substitute.For<ISubscriptionService>(), TimeProvider.System);
        ep.HttpContext.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                EndpointTestHelpers.FakeUserClaims(userId, AppRoles.Trainer)));

        var act = () => ep.HandleAsync(new AddRoleRequest { Role = AppRoles.Trainer }, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationFailureException>();
    }

    [Fact]
    public async Task Validator_RejectsAdminRole()
    {
        var validator = new AddRoleValidator();
        var result = await validator.ValidateAsync(new AddRoleRequest { Role = "Admin" }, TestContext.Current.CancellationToken);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validator_RejectsClientRole()
    {
        var validator = new AddRoleValidator();
        var result = await validator.ValidateAsync(new AddRoleRequest { Role = "Client" }, TestContext.Current.CancellationToken);

        result.IsValid.Should().BeFalse();
    }

    // ── Handler-level guard tests (validator bypassed) ───────────────────────
    // These tests prove the defense-in-depth guard in HandleAsync fires even
    // when the validator is not wired up (e.g. FastEndpoints wiring change).
    // See issue #308 and SelfAssignableRoles for the rationale.

    [Fact]
    public async Task HandleAsync_WithAdminRole_ThrowsValidationError()
    {
        var userId = Guid.NewGuid();
        var db = new MockDbBuilder().Build();

        var ep = Factory.Create<AddRoleEndpoint>(
            _userManager, db, CreateFakeConfig(), _audit, Substitute.For<ISubscriptionService>(), TimeProvider.System);
        ep.HttpContext.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                EndpointTestHelpers.FakeUserClaims(userId, AppRoles.Trainer)));

        // Invoke HandleAsync directly — validator is bypassed.
        var act = () => ep.HandleAsync(new AddRoleRequest { Role = AppRoles.Admin }, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationFailureException>();
    }

    [Fact]
    public async Task HandleAsync_WithClientRole_ThrowsValidationError()
    {
        var userId = Guid.NewGuid();
        var db = new MockDbBuilder().Build();

        var ep = Factory.Create<AddRoleEndpoint>(
            _userManager, db, CreateFakeConfig(), _audit, Substitute.For<ISubscriptionService>(), TimeProvider.System);
        ep.HttpContext.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                EndpointTestHelpers.FakeUserClaims(userId, AppRoles.Trainer)));

        // Invoke HandleAsync directly — validator is bypassed.
        var act = () => ep.HandleAsync(new AddRoleRequest { Role = AppRoles.Client }, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationFailureException>();
    }

    [Fact]
    public async Task HandleAsync_WritesAuditLog()
    {
        var userId = Guid.NewGuid();
        var user = new ApplicationUser { Id = userId, Email = "audit@test.com", UserName = "audit@test.com" };
        var db = new MockDbBuilder()
            .With(new ProfessionalProfile { UserId = userId })
            .Build();

        _userManager.FindByIdAsync(userId.ToString()).Returns(user);
        _userManager.GetRolesAsync(user)
            .Returns(
                new List<string> { AppRoles.Trainer },
                new List<string> { AppRoles.Trainer, AppRoles.Nutritionist });
        _userManager.AddToRoleAsync(user, AppRoles.Nutritionist).Returns(IdentityResult.Success);

        var ep = Factory.Create<AddRoleEndpoint>(
            _userManager, db, CreateFakeConfig(), _audit, Substitute.For<ISubscriptionService>(), TimeProvider.System);
        ep.HttpContext.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                EndpointTestHelpers.FakeUserClaims(userId, AppRoles.Trainer)));

        await ep.HandleAsync(new AddRoleRequest { Role = AppRoles.Nutritionist }, CancellationToken.None);

        await _audit.Received(1).LogAsync(
            userId,
            "AddRole",
            nameof(ApplicationUser),
            userId,
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Is<string?>(s => s != null && s.Contains("Nutritionist")),
            Arg.Any<CancellationToken>());
    }
}
