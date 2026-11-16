using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.Users.RemoveCoachRole;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FormUp.Tests.Endpoints.Users;

public class RemoveCoachRoleEndpointTests
{
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();

    [Fact]
    public async Task HandleAsync_NoUserClaim_Returns401()
    {
        var ep = Factory.Create<RemoveCoachRoleEndpoint>(
            EndpointTestHelpers.CreateFakeUserManager(),
            new MockDbBuilder().Build(),
            Substitute.For<ICoachRoleStatus>(),
            Substitute.For<IAuditService>(),
            TimeProvider.System,
            Substitute.For<ICoachRoleRemover>(),
            Substitute.For<ISubscriptionService>(),
            NullLogger<RemoveCoachRoleEndpoint>.Instance);

        await ep.HandleAsync(new RemoveCoachRoleRequest { Role = "Trainer" }, CancellationToken.None);

        ep.HttpContext.Response.StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task HandleAsync_NotificationFails_StillReturns200AndNotifiesTheOtherClients()
    {
        var coachId = Guid.NewGuid();
        var failingClientId = Guid.NewGuid();
        var okClientId = Guid.NewGuid();
        var coach = new ApplicationUser { Id = coachId, Email = "coach@test.com", UserName = "coach@test.com", FirstName = "Co", LastName = "Ach" };
        var profile = new ProfessionalProfile { Id = 7, UserId = coachId };

        var db = new MockDbBuilder()
            .With(profile)
            .With(LinkTo(profile, failingClientId))
            .With(LinkTo(profile, okClientId))
            .Build();

        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        userManager.FindByIdAsync(coachId.ToString()).Returns(coach);

        var coachRoleStatus = Substitute.For<ICoachRoleStatus>();
        coachRoleStatus.GetActiveRolesAsync(coach, profile, Arg.Any<CancellationToken>())
            .Returns([AppRoles.Trainer, AppRoles.Nutritionist]);

        _notifications
            .CreateAsync(failingClientId, NotificationType.CoachRoleRemoved,
                Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));
        _notifications
            .CreateAsync(okClientId, NotificationType.CoachRoleRemoved,
                Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new Notification { RecipientUserId = okClientId, Title = "t", Body = "b" });

        var ep = Factory.Create<RemoveCoachRoleEndpoint>(
            userManager,
            db,
            coachRoleStatus,
            Substitute.For<IAuditService>(),
            TimeProvider.System,
            new CoachRoleRemover(
                db, _notifications, Substitute.For<IRealtimeNotifier>(), NullLogger<CoachRoleRemover>.Instance),
            Substitute.For<ISubscriptionService>(),
            NullLogger<RemoveCoachRoleEndpoint>.Instance);
        ep.HttpContext.User = new ClaimsPrincipal(
            new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(coachId, AppRoles.Trainer)));

        await ep.HandleAsync(new RemoveCoachRoleRequest { Role = AppRoles.Trainer }, CancellationToken.None);

