using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FormUp.Tests.Endpoints.NutritionPlans;

/// <summary>
/// Testcontainers integration tests (real MongoDB) for #839 — publishing a nutrition plan week
/// now persists via a targeted <c>FindOneAndUpdateAsync</c> + arrayFilters <c>$set</c> instead of
/// a full-document version-gated <c>ReplaceOneAsync</c>. NSubstitute mocks cannot prove real
/// arrayFilters/$set semantics (see <see cref="PublishWeekEndpointTests"/> for the endpoint-logic
/// unit tests) — this file is the load-bearing proof against a real Mongo instance.
/// </summary>
[Collection(TestCollection.Shard4)]
public class PublishWeekConcurrencyIntegrationTests(FitnessApiFactory factory)
{
    private static string UniqueEmail() => $"{Guid.NewGuid():N}@publish-week-test.com";

    /// <summary>
    /// Registers a nutritionist plus a client they are actively linked to. Plan-addressed routes
    /// authorize on the live link, so the linked client's user id is what every seeded plan's
    /// <c>ClientId</c> must carry for the endpoint to reach its own subject.
    /// </summary>
    private async Task<(HttpClient Client, Guid NutritionistId, Guid LinkedClientId)> RegisterNutritionistAsync()
    {
        var client = factory.CreateClient();
        var email = UniqueEmail();
        await TestHelpers.RegisterAsync(client, email, "TestPass1!", "Publish", "WeekTest", "Nutritionist");
        var (accessToken, _) = await TestHelpers.LoginAsync(client, email, "TestPass1!");
        TestHelpers.SetBearerToken(client, accessToken);

        Guid nutritionistId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.FirstAsync(
                u => u.Email == email, TestContext.Current.CancellationToken);
            nutritionistId = user.Id;
        }

        var linkedClientId = await TestHelpers.RegisterLinkedClientAsync(
            factory, nutritionistId, TestContext.Current.CancellationToken);

