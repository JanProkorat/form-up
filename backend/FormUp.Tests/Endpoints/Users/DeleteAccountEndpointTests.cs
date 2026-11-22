using FastEndpoints;
using FluentAssertions;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Features.Users.DeleteAccount;
using FormUp.Tests.Builders;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore.Storage;
using NSubstitute;

namespace FormUp.Tests.Endpoints.Users;

public class DeleteAccountEndpointTests
{
    private readonly Guid _userId = Guid.NewGuid();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly ISubscriptionService _subscriptions = Substitute.For<ISubscriptionService>();

    [Fact]
    public async Task HandleAsync_ValidUser_Returns204()
    {
        var user = EntityBuilder.User.WithId(_userId).WithEmail("test@test.com").Build();

        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        userManager.FindByIdAsync(_userId.ToString()).Returns(user);
        userManager.DeleteAsync(user).Returns(IdentityResult.Success);

        var db = new MockDbBuilder().Build();

        var ep = Factory.Create<DeleteAccountEndpoint>(
            ctx => ctx.Request.HttpContext.User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_userId))),
            userManager, db, _audit, _subscriptions);

        await ep.HandleAsync(TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(204);
    }

    [Fact]
    public async Task HandleAsync_NoClaims_Returns401()
    {
        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        var db = new MockDbBuilder().Build();

        var ep = Factory.Create<DeleteAccountEndpoint>(userManager, db, _audit, _subscriptions);

        await ep.HandleAsync(TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task HandleAsync_UserNotFound_Returns404()
    {
        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        userManager.FindByIdAsync(_userId.ToString()).Returns((ApplicationUser?)null);

        var db = new MockDbBuilder().Build();

        var ep = Factory.Create<DeleteAccountEndpoint>(
            ctx => ctx.Request.HttpContext.User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_userId))),
            userManager, db, _audit, _subscriptions);

        await ep.HandleAsync(TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task HandleAsync_WritesAuditLogAfterDeletion()
    {
        var user = EntityBuilder.User.WithId(_userId).WithEmail("test@test.com").Build();

        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        userManager.FindByIdAsync(_userId.ToString()).Returns(user);
        userManager.DeleteAsync(user).Returns(IdentityResult.Success);

        var db = new MockDbBuilder().Build();

        var ep = Factory.Create<DeleteAccountEndpoint>(
            ctx => ctx.Request.HttpContext.User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_userId))),
            userManager, db, _audit, _subscriptions);

        await ep.HandleAsync(TestContext.Current.CancellationToken);

        await _audit.Received(1).LogAsync(
            _userId,
            "Delete",
            nameof(ApplicationUser),
            _userId,
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_CancellableSubscriptionAndBillingNotConfigured_Returns503_AndDeletesNothing()
    {
        var user = EntityBuilder.User.WithId(_userId).WithEmail("test@test.com").Build();
        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        userManager.FindByIdAsync(_userId.ToString()).Returns(user);
        userManager.DeleteAsync(user).Returns(IdentityResult.Success);

        var profile = new ProfessionalProfile { Id = 7, UserId = _userId };
        var plan = new SubscriptionPlan { Id = 1, Code = "paid", NameCs = "c", NameEn = "c", NameDe = "c", Currency = "CZK", BothRolesIncluded = false };
        var subscription = new CoachSubscription
        {
            ProfessionalProfileId = 7,
            SubscriptionPlanId = 1,
            SubscriptionPlan = plan,
            SubscriptionPlanPriceId = 1,
            Status = SubscriptionStatus.Incomplete,
            ExternalSubscriptionId = "sub_1",
        };
        var db = new MockDbBuilder().With(profile).With(subscription).Build();
        _subscriptions.IsConfigured.Returns(false);

        var ep = Factory.Create<DeleteAccountEndpoint>(
            ctx => ctx.Request.HttpContext.User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_userId))),
            userManager, db, _audit, _subscriptions);

        await ep.HandleAsync(TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(503);
        await _subscriptions.DidNotReceive().CancelNowAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await userManager.DidNotReceive().DeleteAsync(Arg.Any<ApplicationUser>());
        await db.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_CoachWithoutProviderSubscription_NeverTouchesTheProvider()
    {
        var user = EntityBuilder.User.WithId(_userId).WithEmail("test@test.com").Build();
        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        userManager.FindByIdAsync(_userId.ToString()).Returns(user);
        userManager.DeleteAsync(user).Returns(IdentityResult.Success);

        var profile = new ProfessionalProfile { Id = 7, UserId = _userId };
        var plan = new SubscriptionPlan { Id = 1, Code = "free", NameCs = "c", NameEn = "c", NameDe = "c", Currency = "CZK", BothRolesIncluded = false };
        var subscription = new CoachSubscription
        {
            ProfessionalProfileId = 7,
            SubscriptionPlanId = 1,
            SubscriptionPlan = plan,
            SubscriptionPlanPriceId = 1,
            Status = SubscriptionStatus.Active,
        };
        var db = new MockDbBuilder().With(profile).With(subscription).Build();
        _subscriptions.IsConfigured.Returns(false);

        var ep = Factory.Create<DeleteAccountEndpoint>(
            ctx => ctx.Request.HttpContext.User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_userId))),
            userManager, db, _audit, _subscriptions);

        await ep.HandleAsync(TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(204);
        await _subscriptions.DidNotReceive().CancelNowAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_IdentityDeleteFails_Returns400_AndNeverCommits()
    {
        var user = EntityBuilder.User.WithId(_userId).WithEmail("test@test.com").Build();
        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        userManager.FindByIdAsync(_userId.ToString()).Returns(user);
        userManager.DeleteAsync(user).Returns(IdentityResult.Failed(new IdentityError { Code = "X", Description = "boom" }));

        var transaction = Substitute.For<IDbContextTransaction>();
        var db = new MockDbBuilder().Build();
        db.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(transaction);

        var ep = Factory.Create<DeleteAccountEndpoint>(
            ctx => ctx.Request.HttpContext.User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_userId))),
            userManager, db, _audit, _subscriptions);

        var act = () => ep.HandleAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ValidationFailureException>();
        await transaction.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
        await _audit.DidNotReceiveWithAnyArgs().LogAsync(default, default!, default!, default, default, default, default, default);
    }

    [Fact]
    public async Task HandleAsync_IdentityDeleteSucceeds_CommitsOnce()
    {
        var user = EntityBuilder.User.WithId(_userId).WithEmail("test@test.com").Build();
        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        userManager.FindByIdAsync(_userId.ToString()).Returns(user);
        userManager.DeleteAsync(user).Returns(IdentityResult.Success);

        var transaction = Substitute.For<IDbContextTransaction>();
        var db = new MockDbBuilder().Build();
        db.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(transaction);

        var ep = Factory.Create<DeleteAccountEndpoint>(
            ctx => ctx.Request.HttpContext.User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_userId))),
            userManager, db, _audit, _subscriptions);

        await ep.HandleAsync(TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(204);
        await transaction.Received(1).CommitAsync(Arg.Any<CancellationToken>());
    }
}
