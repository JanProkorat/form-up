using System.Security.Claims;
using FastEndpoints;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Features.ClientMeasurements.AddClientMeasurement;
using FormUp.Tests.Builders;
using NSubstitute;

namespace FormUp.Tests.Endpoints.ClientMeasurements;

/// <summary>
/// Tests for <see cref="AddClientMeasurementEndpoint"/>.
/// </summary>
public class AddClientMeasurementEndpointTests
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
    public async Task HandleAsync_ActiveLink_CreatesMeasurement()
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

        var db = new MockDbBuilder()
            .With(trainerProfile)
            .With(clientProfile)
            .With(link)
            .Build();

        var audit = Substitute.For<IAuditService>();

        var ep = Factory.Create<AddClientMeasurementEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_trainerId, AppRoles.Trainer)))),
            db, audit, EndpointTestHelpers.CreateGrantingLinkAuthorizationService());

        var request = new AddClientMeasurementRequest
        {
            ClientId = _clientPublicId,
            MeasuredAt = DateTime.UtcNow,
            WeightKg = 82.5m,
            BodyFatPercentage = 18.5m,
            WaistCm = 85,
            Notes = "Measured in person during session"
        };

        await ep.HandleAsync(request, TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(201);
        db.BodyMeasurements.Received(1).Add(Arg.Is<BodyMeasurement>(m =>
            m.ClientProfileId == 2 &&
            m.WeightKg == 82.5m &&
            m.Notes == "Measured in person during session"));

        await audit.Received(1).LogAsync(
            _trainerId,
            "Create",
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

        var ep = Factory.Create<AddClientMeasurementEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_trainerId, AppRoles.Trainer)))),
            db, audit, CreateDenyingLinkAuthorizationService());

        await ep.HandleAsync(
            new AddClientMeasurementRequest { ClientId = _clientPublicId, MeasuredAt = DateTime.UtcNow, WeightKg = 80 },
            TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(404);
        db.BodyMeasurements.DidNotReceive().Add(Arg.Any<BodyMeasurement>());
    }

    [Fact]
    public async Task HandleAsync_InactiveLink_Returns404()
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
            .Inactive()
            .Build();

        var db = new MockDbBuilder()
            .With(trainerProfile)
            .With(clientProfile)
            .With(link)
            .Build();

        var audit = Substitute.For<IAuditService>();

        var ep = Factory.Create<AddClientMeasurementEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_trainerId, AppRoles.Trainer)))),
            db, audit, CreateDenyingLinkAuthorizationService());

        await ep.HandleAsync(
            new AddClientMeasurementRequest { ClientId = _clientPublicId, MeasuredAt = DateTime.UtcNow, WeightKg = 80 },
            TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(404);
        db.BodyMeasurements.DidNotReceive().Add(Arg.Any<BodyMeasurement>());
    }

    [Fact]
    public async Task HandleAsync_NoClientProfile_Returns404()
    {
        var trainerProfile = EntityBuilder.ProfessionalProfile
            .WithUserId(_trainerId)
            .WithId(1)
            .Build();

        var db = new MockDbBuilder()
            .With(trainerProfile)
            .Build();

        var audit = Substitute.For<IAuditService>();

        var ep = Factory.Create<AddClientMeasurementEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_trainerId, AppRoles.Trainer)))),
            db, audit, EndpointTestHelpers.CreateGrantingLinkAuthorizationService());

        await ep.HandleAsync(
            new AddClientMeasurementRequest { ClientId = _clientPublicId, MeasuredAt = DateTime.UtcNow, WeightKg = 80 },
            TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(404);
    }

    [Fact]
    public void Validator_NoMeasurementValuesProvided_HasValidationError()
    {
        var validator = new AddClientMeasurementValidator();

        var result = validator.Validate(new AddClientMeasurementRequest
        {
            ClientId = _clientPublicId,
            MeasuredAt = DateTime.UtcNow
        });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "At least one measurement value must be provided.");
    }

    [Fact]
    public void Validator_MeasuredAtInFuture_HasValidationError()
    {
        var validator = new AddClientMeasurementValidator();

        var result = validator.Validate(new AddClientMeasurementRequest
        {
            ClientId = _clientPublicId,
            MeasuredAt = DateTime.UtcNow.AddDays(1),
            WeightKg = 80
        });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "MeasuredAt cannot be in the future.");
    }
}
