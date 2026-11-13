using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.Users.RemoveCoachRole;
using FormUp.Tests.Builders;
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
            Substitute.For<ICoachRoleRemover>());

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
                db, _notifications, Substitute.For<IRealtimeNotifier>(), NullLogger<CoachRoleRemover>.Instance));
        ep.HttpContext.User = new ClaimsPrincipal(
            new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(coachId, AppRoles.Trainer)));

        await ep.HandleAsync(new RemoveCoachRoleRequest { Role = AppRoles.Trainer }, CancellationToken.None);

        ep.HttpContext.Response.StatusCode.Should().Be(200);
        profile.TrainerRoleRemovedAt.Should().NotBeNull();
        await _notifications.Received(1).CreateAsync(
            okClientId, NotificationType.CoachRoleRemoved,
            Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
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
