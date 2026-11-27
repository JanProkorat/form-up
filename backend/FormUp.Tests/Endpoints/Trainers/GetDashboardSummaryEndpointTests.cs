using System.Security.Claims;
using FastEndpoints;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Features.Trainers.GetDashboardSummary;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Application.Infrastructure.Services;
using FormUp.Tests.Builders;
using FormUp.Tests.Endpoints.NutritionPlans;
using MongoDB.Driver;
using NSubstitute;

namespace FormUp.Tests.Endpoints.Trainers;

/// <summary>
/// Unit tests for <see cref="GetDashboardSummaryEndpoint"/>.
/// Focuses on the <see cref="ClientDashboardItem.AvatarBlobUrl"/> projection.
/// </summary>
public class GetDashboardSummaryEndpointTests
{
    private readonly Guid _trainerUserId = Guid.NewGuid();

    // Stub compliance service — returns neutral zeros for every client.
    private static IComplianceService CreateStubComplianceService()
    {
        var svc = Substitute.For<IComplianceService>();

        svc.CalculateComplianceAsync(
                Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>())
            .Returns(new ComplianceResult
            {
                CompliancePercent = 0m,
                NutritionCompliancePercent = 0m,
                TrainingCompliancePercent = 0m
            });

        svc.CalculateStreakAsync(
                Arg.Any<Guid>(), Arg.Any<ComplianceDiscipline>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>())
            .Returns(0);

        svc.CalculateAverageMacrosAsync(
                Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new NutrientTotals { Kcal = 0m });

        return svc;
    }