        return (client, nutritionistId, linkedClientId);
    }

    private async Task SeedPlanAsync(NutritionPlan plan)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        await mongo.NutritionPlans.InsertOneAsync(plan, cancellationToken: TestContext.Current.CancellationToken);
    }

    private async Task<NutritionPlan> FetchPlanAsync(Guid externalId)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        return await mongo.NutritionPlans
            .Find(p => p.ExternalId == externalId)
            .FirstAsync(TestContext.Current.CancellationToken);
    }

    private static NutritionPlan BuildDraftPlan(
        Guid nutritionistId, Guid? clientId = null, int weekCount = 2, DateTime? startDate = null) => new()
    {
        ExternalId = Guid.NewGuid(),
        ClientId = clientId ?? Guid.NewGuid(),
        NutritionistId = nutritionistId,
        Name = "Publish Week Test Plan",
        Status = NutritionPlanStatus.Draft,
        StartDate = startDate ?? DateTime.UtcNow.Date,
        Version = 1,
        DateCreated = DateTime.UtcNow.AddDays(-1),
        Weeks = Enumerable.Range(1, weekCount)
            .Select(w => new PlanWeek { WeekNumber = w, Status = WeekStatus.Draft })
            .ToList()
    };

    // ── AC#4 headline: concurrent unrelated Version bump must NOT false-409 ──────

    [Fact]
    public async Task Publish_ConcurrentUnrelatedVersionBump_Returns200_NoFalseConflict()
    {
        var (client, nutritionistId, linkedClientId) = await RegisterNutritionistAsync();
        var plan = BuildDraftPlan(nutritionistId, clientId: linkedClientId);
        await SeedPlanAsync(plan);

        // Simulate a concurrent unrelated edit that bumped the document Version — e.g. the
        // nutritionist tweaked week 2's macros in another tab. The plan being published (week 1)
        // is untouched by this bump.
        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            var bump = Builders<NutritionPlan>.Update.Inc(p => p.Version, 1);
            await mongo.NutritionPlans.UpdateOneAsync(
                p => p.ExternalId == plan.ExternalId, bump,
                cancellationToken: TestContext.Current.CancellationToken);
        }

        // The publish request is an empty JSON body (the plan id and week number come from the
        // route) and the document is now at Version 2. The publish must still succeed.
        var response = await client.PostAsync(
            $"/nutrition/plans/{plan.ExternalId}/weeks/1/publish",
            JsonContent.Create(new { }),
            TestContext.Current.CancellationToken);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            $"an unrelated concurrent Version bump must not false-409 the publish. Body: {body}");

        var updated = await FetchPlanAsync(plan.ExternalId);
        updated.Weeks.First(w => w.WeekNumber == 1).Status.Should().Be(WeekStatus.Published);
        updated.Status.Should().Be(NutritionPlanStatus.Active);
    }

    // ── sibling-archive: first publish only ──────────────────────────────────────

    [Fact]
    public async Task Publish_FirstWeek_ArchivesOverlappingSibling_SecondWeekPublish_DoesNotReArchive()
    {
        var (client, nutritionistId, clientId) = await RegisterNutritionistAsync();
        var plan = BuildDraftPlan(nutritionistId, clientId: clientId, weekCount: 2);
        await SeedPlanAsync(plan);

        var overlappingSibling = BuildDraftPlan(nutritionistId, clientId: clientId, weekCount: 2);
        overlappingSibling.Status = NutritionPlanStatus.Active;
        overlappingSibling.StartDate = plan.StartDate;
        await SeedPlanAsync(overlappingSibling);

        // ── publish week 1 → sibling must be archived (first-publish gate) ──────
        var response1 = await client.PostAsync(
            $"/nutrition/plans/{plan.ExternalId}/weeks/1/publish",
            JsonContent.Create(new { PlanId = plan.ExternalId, WeekNumber = 1, Version = 1 }),
            TestContext.Current.CancellationToken);
        response1.StatusCode.Should().Be(HttpStatusCode.OK);

        var siblingAfterFirstPublish = await FetchPlanAsync(overlappingSibling.ExternalId);
        siblingAfterFirstPublish.Status.Should().Be(
            NutritionPlanStatus.Archived,
            "the first published week must archive the overlapping Active sibling");

        // A SECOND overlapping Active sibling created AFTER week 1 was published — publishing
        // week 2 (a subsequent, not first, publish) must NOT archive it: the hadPublishedWeeks
        // gate must prevent re-triggering the archive pass.
        var secondSibling = BuildDraftPlan(nutritionistId, clientId: clientId, weekCount: 2);
        secondSibling.Status = NutritionPlanStatus.Active;
        secondSibling.StartDate = plan.StartDate;
        await SeedPlanAsync(secondSibling);

        var response2 = await client.PostAsync(
            $"/nutrition/plans/{plan.ExternalId}/weeks/2/publish",
            JsonContent.Create(new { PlanId = plan.ExternalId, WeekNumber = 2, Version = 1 }),
            TestContext.Current.CancellationToken);
        var body2 = await response2.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        response2.StatusCode.Should().Be(HttpStatusCode.OK, $"Body: {body2}");

        var secondSiblingAfter = await FetchPlanAsync(secondSibling.ExternalId);
        secondSiblingAfter.Status.Should().Be(
            NutritionPlanStatus.Active,
            "publishing a SECOND week on an already-published plan must not re-run the " +
            "first-publish sibling-archive pass");
    }

    // ── idempotency: double-publish of an already-published week ────────────────

    [Fact]
    public async Task Publish_AlreadyPublishedWeek_Returns400_Idempotent()
    {
        var (client, nutritionistId, linkedClientId) = await RegisterNutritionistAsync();
        var plan = BuildDraftPlan(nutritionistId, clientId: linkedClientId, weekCount: 1);
        await SeedPlanAsync(plan);

        var first = await client.PostAsync(
            $"/nutrition/plans/{plan.ExternalId}/weeks/1/publish",
            JsonContent.Create(new { PlanId = plan.ExternalId, WeekNumber = 1, Version = 1 }),
            TestContext.Current.CancellationToken);
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var afterFirst = await FetchPlanAsync(plan.ExternalId);
        afterFirst.Version.Should().Be(2, "exactly one successful publish must bump Version once");

        // Second publish attempt on the same, now-Published week — must be rejected cleanly
        // (400), not corrupt state (no further Version bump, no crash).
        var second = await client.PostAsync(
            $"/nutrition/plans/{plan.ExternalId}/weeks/1/publish",
            JsonContent.Create(new { PlanId = plan.ExternalId, WeekNumber = 1, Version = afterFirst.Version }),
            TestContext.Current.CancellationToken);
        second.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var afterSecond = await FetchPlanAsync(plan.ExternalId);
        afterSecond.Version.Should().Be(
            2, "a rejected double-publish must not mutate the document at all");
    }

    // ── 400: target week not found in plan ───────────────────────────────────────

    [Fact]
    public async Task Publish_TargetWeekNotFound_Returns400()
    {
        var (client, nutritionistId, linkedClientId) = await RegisterNutritionistAsync();
        var plan = BuildDraftPlan(nutritionistId, clientId: linkedClientId, weekCount: 1);
        await SeedPlanAsync(plan);

        var response = await client.PostAsync(
            $"/nutrition/plans/{plan.ExternalId}/weeks/99/publish",
            JsonContent.Create(new { PlanId = plan.ExternalId, WeekNumber = 99, Version = 1 }),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── 404: not found / not owned ────────────────────────────────────────────────

    [Fact]
    public async Task Publish_NonexistentPlan_Returns404()
    {
        var (client, _, _) = await RegisterNutritionistAsync();

        var response = await client.PostAsync(
            $"/nutrition/plans/{Guid.NewGuid()}/weeks/1/publish",
            JsonContent.Create(new { PlanId = Guid.NewGuid(), WeekNumber = 1, Version = 1 }),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Publish_WrongOwner_Returns404()
    {
        var (_, ownerNutritionistId, ownerClientId) = await RegisterNutritionistAsync();
        var (otherClient, _, _) = await RegisterNutritionistAsync();

        var plan = BuildDraftPlan(ownerNutritionistId, clientId: ownerClientId, weekCount: 1);
        await SeedPlanAsync(plan);

        // A different nutritionist attempts to publish a plan they don't own — the lookup filter
        // (ExternalId + NutritionistId) must exclude it, same as a non-existent plan.
        var response = await otherClient.PostAsync(
            $"/nutrition/plans/{plan.ExternalId}/weeks/1/publish",
            JsonContent.Create(new { PlanId = plan.ExternalId, WeekNumber = 1, Version = 1 }),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── 400: StartDate required / week start in the past ─────────────────────────

    [Fact]
    public async Task Publish_StartDateNotSet_Returns400()
    {
        var (client, nutritionistId, linkedClientId) = await RegisterNutritionistAsync();
        var plan = BuildDraftPlan(nutritionistId, clientId: linkedClientId, weekCount: 1);
        plan.StartDate = null;
        await SeedPlanAsync(plan);

        var response = await client.PostAsync(
            $"/nutrition/plans/{plan.ExternalId}/weeks/1/publish",
            JsonContent.Create(new { PlanId = plan.ExternalId, WeekNumber = 1, Version = 1 }),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Publish_WeekStartInPast_Returns400()
    {
        var (client, nutritionistId, linkedClientId) = await RegisterNutritionistAsync();
        var plan = BuildDraftPlan(nutritionistId, clientId: linkedClientId, weekCount: 1, startDate: DateTime.UtcNow.Date.AddDays(-30));
        await SeedPlanAsync(plan);

        var response = await client.PostAsync(
            $"/nutrition/plans/{plan.ExternalId}/weeks/1/publish",
            JsonContent.Create(new { PlanId = plan.ExternalId, WeekNumber = 1, Version = 1 }),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── 409: genuine same-week concurrency race ──────────────────────────────────

    /// <summary>
    /// Deterministic proof of the genuine same-week race (#839 error path 7): a competing write
    /// publishes the SAME target week between our fetch and our own write. This exercises
    /// <see cref="PlanConcurrencyGuard.UpdateWithArrayFilterGuardAsync{TDoc}"/> directly against
    /// real MongoDB — the <c>validate</c> delegate performs the "competing" write as a side
    /// effect (simulating the race window), proving the targeted write's ElemMatch/arrayFilters
    /// match ZERO documents once the week is no longer in its expected pre-mutation state, and
    /// the guard reports <see cref="PlanConcurrencyOutcome.ReplaceConflict"/> rather than
    /// silently succeeding or throwing.
    /// </summary>
    [Fact]
    public async Task Guard_ConcurrentSameWeekPublish_ArrayFilterMatchesZero_ReturnsReplaceConflict()
    {
        var nutritionistId = Guid.NewGuid();
        var plan = BuildDraftPlan(nutritionistId, weekCount: 1);
        await SeedPlanAsync(plan);

        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        var guard = new PlanConcurrencyGuard();

        var lookupFilter = Builders<NutritionPlan>.Filter.Eq(p => p.ExternalId, plan.ExternalId)
            & Builders<NutritionPlan>.Filter.Eq(p => p.NutritionistId, nutritionistId);

        var writeFilter = Builders<NutritionPlan>.Filter.Eq(p => p.ExternalId, plan.ExternalId)
            & Builders<NutritionPlan>.Filter.Eq(p => p.NutritionistId, nutritionistId)
            & Builders<NutritionPlan>.Filter.ElemMatch(p => p.Weeks,
                w => w.WeekNumber == 1 && w.Status != WeekStatus.Published);

        var update = Builders<NutritionPlan>.Update
            .Set("weeks.$[w].status", WeekStatus.Published.ToString())
            .Set("weeks.$[w].datePublished", DateTime.UtcNow)
            .Set(p => p.Status, NutritionPlanStatus.Active)
            .Set(p => p.DateUpdated, DateTime.UtcNow)
            .Inc(p => p.Version, 1);

        var arrayFilters = new List<ArrayFilterDefinition>
        {
            new BsonDocumentArrayFilterDefinition<BsonDocument>(new BsonDocument
            {
                { "w.weekNumber", 1 },
                { "w.status", new BsonDocument("$ne", WeekStatus.Published.ToString()) }
            })
        };

        var result = await guard.UpdateWithArrayFilterGuardAsync(
            mongo.NutritionPlans,
            lookupFilter,
            // This test's subject is the race window, not authorization — grant and move on.
            (_, _) => Task.FromResult(true),
            async (_, ct) =>
            {
                // Simulate a competing request publishing the SAME week right here, between our
                // fetch (above) and our own write (below) — the exact race window #839 must guard.
                var competingUpdate = Builders<NutritionPlan>.Update
                    .Set(p => p.Weeks[0].Status, WeekStatus.Published)
                    .Set(p => p.Weeks[0].DatePublished, DateTime.UtcNow)
                    .Inc(p => p.Version, 1);
                await mongo.NutritionPlans.UpdateOneAsync(
                    p => p.ExternalId == plan.ExternalId, competingUpdate, cancellationToken: ct);
                return true;
            },
            writeFilter,
            update,
            arrayFilters,
            TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(PlanConcurrencyOutcome.ReplaceConflict);
        result.Document.Should().BeNull();

        // The competing write's own mutation must be the only one reflected — our own write must
        // not have layered a second increment on top.
        var final = await FetchPlanAsync(plan.ExternalId);
        final.Version.Should().Be(2, "only the competing write's single Version increment applied");
    }
}
