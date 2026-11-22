using FastEndpoints;
using FluentAssertions;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Features.Auth.Register;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Services;
using FormUp.Tests.Builders;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace FormUp.Tests.Endpoints.Auth;

public class RegisterEndpointTests
{
    private readonly UserManager<ApplicationUser> _userManager = EndpointTestHelpers.CreateFakeUserManager();
    private readonly IApplicationDbContext _db = new MockDbBuilder().Build();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly ILogger<RegisterEndpoint> _logger = Substitute.For<ILogger<RegisterEndpoint>>();
    private readonly FormUp.Application.Domain.Interfaces.ICoachEventRecorder _coachEvents = Substitute.For<FormUp.Application.Domain.Interfaces.ICoachEventRecorder>();

    // Real EmailVerificationTokenService wired to the same mocked _db/_emailService so
    // assertions on token persistence (_db.EmailVerificationTokens) and email dispatch
    // (_emailService.SendEmailVerificationAsync) still exercise the shared path (#679).
    private readonly IEmailVerificationTokenService _tokenService;

    public RegisterEndpointTests()
    {
        _tokenService = new EmailVerificationTokenService(_db, _emailService);
    }

    [Fact]
    public async Task HandleAsync_ValidRequest_Returns201WithUserId()
    {
        _userManager.CreateAsync(Arg.Any<ApplicationUser>(), Arg.Any<string>())
            .Returns(IdentityResult.Success);
        _userManager.AddToRolesAsync(Arg.Any<ApplicationUser>(), Arg.Any<IEnumerable<string>>())
            .Returns(IdentityResult.Success);

        var ep = Factory.Create<RegisterEndpoint>(_userManager, _db, _audit, _tokenService, _logger, _coachEvents, TimeProvider.System);

        var req = new RegisterRequest
        {
            Email = "test@example.com",
            Password = "TestPass1!",
            ConfirmPassword = "TestPass1!",
            FirstName = "John",
            LastName = "Doe",
            Roles = new List<string> { "Client" },
            GdprConsent = true
        };

        await ep.HandleAsync(req, CancellationToken.None);

        ep.HttpContext.Response.StatusCode.Should().Be(201);
        ep.Response.Email.Should().Be("test@example.com");
        ep.Response.Message.Should().Be("Registration successful.");
    }

