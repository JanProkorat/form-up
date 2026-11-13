using FastEndpoints;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.Client.Invites.GetPending;
using FormUp.Tests.Builders;
using NSubstitute;

namespace FormUp.Tests.Endpoints.Client.Invites;

/// <summary>
/// Tests for <see cref="GetPendingInviteEndpoint"/>, in particular the displayed
/// TrainerRole label for a professional holding multiple identity roles (#771).
/// </summary>
public class GetPendingInviteEndpointTests
{
    private readonly Guid _clientUserId = Guid.NewGuid();

    private static ApplicationUser CreateClientUser(Guid id, string email) => new()
    {
        Id = id,
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        FirstName = "Anna",
        LastName = "Novakova"
    };

    /// <summary>
    /// A professional holding BOTH Trainer and Nutritionist roles must show both
    /// in the pending-invite preview, not a single tie-broken role.
    /// </summary>
    [Fact]
    public async Task HandleAsync_DualRoleProfessional_ReturnsBothRolesInLabel()
    {
        var clientUser = CreateClientUser(_clientUserId, "client@example.com");

        var profUser = EntityBuilder.User.WithEmail("pro@example.com")
            .WithFirstName("Pat").WithLastName("Pro").Build();
        var professionalProfile = EntityBuilder.ProfessionalProfile.WithId(1).WithUser(profUser).Build();

        var invite = EntityBuilder.PendingInvite
            .WithProfessionalProfile(professionalProfile)
            .WithEmail("client@example.com")
            .Build();

        var db = new MockDbBuilder()
            .With(clientUser)
            .With(professionalProfile)
            .With(invite)
            .Build();

        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        userManager.GetRolesAsync(profUser).Returns(["Trainer", "Nutritionist"]);

        var ep = Factory.Create<GetPendingInviteEndpoint>(
            ctx => ctx.Request.HttpContext.User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_clientUserId, AppRoles.Client))),
            db, userManager, TimeProvider.System, EntitlementServiceBuilder.For(db));

        await ep.HandleAsync(TestContext.Current.CancellationToken);

        ep.Response.TrainerRole.Should().Be("Trainer & Nutritionist");
        ep.Response.OfferedType.Should().Be(LinkCapabilityScope.Both);
    }

    /// <summary>
    /// Single-role professional (Trainer only) — unaffected by the fix.
    /// </summary>
    [Fact]
    public async Task HandleAsync_TrainerOnlyProfessional_ReturnsSingleRole()
    {
        var clientUser = CreateClientUser(_clientUserId, "client2@example.com");

        var profUser = EntityBuilder.User.WithEmail("pro2@example.com")
            .WithFirstName("Pat").WithLastName("Pro").Build();
        var professionalProfile = EntityBuilder.ProfessionalProfile.WithId(1).WithUser(profUser).Build();

        var invite = EntityBuilder.PendingInvite
            .WithProfessionalProfile(professionalProfile)
            .WithEmail("client2@example.com")
            .Build();

        var db = new MockDbBuilder()
            .With(clientUser)
            .With(professionalProfile)
            .With(invite)
            .Build();

        var userManager = EndpointTestHelpers.CreateFakeUserManager();
        userManager.GetRolesAsync(profUser).Returns(["Trainer"]);

        var ep = Factory.Create<GetPendingInviteEndpoint>(
            ctx => ctx.Request.HttpContext.User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_clientUserId, AppRoles.Client))),
            db, userManager, TimeProvider.System, EntitlementServiceBuilder.For(db));

        await ep.HandleAsync(TestContext.Current.CancellationToken);

        ep.Response.TrainerRole.Should().Be("Trainer");
        ep.Response.OfferedType.Should().Be(LinkCapabilityScope.TrainingOnly);
    }
}