        ep.HttpContext.Response.StatusCode.Should().Be(200);
        profile.TrainerRoleRemovedAt.Should().NotBeNull();
        await _notifications.Received(1).CreateAsync(
            okClientId, NotificationType.CoachRoleRemoved,
            Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_SaveFailsAfterTheScheduleWasSet_ClearsTheScheduleAndRethrows()
    {
        var provider = new FakeSubscriptionService();
        var ep = ArrangeSaveFailure(provider, withPendingDowngrade: false, out var coachId);

        var act = () => ep.HandleAsync(new RemoveCoachRoleRequest { Role = AppRoles.Trainer }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("save failed");
        provider.PlanChanges.Should().ContainSingle().Which.AtNextRenewal.Should().BeTrue();
        provider.ClearedScheduledChanges.Should().Equal("sub_1");
        coachId.Should().NotBeEmpty();
    }

    [Fact]
    public async Task HandleAsync_SaveFailsWithAScheduledDowngrade_PutsTheDowngradeBackWithItsOwnAddOn()
    {
        var provider = new FakeSubscriptionService();
        var ep = ArrangeSaveFailure(provider, withPendingDowngrade: true, out _);

        var act = () => ep.HandleAsync(new RemoveCoachRoleRequest { Role = AppRoles.Trainer }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        provider.ClearedScheduledChanges.Should().BeEmpty();
        provider.PlanChanges.Should().HaveCount(2);
        provider.PlanChanges[0].Should().BeEquivalentTo(new FakeSubscriptionService.ChangePlanCall(
            "sub_1", "downgrade", "price_downgrade", AtNextRenewal: true, AddOnAmountMinorUnits: null));
        provider.PlanChanges[1].Should().BeEquivalentTo(new FakeSubscriptionService.ChangePlanCall(
            "sub_1", "downgrade", "price_downgrade", AtNextRenewal: true, AddOnAmountMinorUnits: 4900));
    }

    private static RemoveCoachRoleEndpoint ArrangeSaveFailure(FakeSubscriptionService provider, bool withPendingDowngrade, out Guid coachId)
    {
        coachId = Guid.NewGuid();
        var coach = new ApplicationUser { Id = coachId, Email = "coach@test.com", UserName = "coach@test.com", FirstName = "Co", LastName = "Ach" };
        var profile = new ProfessionalProfile { Id = 7, UserId = coachId };
        var plan = new SubscriptionPlan { Id = 1, Code = "current", NameCs = "c", NameEn = "c", NameDe = "c", Currency = "CZK", BothRolesIncluded = false };
        var price = new SubscriptionPlanPrice { SubscriptionPlanId = 1, SubscriptionPlan = plan, Currency = "CZK", ExternalPriceId = "price_current" };
        var subscription = new CoachSubscription
        {
            ProfessionalProfileId = 7,
            SubscriptionPlanId = 1,
            SubscriptionPlan = plan,
            SubscriptionPlanPriceId = 1,
            SubscriptionPlanPrice = price,
            PaysSecondRoleAddOn = true,
            Status = SubscriptionStatus.Active,
            ExternalSubscriptionId = "sub_1",
            CurrentPeriodEndsAt = DateTimeOffset.UtcNow.AddDays(9),
        };

        if (withPendingDowngrade)
        {
            var downgrade = new SubscriptionPlan { Id = 2, Code = "downgrade", NameCs = "d", NameEn = "d", NameDe = "d", Currency = "CZK", BothRolesIncluded = false };
            subscription.PendingSubscriptionPlanPrice = new SubscriptionPlanPrice
            {
                SubscriptionPlanId = 2,
                SubscriptionPlan = downgrade,
                Currency = "CZK",
                ExternalPriceId = "price_downgrade",
                SecondRoleAddOnPriceMinorUnits = 4900,
            };
        }

        var db = new MockDbBuilder().With(profile).With(subscription).Build();
        db.SaveChangesAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("save failed"));

        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        userManager.FindByIdAsync(coachId.ToString()).Returns(coach);

        var coachRoleStatus = Substitute.For<ICoachRoleStatus>();
        coachRoleStatus.GetActiveRolesAsync(coach, profile, Arg.Any<CancellationToken>())
            .Returns([AppRoles.Trainer, AppRoles.Nutritionist]);

        var ep = Factory.Create<RemoveCoachRoleEndpoint>(
            userManager,
            db,
            coachRoleStatus,
            Substitute.For<IAuditService>(),
            TimeProvider.System,
            Substitute.For<ICoachRoleRemover>(),
            provider,
            NullLogger<RemoveCoachRoleEndpoint>.Instance);
        ep.HttpContext.User = new ClaimsPrincipal(
            new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(coachId, AppRoles.Trainer)));

        return ep;
    }

    private static ClientProfessionalLink LinkTo(ProfessionalProfile profile, Guid clientUserId) => new()
    {
        ProfessionalProfileId = profile.Id,
        ProfessionalProfile = profile,
        ClientProfile = new ClientProfile { UserId = clientUserId },
        ConnectionState = ConnectionState.Connected,
        CanViewTrainingPlans = true,
        CanViewNutritionPlans = false,
    };
}