    [Fact]
    public async Task HandleAsync_DuplicateEmail_ThrowsValidationError()
    {
        _userManager.CreateAsync(Arg.Any<ApplicationUser>(), Arg.Any<string>())
            .Returns(IdentityResult.Failed(new IdentityError { Description = "Email already taken." }));

        var ep = Factory.Create<RegisterEndpoint>(_userManager, _db, _audit, _tokenService, _logger, _coachEvents, TimeProvider.System);

        var req = new RegisterRequest
        {
            Email = "taken@example.com",
            Password = "TestPass1!",
            ConfirmPassword = "TestPass1!",
            FirstName = "John",
            LastName = "Doe",
            Roles = new List<string> { "Client" },
            GdprConsent = true
        };

        var act = () => ep.HandleAsync(req, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationFailureException>();
    }

    [Fact]
    public async Task HandleAsync_AssignsCorrectRole()
    {
        _userManager.CreateAsync(Arg.Any<ApplicationUser>(), Arg.Any<string>())
            .Returns(IdentityResult.Success);
        _userManager.AddToRolesAsync(Arg.Any<ApplicationUser>(), Arg.Any<IEnumerable<string>>())
            .Returns(IdentityResult.Success);

        var ep = Factory.Create<RegisterEndpoint>(_userManager, _db, _audit, _tokenService, _logger, _coachEvents, TimeProvider.System);

        var req = new RegisterRequest
        {
            Email = "trainer@example.com",
            Password = "TestPass1!",
            ConfirmPassword = "TestPass1!",
            FirstName = "Jane",
            LastName = "Doe",
            Roles = new List<string> { "Trainer" },
            GdprConsent = true
        };

        await ep.HandleAsync(req, CancellationToken.None);

        await _userManager.Received(1).AddToRolesAsync(
            Arg.Any<ApplicationUser>(),
            Arg.Is<IEnumerable<string>>(r => r.Contains("Trainer")));
    }

    [Theory]
    [InlineData("Trainer", 1)]
    [InlineData("Nutritionist", 1)]
    [InlineData("Client", 0)]
    public async Task HandleAsync_RecordsSignedUpForCoachesOnly(string role, int expectedEvents)
    {
        _userManager.CreateAsync(Arg.Any<ApplicationUser>(), Arg.Any<string>())
            .Returns(IdentityResult.Success);
        _userManager.AddToRolesAsync(Arg.Any<ApplicationUser>(), Arg.Any<IEnumerable<string>>())
            .Returns(IdentityResult.Success);

        var ep = Factory.Create<RegisterEndpoint>(_userManager, _db, _audit, _tokenService, _logger, _coachEvents, TimeProvider.System);

        await ep.HandleAsync(new RegisterRequest
        {
            Email = "someone@example.com",
            Password = "TestPass1!",
            ConfirmPassword = "TestPass1!",
            FirstName = "Jane",
            LastName = "Doe",
            Roles = new List<string> { role },
            GdprConsent = true,
            HealthDataConsent = role == "Client" ? true : null
        }, CancellationToken.None);

        await _coachEvents.Received(expectedEvents).RecordAsync(
            FormUp.Application.Domain.Enums.CoachEventType.SignedUp,
            Arg.Any<Guid>(),
            Arg.Is<string>(key => key.StartsWith("signup:")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ClientRole_CreatesClientProfile()
    {
        _userManager.CreateAsync(Arg.Any<ApplicationUser>(), Arg.Any<string>())
            .Returns(IdentityResult.Success);
        _userManager.AddToRolesAsync(Arg.Any<ApplicationUser>(), Arg.Any<IEnumerable<string>>())
            .Returns(IdentityResult.Success);

        var ep = Factory.Create<RegisterEndpoint>(_userManager, _db, _audit, _tokenService, _logger, _coachEvents, TimeProvider.System);

        await ep.HandleAsync(new RegisterRequest
        {
            Email = "client@example.com",
            Password = "TestPass1!",
            ConfirmPassword = "TestPass1!",
            FirstName = "John",
            LastName = "Doe",
            Roles = new List<string> { "Client" },
            GdprConsent = true
        }, CancellationToken.None);

        _db.ClientProfiles.Received(1).Add(Arg.Any<ClientProfile>());
    }

    [Fact]
    public async Task HandleAsync_TrainerRole_CreatesProfessionalProfile()
    {
        _userManager.CreateAsync(Arg.Any<ApplicationUser>(), Arg.Any<string>())
            .Returns(IdentityResult.Success);
        _userManager.AddToRolesAsync(Arg.Any<ApplicationUser>(), Arg.Any<IEnumerable<string>>())
            .Returns(IdentityResult.Success);

        var ep = Factory.Create<RegisterEndpoint>(_userManager, _db, _audit, _tokenService, _logger, _coachEvents, TimeProvider.System);

        await ep.HandleAsync(new RegisterRequest
        {
            Email = "trainer@example.com",
            Password = "TestPass1!",
            ConfirmPassword = "TestPass1!",
            FirstName = "Jane",
            LastName = "Doe",
            Roles = new List<string> { "Trainer" },
            GdprConsent = true
        }, CancellationToken.None);

        _db.ProfessionalProfiles.Received(1).Add(Arg.Any<ProfessionalProfile>());
    }

    [Fact]
    public async Task HandleAsync_SetsGdprConsentDate()
    {
        ApplicationUser? capturedUser = null;
        _userManager.CreateAsync(Arg.Do<ApplicationUser>(u => capturedUser = u), Arg.Any<string>())
            .Returns(IdentityResult.Success);
        _userManager.AddToRolesAsync(Arg.Any<ApplicationUser>(), Arg.Any<IEnumerable<string>>())
            .Returns(IdentityResult.Success);

        var ep = Factory.Create<RegisterEndpoint>(_userManager, _db, _audit, _tokenService, _logger, _coachEvents, TimeProvider.System);

        var req = new RegisterRequest
        {
            Email = "gdpr@example.com",
            Password = "TestPass1!",
            ConfirmPassword = "TestPass1!",
            FirstName = "Jane",
            LastName = "Doe",
            Roles = new List<string> { "Client" },
            GdprConsent = true
        };

        await ep.HandleAsync(req, CancellationToken.None);

        capturedUser.Should().NotBeNull();
        capturedUser!.GdprConsent.Should().BeTrue();
        capturedUser.GdprConsentDate.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task HandleAsync_WritesAuditLog()
    {
        _userManager.CreateAsync(Arg.Any<ApplicationUser>(), Arg.Any<string>())
            .Returns(IdentityResult.Success);
        _userManager.AddToRolesAsync(Arg.Any<ApplicationUser>(), Arg.Any<IEnumerable<string>>())
            .Returns(IdentityResult.Success);

        var ep = Factory.Create<RegisterEndpoint>(_userManager, _db, _audit, _tokenService, _logger, _coachEvents, TimeProvider.System);

        await ep.HandleAsync(new RegisterRequest
        {
            Email = "audit@example.com",
            Password = "TestPass1!",
            ConfirmPassword = "TestPass1!",
            FirstName = "Jane",
            LastName = "Doe",
            Roles = new List<string> { "Client" },
            GdprConsent = true
        }, CancellationToken.None);

        await _audit.Received(1).LogAsync(
            Arg.Any<Guid?>(),
            "Register",
            nameof(ApplicationUser),
            Arg.Any<Guid?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Is<string?>(s => s != null && s.Contains("gdprConsent")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_TrainerAndNutritionistRoles_CreatesOneProfessionalProfile_AndAssignsBothRoles()
    {
        _userManager.CreateAsync(Arg.Any<ApplicationUser>(), Arg.Any<string>())
            .Returns(IdentityResult.Success);
        _userManager.AddToRolesAsync(Arg.Any<ApplicationUser>(), Arg.Any<IEnumerable<string>>())
            .Returns(IdentityResult.Success);

        var ep = Factory.Create<RegisterEndpoint>(_userManager, _db, _audit, _tokenService, _logger, _coachEvents, TimeProvider.System);

        await ep.HandleAsync(new RegisterRequest
        {
            Email = "dual@example.com",
            Password = "TestPass1!",
            ConfirmPassword = "TestPass1!",
            FirstName = "Alex",
            LastName = "Smith",
            Roles = new List<string> { "Trainer", "Nutritionist" },
            GdprConsent = true
        }, CancellationToken.None);

        // Both Trainer and Nutritionist roles are assigned in a single call
        await _userManager.Received(1).AddToRolesAsync(
            Arg.Any<ApplicationUser>(),
            Arg.Is<IEnumerable<string>>(r => r.Contains("Trainer") && r.Contains("Nutritionist")));

        // Exactly ONE ProfessionalProfile row is created (not two)
        _db.ProfessionalProfiles.Received(1).Add(Arg.Any<ProfessionalProfile>());

        // No ClientProfile should be created
        _db.ClientProfiles.DidNotReceive().Add(Arg.Any<ClientProfile>());
    }

    [Fact]
    public async Task HandleAsync_EmailSendFails_StillReturns201_AndUserIsCreated()
    {
        _userManager.CreateAsync(Arg.Any<ApplicationUser>(), Arg.Any<string>())
            .Returns(IdentityResult.Success);
        _userManager.AddToRolesAsync(Arg.Any<ApplicationUser>(), Arg.Any<IEnumerable<string>>())
            .Returns(IdentityResult.Success);

        _emailService
            .SendEmailVerificationAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("smtp down"));

        var ep = Factory.Create<RegisterEndpoint>(_userManager, _db, _audit, _tokenService, _logger, _coachEvents, TimeProvider.System);

        var req = new RegisterRequest
        {
            Email = "noemail@example.com",
            Password = "TestPass1!",
            ConfirmPassword = "TestPass1!",
            FirstName = "John",
            LastName = "Doe",
            Roles = new List<string> { "Client" },
            GdprConsent = true
        };

        await ep.HandleAsync(req, CancellationToken.None);

        ep.HttpContext.Response.StatusCode.Should().Be(201);

        await _userManager.Received(1).CreateAsync(Arg.Any<ApplicationUser>(), Arg.Any<string>());

        _db.EmailVerificationTokens.Received(1).Add(Arg.Any<EmailVerificationToken>());

        _logger.Received(1).Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("noemail@example.com")),
            Arg.Is<Exception>(ex => ex is InvalidOperationException && ex.Message == "smtp down"),
            Arg.Any<Func<object, Exception?, string>>());
    }

    // Pending-invite conversation seeding (#803/#817) no longer runs from RegisterEndpoint —
    // moved to VerifyEmailEndpoint for #1100 (R4: invite threads exist only for verified
    // accounts). See VerifyEmailEndpointTests for the equivalent coverage.
}