    /// <summary>
    /// Builds a minimal <see cref="IMongoContext"/> that returns empty collections
    /// for every query the dashboard endpoint fires.
    /// Pre-creates all cursors before calling Returns() to avoid the NSubstitute
    /// "nested substitute inside Returns()" pitfall.
    /// </summary>
    private static IMongoContext CreateEmptyMongo()
    {
        var mongo = Substitute.For<IMongoContext>();

        // Training plans — empty
        var emptyPlanCursor = CreateEmptyCursor<TrainingPlan>();
        var planCollection = Substitute.For<IMongoCollection<TrainingPlan>>();
        planCollection.FindAsync(
                Arg.Any<FilterDefinition<TrainingPlan>>(),
                Arg.Any<FindOptions<TrainingPlan, TrainingPlan>>(),
                Arg.Any<CancellationToken>())
            .Returns(emptyPlanCursor);
        mongo.TrainingPlans.Returns(planCollection);

        // Nutrition plans — empty (FindAsync + CountDocumentsAsync)
        var emptyNutritionCursor = CreateEmptyCursor<NutritionPlan>();
        var nutritionCollection = Substitute.For<IMongoCollection<NutritionPlan>>();
        nutritionCollection.FindAsync(
                Arg.Any<FilterDefinition<NutritionPlan>>(),
                Arg.Any<FindOptions<NutritionPlan, NutritionPlan>>(),
                Arg.Any<CancellationToken>())
            .Returns(emptyNutritionCursor);
        nutritionCollection.CountDocumentsAsync(
                Arg.Any<FilterDefinition<NutritionPlan>>(),
                Arg.Any<CountOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(0L);
        mongo.NutritionPlans.Returns(nutritionCollection);

        // Meal logs — empty (projected to Guid)
        var emptyMealCursor = CreateEmptyCursor<Guid>();
        var mealLogCollection = Substitute.For<IMongoCollection<MealLog>>();
        mealLogCollection.FindAsync(
                Arg.Any<FilterDefinition<MealLog>>(),
                Arg.Any<FindOptions<MealLog, Guid>>(),
                Arg.Any<CancellationToken>())
            .Returns(emptyMealCursor);
        mongo.MealLogs.Returns(mealLogCollection);

        return mongo;
    }

    private static IAsyncCursor<T> CreateEmptyCursor<T>()
    {
        var cursor = Substitute.For<IAsyncCursor<T>>();
        cursor.Current.Returns([]);
        cursor.MoveNext(Arg.Any<CancellationToken>()).Returns(false);
        cursor.MoveNextAsync(Arg.Any<CancellationToken>()).Returns(false);
        return cursor;
    }

    private GetDashboardSummaryEndpoint CreateEndpoint(
        IApplicationDbContext db,
        IMongoContext? mongo = null,
        IComplianceService? compliance = null)
    {
        return Factory.Create<GetDashboardSummaryEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_trainerUserId, AppRoles.Trainer))),
            db,
            mongo ?? CreateEmptyMongo(),
            compliance ?? CreateStubComplianceService(),
            new MacroCalculatorService());
    }

    [Fact]
    public async Task HandleAsync_ClientWithAvatar_ReturnsAvatarBlobUrl()
    {
        // Arrange
        const string expectedUrl = "avatars/client-abc123.jpg";

        var clientUser = EntityBuilder.User
            .WithEmail("avatar@test.com")
            .WithFirstName("Alice")
            .WithLastName("Smith")
            .Build();

        // Set AvatarBlobUrl directly — the builder doesn't expose it as a fluent method
        clientUser.AvatarBlobUrl = expectedUrl;

        var trainerProfile = EntityBuilder.ProfessionalProfile
            .WithId(1)
            .WithUserId(_trainerUserId)
            .Build();

        var clientProfile = EntityBuilder.ClientProfile
            .WithId(1)
            .WithUser(clientUser)
            .Build();

        var link = EntityBuilder.ClientProfessionalLink
            .WithClientProfile(clientProfile)
            .WithProfessionalProfile(trainerProfile)
            .Build();

        var db = new MockDbBuilder()
            .With(trainerProfile)
            .With(clientProfile)
            .With(link)
            .Build();

        var ep = CreateEndpoint(db);

        // Act
        await ep.HandleAsync(TestContext.Current.CancellationToken);

        // Assert
        ep.HttpContext.Response.StatusCode.Should().Be(200);
        ep.Response.Clients.Should().ContainSingle();
        ep.Response.Clients[0].AvatarBlobUrl.Should().Be(expectedUrl);
    }

    [Fact]
    public async Task HandleAsync_ClientWithoutAvatar_ReturnsNullAvatarBlobUrl()
    {
        // Arrange
        var clientUser = EntityBuilder.User
            .WithEmail("noavatar@test.com")
            .WithFirstName("Bob")
            .WithLastName("Jones")
            .Build();

        // AvatarBlobUrl left as null (default)

        var trainerProfile = EntityBuilder.ProfessionalProfile
            .WithId(2)
            .WithUserId(_trainerUserId)
            .Build();

        var clientProfile = EntityBuilder.ClientProfile
            .WithId(2)
            .WithUser(clientUser)
            .Build();

        var link = EntityBuilder.ClientProfessionalLink
            .WithClientProfile(clientProfile)
            .WithProfessionalProfile(trainerProfile)
            .Build();

        var db = new MockDbBuilder()
            .With(trainerProfile)
            .With(clientProfile)
            .With(link)
            .Build();

        var ep = CreateEndpoint(db);

        // Act
        await ep.HandleAsync(TestContext.Current.CancellationToken);

        // Assert
        ep.HttpContext.Response.StatusCode.Should().Be(200);
        ep.Response.Clients.Should().ContainSingle();
        ep.Response.Clients[0].AvatarBlobUrl.Should().BeNull();
    }

    [Fact]
    public async Task HandleAsync_EmptyRoster_ReturnsEmptyClientsList()
    {
        // Arrange — trainer has no active client links
        var trainerProfile = EntityBuilder.ProfessionalProfile
            .WithId(99)
            .WithUserId(_trainerUserId)
            .Build();

        var db = new MockDbBuilder()
            .With(trainerProfile)
            .Build();

        var ep = CreateEndpoint(db);

        // Act
        await ep.HandleAsync(TestContext.Current.CancellationToken);

        // Assert — empty roster short-circuits to an empty list (#660 error path)
        ep.HttpContext.Response.StatusCode.Should().Be(200);
        ep.Response.Clients.Should().BeEmpty();
    }

    /// <summary>
    /// Regression guard for the #660 roster parallelization: proves that after
    /// switching the per-client loop to a bounded <c>Parallel.ForEachAsync</c>,
    /// (a) results stay index-correlated with the roster order returned by EF,
    /// (b) each client's compliance percentage comes from its own
    /// <see cref="IComplianceService"/> call (no cross-client bleed), and
    /// (c) the batched last-measurement EF lookup resolves the correct MAX
    /// measurement per client, ignoring other clients' rows entirely.
    /// </summary>
    [Fact]
    public async Task HandleAsync_MultipleClients_ReturnsIndexCorrelatedPerClientResults()
    {
        // Arrange — three clients with distinct compliance values and distinct
        // body-measurement histories (some with multiple rows, one with none).
        var clientAUser = EntityBuilder.User
            .WithEmail("alice@test.com").WithFirstName("Alice").WithLastName("A").Build();
        var clientBUser = EntityBuilder.User
            .WithEmail("bob@test.com").WithFirstName("Bob").WithLastName("B").Build();
        var clientCUser = EntityBuilder.User
            .WithEmail("carol@test.com").WithFirstName("Carol").WithLastName("C").Build();

        var trainerProfile = EntityBuilder.ProfessionalProfile
            .WithId(100)
            .WithUserId(_trainerUserId)
            .Build();

        var clientAProfile = EntityBuilder.ClientProfile.WithId(101).WithUser(clientAUser).Build();
        var clientBProfile = EntityBuilder.ClientProfile.WithId(102).WithUser(clientBUser).Build();
        var clientCProfile = EntityBuilder.ClientProfile.WithId(103).WithUser(clientCUser).Build();

        var linkA = EntityBuilder.ClientProfessionalLink
            .WithClientProfile(clientAProfile).WithProfessionalProfile(trainerProfile).Build();
        var linkB = EntityBuilder.ClientProfessionalLink
            .WithClientProfile(clientBProfile).WithProfessionalProfile(trainerProfile).Build();
        var linkC = EntityBuilder.ClientProfessionalLink
            .WithClientProfile(clientCProfile).WithProfessionalProfile(trainerProfile).Build();

        // Client A: two measurements — the later one must win.
        var clientAOlderMeasurement = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var clientANewerMeasurement = new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);
        // Client B: single measurement.
        var clientBMeasurement = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
        // Client C: no measurements at all — must resolve to null, not leak A/B's dates.

        var db = new MockDbBuilder()
            .With(trainerProfile)
            .With(clientAProfile)
            .With(clientBProfile)
            .With(clientCProfile)
            .With(linkA)
            .With(linkB)
            .With(linkC)
            .With(new BodyMeasurement { ClientProfileId = clientAProfile.Id, MeasuredAt = clientAOlderMeasurement })
            .With(new BodyMeasurement { ClientProfileId = clientAProfile.Id, MeasuredAt = clientANewerMeasurement })
            .With(new BodyMeasurement { ClientProfileId = clientBProfile.Id, MeasuredAt = clientBMeasurement })
            .Build();

        // Keyed on ApplicationUser.Id (#840) — GetDashboardSummaryEndpoint resolves each
        // client's UserId before calling ComplianceService (Mongo documents are keyed on
        // UserId, not ClientProfile.PublicId).
        var complianceByClient = new Dictionary<Guid, decimal>
        {
            [clientAProfile.UserId] = 10m,
            [clientBProfile.UserId] = 20m,
            [clientCProfile.UserId] = 30m,
        };

        // The test trainer only holds the Trainer role (see FakeUserClaims below),
        // so the endpoint resolves discipline = TrainingOnly and reads
        // TrainingCompliancePercent as percentForViewer — set all three fields
        // to the same per-client value so the assertion is robust regardless
        // of which discipline the endpoint picks.
        var complianceSvc = Substitute.For<IComplianceService>();
        complianceSvc.CalculateComplianceAsync(
                Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>())
            .Returns(callInfo =>
            {
                var percent = complianceByClient[callInfo.ArgAt<Guid>(0)];
                return new ComplianceResult
                {
                    CompliancePercent = percent,
                    NutritionCompliancePercent = percent,
                    TrainingCompliancePercent = percent,
                };
            });
        complianceSvc.CalculateStreakAsync(
                Arg.Any<Guid>(), Arg.Any<ComplianceDiscipline>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>())
            .Returns(0);
        complianceSvc.CalculateAverageMacrosAsync(
                Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new NutrientTotals { Kcal = 0m });

        var ep = CreateEndpoint(db, compliance: complianceSvc);

        // Act
        await ep.HandleAsync(TestContext.Current.CancellationToken);

        // Assert
        ep.HttpContext.Response.StatusCode.Should().Be(200);
        ep.Response.Clients.Should().HaveCount(3);

        // Response order matches the roster order returned by EF (index-correlated
        // array write in Parallel.ForEachAsync), regardless of task completion order.
        ep.Response.Clients[0].PublicId.Should().Be(clientAProfile.PublicId);
        ep.Response.Clients[1].PublicId.Should().Be(clientBProfile.PublicId);
        ep.Response.Clients[2].PublicId.Should().Be(clientCProfile.PublicId);

        // Each client's compliance came from its own ComplianceService call — no
        // cross-client bleed introduced by running the roster concurrently.
        ep.Response.Clients[0].CompliancePercent.Should().Be(10m);
        ep.Response.Clients[1].CompliancePercent.Should().Be(20m);
        ep.Response.Clients[2].CompliancePercent.Should().Be(30m);

        // The batched last-measurement EF lookup resolves the MAX per client,
        // never a value belonging to a different client.
        ep.Response.Clients[0].LastActivityAt.Should().Be(clientANewerMeasurement);
        ep.Response.Clients[1].LastActivityAt.Should().Be(clientBMeasurement);
        ep.Response.Clients[2].LastActivityAt.Should().BeNull();
    }

    /// <summary>
    /// Builds on <see cref="CreateEmptyMongo"/>, replacing only the <c>NutritionPlans</c>
    /// collection so it returns <paramref name="plan"/> for both the active-plan lookup and
    /// the active-plan-count lookup this endpoint issues.
    /// </summary>
    private static IMongoContext CreateMongoWithNutritionPlan(NutritionPlan plan)
    {
        var mongo = CreateEmptyMongo();

        var cursor = Substitute.For<IAsyncCursor<NutritionPlan>>();
        var moved = false;
        cursor.Current.Returns(new List<NutritionPlan> { plan });
        cursor.MoveNext(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (moved) return false;
            moved = true;
            return true;
        });
        cursor.MoveNextAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (moved) return false;
            moved = true;
            return true;
        });

        var nutritionCollection = Substitute.For<IMongoCollection<NutritionPlan>>();
        nutritionCollection.FindAsync(
                Arg.Any<FilterDefinition<NutritionPlan>>(),
                Arg.Any<FindOptions<NutritionPlan, NutritionPlan>>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => cursor);
        nutritionCollection.CountDocumentsAsync(
                Arg.Any<FilterDefinition<NutritionPlan>>(),
                Arg.Any<CountOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(1L);

        mongo.NutritionPlans.Returns(nutritionCollection);

        return mongo;
    }
}
