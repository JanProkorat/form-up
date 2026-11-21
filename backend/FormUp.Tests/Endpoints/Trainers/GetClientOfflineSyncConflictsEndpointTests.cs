using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.Trainers.GetClientOfflineSyncConflicts;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentValidation.TestHelper;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FormUp.Tests.Endpoints.Trainers;

/// <summary>
/// Tests for <c>GET /trainer/clients/{ClientId}/offline-sync-conflicts</c>.
/// </summary>
[Collection(TestCollection.Shard4)]
public class GetClientOfflineSyncConflictsEndpointTests(FitnessApiFactory factory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task InsertConflictAsync(Guid clientUserId, Guid planAuthorId, Guid planId, DateTime createdAt)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        await mongo.OfflineSyncConflicts.InsertOneAsync(new OfflineSyncConflict
        {
            ExternalId = Guid.NewGuid(),
            ClientId = clientUserId,
            PlanId = planId,
            PlanAuthorId = planAuthorId,
            Operation = "LogMealEaten",
            Kind = OfflineSyncConflictKind.PlanChanged,
            MealId = Guid.NewGuid(),
            OccurredAt = createdAt,
            LogDate = createdAt.Date,
            BasedOnPlanVersion = 1,
            CurrentPlanVersion = 2,
            PlanName = "Plan",
            MealKind = MealKind.Lunch,
            MealLogged = true,
            DateCreated = createdAt,
        }, cancellationToken: Ct);
    }

    private static Task<HttpResponseMessage> GetAsync(Actor coach, Guid clientPublicId, string query = "") =>
        coach.Http.GetAsync($"/trainer/clients/{clientPublicId}/offline-sync-conflicts{query}", Ct);

    [Fact]
    public async Task Get_LinkedNutritionist_ReturnsOwnClientsConflictsNewestFirstWithTotalCount()
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var otherClient = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).AsRole(UserRole.Nutritionist).CreateAsync(Ct);
        var planId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await InsertConflictAsync(client.UserId, coach.UserId, planId, now.AddHours(-3));
        await InsertConflictAsync(client.UserId, coach.UserId, planId, now.AddHours(-1));
        await InsertConflictAsync(client.UserId, coach.UserId, planId, now.AddHours(-2));
        await InsertConflictAsync(otherClient.UserId, coach.UserId, planId, now);
        await InsertConflictAsync(client.UserId, Guid.NewGuid(), planId, now);

        var response = await GetAsync(coach, client.PublicId, "?page=1&pageSize=2");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("X-Total-Count").Single().Should().Be("3");
        var body = (await response.Content.ReadFromJsonAsync<GetClientOfflineSyncConflictsResponse>(Json, Ct))!;
        body.Items.Should().HaveCount(2);
        body.Items.Select(i => i.DetectedAt).Should().BeInDescendingOrder();
        body.Items[0].DetectedAt.Should().BeCloseTo(now.AddHours(-1), TimeSpan.FromSeconds(1));
        body.Items.Should().OnlyContain(i => i.PlanId == planId && i.Kind == OfflineSyncConflictKind.PlanChanged);
    }

    [Fact]
    public async Task Get_SeenItems_CarryTheItemIdAndAllFourMacros()
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).AsRole(UserRole.Nutritionist).CreateAsync(Ct);
        var itemId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            await mongo.OfflineSyncConflicts.InsertOneAsync(new OfflineSyncConflict
            {
                ExternalId = Guid.NewGuid(),
                ClientId = client.UserId,
                PlanId = Guid.NewGuid(),
                PlanAuthorId = coach.UserId,
                Operation = "LogMealEaten",
                Kind = OfflineSyncConflictKind.PlanChanged,
                MealId = Guid.NewGuid(),
                PlanName = "Plan",
                MealLogged = true,
                SeenItems =
                [
                    new SeenMealItem
                    {
                        ItemId = itemId, Kind = SeenMealItemKind.Food, RefId = Guid.NewGuid(), Name = "Rice", Amount = 150m,
                        Kcal = 195m, Protein = 4m, Carbs = 42m, Fat = 0.5m, Fibre = 1m,
                    },
                ],
            }, cancellationToken: Ct);
        }

        var response = await GetAsync(coach, client.PublicId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<GetClientOfflineSyncConflictsResponse>(Json, Ct))!;
        var seen = body.Items.Should().ContainSingle().Subject.SeenItems.Should().ContainSingle().Subject;
        seen.ItemId.Should().Be(itemId);
        (seen.Kcal, seen.Protein, seen.Carbs, seen.Fat, seen.Fibre).Should().Be((195m, 4m, 42m, 0.5m, 1m));
    }

    [Fact]
    public async Task Get_LegacyDocumentWithoutNewFields_LoadsAndIsUnreviewed()
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).AsRole(UserRole.Nutritionist).CreateAsync(Ct);
        var legacyId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            var raw = mongo.OfflineSyncConflicts.Database
                .GetCollection<BsonDocument>(mongo.OfflineSyncConflicts.CollectionNamespace.CollectionName);
            await raw.InsertOneAsync(new BsonDocument
            {
                { "externalId", new BsonBinaryData(legacyId, GuidRepresentation.Standard) },
                { "clientId", new BsonBinaryData(client.UserId, GuidRepresentation.Standard) },
                { "planId", new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard) },
                { "planAuthorId", new BsonBinaryData(coach.UserId, GuidRepresentation.Standard) },
                { "operation", "LogMealEaten" },
                { "kind", "PlanChanged" },
                { "mealId", new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard) },
                { "occurredAt", DateTime.UtcNow },
                { "logDate", DateTime.UtcNow.Date },
                { "basedOnPlanVersion", 1 },
                { "currentPlanVersion", 2 },
                { "planName", "Plan" },
                { "mealKind", "Lunch" },
                { "mealLogged", true },
                { "dateCreated", DateTime.UtcNow },
                { "version", 1 },
            }, cancellationToken: Ct);
        }

        var response = await GetAsync(coach, client.PublicId, "?unreviewedOnly=true");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<GetClientOfflineSyncConflictsResponse>(Json, Ct))!;
        var item = body.Items.Should().ContainSingle().Subject;
        item.ConflictId.Should().Be(legacyId);
        item.SeenItems.Should().BeEmpty();
        item.ReviewedAt.Should().BeNull();
    }

    [Fact]
    public async Task Get_PlanIdAndUnreviewedFilters_NarrowResultsAndTotalCount()
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).AsRole(UserRole.Nutritionist).CreateAsync(Ct);
        var planA = Guid.NewGuid();
        var planB = Guid.NewGuid();
        await InsertConflictAsync(client.UserId, coach.UserId, planA, DateTime.UtcNow.AddHours(-1));
        await InsertConflictAsync(client.UserId, coach.UserId, planA, DateTime.UtcNow.AddHours(-2));
        await InsertConflictAsync(client.UserId, coach.UserId, planB, DateTime.UtcNow.AddHours(-3));
        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            var oldestOfA = (await mongo.OfflineSyncConflicts
                .Find(c => c.ClientId == client.UserId && c.PlanId == planA)
                .SortBy(c => c.DateCreated)
                .FirstAsync(Ct));
            await mongo.OfflineSyncConflicts.UpdateOneAsync(
                c => c.ExternalId == oldestOfA.ExternalId,
                Builders<OfflineSyncConflict>.Update.Set(c => c.ReviewedAt, DateTime.UtcNow),
                cancellationToken: Ct);
        }

        var byPlan = await GetAsync(coach, client.PublicId, $"?planId={planA}");
        var unreviewed = await GetAsync(coach, client.PublicId, "?unreviewedOnly=true");
        var both = await GetAsync(coach, client.PublicId, $"?planId={planA}&unreviewedOnly=true");

        byPlan.Headers.GetValues("X-Total-Count").Single().Should().Be("2");
        unreviewed.Headers.GetValues("X-Total-Count").Single().Should().Be("2");
        both.Headers.GetValues("X-Total-Count").Single().Should().Be("1");
        var bothBody = (await both.Content.ReadFromJsonAsync<GetClientOfflineSyncConflictsResponse>(Json, Ct))!;
        bothBody.Items.Should().ContainSingle().Which.PlanId.Should().Be(planA);
    }

    [Fact]
    public async Task Get_SecondPage_ReturnsTheRemainder()
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).AsRole(UserRole.Nutritionist).CreateAsync(Ct);
        var planId = Guid.NewGuid();
        for (var i = 0; i < 3; i++)
        {
            await InsertConflictAsync(client.UserId, coach.UserId, planId, DateTime.UtcNow.AddMinutes(-i));
        }

        var response = await GetAsync(coach, client.PublicId, "?page=2&pageSize=2");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<GetClientOfflineSyncConflictsResponse>(Json, Ct))!
            .Items.Should().HaveCount(1);
    }

    [Fact]
    public async Task Get_NotLinked_Returns404()
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);

        (await GetAsync(coach, client.PublicId)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await GetAsync(coach, Guid.NewGuid())).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_InactiveLink_Returns404()
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).AsRole(UserRole.Nutritionist).Inactive().CreateAsync(Ct);

        (await GetAsync(coach, client.PublicId)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_TrainerOnlyLink_Returns403()
    {
        var trainer = await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, trainer, client).CreateAsync(Ct);

        (await GetAsync(trainer, client.PublicId)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Get_LinkWithoutNutritionFlag_Returns403()
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).AsRole(UserRole.Nutritionist).CanViewNutritionPlans(false).CreateAsync(Ct);

        (await GetAsync(coach, client.PublicId)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Get_PageSizeZero_Returns400()
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).AsRole(UserRole.Nutritionist).CreateAsync(Ct);

        (await GetAsync(coach, client.PublicId, "?pageSize=0")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(0, 20, ErrorCodes.OutOfRange)]
    [InlineData(1, 0, ErrorCodes.OutOfRange)]
    [InlineData(1, 101, ErrorCodes.OutOfRange)]
    public void Validate_PagingOutOfRange_FailsWithOutOfRange(int page, int pageSize, string errorCode)
    {
        var result = new GetClientOfflineSyncConflictsValidator().TestValidate(
            new GetClientOfflineSyncConflictsRequest { ClientId = Guid.NewGuid(), Page = page, PageSize = pageSize });

        result.Errors.Should().ContainSingle().Which.ErrorCode.Should().Be(errorCode);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(5, 100)]
    public void Validate_PagingInRange_Passes(int page, int pageSize)
    {
        var result = new GetClientOfflineSyncConflictsValidator().TestValidate(
            new GetClientOfflineSyncConflictsRequest { ClientId = Guid.NewGuid(), Page = page, PageSize = pageSize });

        result.IsValid.Should().BeTrue();
    }
}
