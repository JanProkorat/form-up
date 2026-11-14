using System.Security.Claims;
using FastEndpoints;
using FormUp.Tests.Builders;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FormUp.Tests.Endpoints;

/// <summary>
/// Helpers for creating mocked dependencies used in FastEndpoints unit tests.
/// </summary>
public static class EndpointTestHelpers
{
    /// <summary>
    /// Wraps a <c>Factory.Create</c> context setup so the request also carries an <see cref="EntitlementService"/>
    /// over the default mock database (a Free plan with every feature on), as the coach-feature gate requires.
    /// </summary>
    /// <param name="inner">The test's own context setup, if any.</param>
    public static Action<DefaultHttpContext> WithEntitlements(Action<DefaultHttpContext>? inner = null) =>
        context =>
        {
            inner?.Invoke(context);
            var entitlementService = EntitlementServiceBuilder.For(new MockDbBuilder().Build());
            context.AddTestServices(services => services.AddSingleton(entitlementService));
        };

    /// <summary>
    /// Creates a substitute UserManager with common default behaviors.
    /// </summary>
    public static UserManager<ApplicationUser> CreateFakeUserManager()
    {
        var store = Substitute.For<IUserStore<ApplicationUser>>();
        return Substitute.For<UserManager<ApplicationUser>>(
            store, null, null, null, null, null, null, null, null);
    }

    /// <summary>
    /// Builds the invite-terms applier the accept endpoints take, over the given database.
    /// </summary>
    public static InvitePackageTermsApplier CreateInviteTermsApplier(IApplicationDbContext db, IAuditService? audit = null)
    {
        audit ??= Substitute.For<IAuditService>();

        return new InvitePackageTermsApplier(
            db,
            Builders.EntitlementServiceBuilder.For(db),
            new ClientPaymentTermsWriter(db, audit),
            audit,
            NullLogger<InvitePackageTermsApplier>.Instance);
    }

    /// <summary>
    /// Returns claims representing an authenticated user with the given userId and role.
    /// </summary>
    public static Claim[] FakeUserClaims(Guid userId, string role = AppRoles.Client)
    {
        return
        [
            new Claim(AppClaims.UserId, userId.ToString()),
            new Claim(AppClaims.Email, "test@test.com"),
            new Claim(ClaimTypes.Role, role)
        ];
    }

    /// <summary>
    /// An <see cref="IClientLinkAuthorizationService"/> substitute that grants every link
    /// capability.
    /// </summary>
    /// <remarks>
    /// A unit test whose subject is something else — response mapping, plan computation, macro
    /// totals — injects this so it keeps testing its own subject instead of silently becoming an
    /// authorization test. Deny-path tests should NOT use this helper — they need the stub to
    /// actually return <see langword="null"/> or a <c>GrantsNothing</c> capability, so a real
    /// authorization regression fails loudly instead of a dead granting stub passing quietly
    /// (the #916 / F1–F11 failure mode).
    /// </remarks>
    /// <param name="canViewNutritionPlans">Whether the simulated link grants nutrition-domain access.</param>
    /// <param name="canViewTrainingPlans">Whether the simulated link grants training-domain access.</param>
    /// <param name="accessibleClients">
    /// What <see cref="IClientLinkAuthorizationService.GetAccessibleClientsAsync"/> returns — only
    /// consulted by the plan LIST routes, whose unit tests mock the Mongo collection and so never
    /// evaluate the resulting filter.
    /// </param>
    public static IClientLinkAuthorizationService CreateGrantingLinkAuthorizationService(
        bool canViewNutritionPlans = true,
        bool canViewTrainingPlans = true,
        IReadOnlyList<(Guid ClientUserId, LinkCapabilities Capabilities)>? accessibleClients = null)
    {
        var capabilities = new LinkCapabilities(canViewNutritionPlans, canViewTrainingPlans);
        var service = Substitute.For<IClientLinkAuthorizationService>();

        service.GetCapabilitiesByClientPublicIdAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((LinkCapabilities?)capabilities);
        service.GetCapabilitiesByClientUserIdAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((LinkCapabilities?)capabilities);
        service.GetAccessibleClientsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>(), Arg.Any<LinkCapabilityScope?>())
            .Returns(accessibleClients ?? []);

        return service;
    }
}

/// <summary>
/// Hand-rolled fixed-instant clock for deterministic day-boundary tests (#935/#955). No new
/// dependency was introduced for this — <c>Microsoft.Extensions.TimeProvider.Testing</c> is
/// explicitly out of scope, so this is a minimal <see cref="TimeProvider"/> override. Promoted
/// here (#955) from a private nested class in <c>StartWorkoutEndpointTests</c> so read-path
/// (<c>Endpoints/Client/</c>, <c>Endpoints/SessionExecutions/</c>, <c>Endpoints/ClientNutrition/</c>)
/// tests can share it too.
/// </summary>
public sealed class FixedTimeProvider(DateTimeOffset fixedUtcNow) : TimeProvider
{
    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => fixedUtcNow;
}
