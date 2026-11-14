using System.Security.Claims;
using FastEndpoints;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Features.ClientMeasurements.GetClientMeasurements;
using FormUp.Tests.Builders;
using NSubstitute;

namespace FormUp.Tests.Endpoints.ClientMeasurements;

/// <summary>
/// Tests for <see cref="GetClientMeasurementsEndpoint"/>.
/// </summary>
public class GetClientMeasurementsEndpointTests
{
    private readonly Guid _trainerId = Guid.NewGuid();
    private readonly Guid _clientPublicId = Guid.NewGuid();

    /// <summary>
    /// An <see cref="IClientLinkAuthorizationService"/> substitute that always reports "no active
    /// link" — measurements gate on presence alone (<c>capabilities is null</c>), not on a
    /// specific domain flag, so a deny test for this endpoint needs a <c>null</c>-returning stub
    /// rather than <c>EndpointTestHelpers.CreateGrantingLinkAuthorizationService</c> with a flag
    /// set to <see langword="false"/> (which still returns a non-null, GrantsNothing result).
    /// </summary>
    private static IClientLinkAuthorizationService CreateDenyingLinkAuthorizationService()
    {
        var service = Substitute.For<IClientLinkAuthorizationService>();
        service.GetCapabilitiesByClientPublicIdAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((LinkCapabilities?)null);
        return service;
    }

    [Fact]
    public async Task HandleAsync_ActiveLink_ReturnsMeasurements()
    {
        var trainerProfile = EntityBuilder.ProfessionalProfile
            .WithUserId(_trainerId)
            .WithId(1)
            .Build();

        var clientProfile = EntityBuilder.ClientProfile
            .WithPublicId(_clientPublicId)
            .WithId(2)
            .Build();

        var link = EntityBuilder.ClientProfessionalLink
            .WithClientProfileId(2)
            .WithProfessionalProfileId(1)
            .Build();

        var measurement = new BodyMeasurement
        {
            Id = 1,
            PublicId = Guid.NewGuid(),
            ClientProfileId = 2,
            MeasuredAt = DateTime.UtcNow,
            WeightKg = 75m
        };

        var db = new MockDbBuilder()
            .With(trainerProfile)
            .With(clientProfile)
            .With(link)
            .With(measurement)
            .Build();

        var audit = Substitute.For<IAuditService>();

        var ep = Factory.Create<GetClientMeasurementsEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_trainerId, AppRoles.Trainer)))),
            db, audit, EndpointTestHelpers.CreateGrantingLinkAuthorizationService());

        await ep.HandleAsync(
            new GetClientMeasurementsRequest { ClientId = _clientPublicId, Page = 1, PageSize = 10 },
            TestContext.Current.CancellationToken);

        ep.Response.Should().NotBeNull();
        ep.Response.TotalCount.Should().Be(1);
        ep.Response.Items.Should().HaveCount(1);
        ep.Response.Items[0].WeightKg.Should().Be(75m);

        // Verify audit was logged
        await audit.Received(1).LogAsync(
            _trainerId,
            "Read",
            nameof(BodyMeasurement),
            _clientPublicId,
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_NoActiveLink_Returns404()
    {
        var trainerProfile = EntityBuilder.ProfessionalProfile
            .WithUserId(_trainerId)
            .WithId(1)
            .Build();

        var clientProfile = EntityBuilder.ClientProfile
            .WithPublicId(_clientPublicId)
            .WithId(2)
            .Build();

        // No link between trainer and client
        var db = new MockDbBuilder()
            .With(trainerProfile)
            .With(clientProfile)
            .Build();

        var audit = Substitute.For<IAuditService>();

        var ep = Factory.Create<GetClientMeasurementsEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_trainerId, AppRoles.Trainer)))),
            db, audit, CreateDenyingLinkAuthorizationService());

        await ep.HandleAsync(
            new GetClientMeasurementsRequest { ClientId = _clientPublicId, Page = 1, PageSize = 10 },
            TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(404);
    }

}
