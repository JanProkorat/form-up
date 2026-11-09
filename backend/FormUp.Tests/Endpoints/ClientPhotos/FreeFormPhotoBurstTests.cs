using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using FastEndpoints;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Features.ClientPhotos.FinalizeUnlinkedPhoto;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace FormUp.Tests.Endpoints.ClientPhotos;

/// <summary>
/// Both finalize endpoints open or extend the coach's grouped-email burst for free-form progress photos only.
/// </summary>
[Collection(TestCollection.Name)]
public class FreeFormPhotoBurstTests(FitnessApiFactory factory)
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private async Task<List<PendingNotificationEmail>> PendingAsync(Guid recipientUserId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.PendingNotificationEmails.AsNoTracking()
            .Where(p => p.RecipientUserId == recipientUserId && p.Event == NotificationEvent.ProgressPhotosSubmitted)
            .ToListAsync(Ct);
    }

    private static async Task<HttpResponseMessage> FinalizeUnlinkedAsync(Actor client, string category) =>
        await client.Http.PostAsJsonAsync(
            "/client/photos",
            new { blobUrl = $"plan-photos/unlinked/{client.UserId}/{Guid.NewGuid()}.jpg", category },
            Ct);

    private async Task<Guid> SeedNutritionPlanAsync(Actor client, Actor coach)
    {
        var planId = Guid.NewGuid();
        var mongo = factory.Services.GetRequiredService<IMongoContext>();

        await mongo.NutritionPlans.InsertOneAsync(new NutritionPlan
        {
            ExternalId = planId,
            ClientId = client.UserId,
            NutritionistId = coach.UserId,
            Status = NutritionPlanStatus.Active,
        }, cancellationToken: Ct);

        return planId;
    }

    private static async Task<HttpResponseMessage> FinalizePlanPhotoAsync(
        Actor client, Guid planId, string category, Guid? diaryRequestId = null) =>
        await client.Http.PostAsJsonAsync(
            $"/client/plans/{planId}/photos",
            new
            {
                BlobUrl = $"plan-photos/{planId}/{Guid.NewGuid()}.jpg",
                Category = category,
                DiaryRequestId = diaryRequestId,
            },
            Ct);

    [Fact]
    public async Task FinalizeUnlinkedPhoto_TwoPhotosForALinkedCoach_OpenOneBurstGroupedByTheClient()
    {
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var formerCoach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).CreateAsync(Ct);
        await TestActors.Link(factory, formerCoach, client).Inactive().CreateAsync(Ct);

        (await FinalizeUnlinkedAsync(client, "Body")).StatusCode.Should().Be(HttpStatusCode.Created);
        (await FinalizeUnlinkedAsync(client, "FreeForm")).StatusCode.Should().Be(HttpStatusCode.Created);

        var burst = (await PendingAsync(coach.UserId)).Should().ContainSingle("two photos are one burst").Subject;
        burst.GroupId.Should().Be(client.PublicId);
        burst.LastEventAt.Should().BeAfter(burst.FirstEventAt);
        (await PendingAsync(formerCoach.UserId)).Should().BeEmpty("a coach without a live link has no viewer row");
    }

    [Fact]
    public async Task FinalizePlanPhoto_BodyPhotoButNotFoodPhoto_OpensTheBurst()
    {
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).CreateAsync(Ct);
        var planId = await SeedNutritionPlanAsync(client, coach);

        (await FinalizePlanPhotoAsync(client, planId, "Food")).StatusCode.Should().Be(HttpStatusCode.Created);
        (await PendingAsync(coach.UserId)).Should().BeEmpty("a meal photo is not a progress photo");

        (await FinalizePlanPhotoAsync(client, planId, "Body")).StatusCode.Should().Be(HttpStatusCode.Created);

        (await PendingAsync(coach.UserId)).Should().ContainSingle().Which.GroupId.Should().Be(client.PublicId);
    }

    [Fact]
    public async Task FinalizePlanPhoto_AnswerToARequest_OpensNoBurst()
    {
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var linkId = await TestActors.Link(factory, coach, client).CreateAsync(Ct);
        var planId = await SeedNutritionPlanAsync(client, coach);
        var requestId = Guid.NewGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.PhotoDiaryRequests.Add(new PhotoDiaryRequest
            {
                Id = requestId,
                ProfessionalId = coach.UserId,
                LinkId = linkId,
                PlanId = planId,
                DurationDays = 7,
                Status = PhotoDiaryStatus.InProgress,
                Mode = PhotoDiaryMode.Workflow,
                AcceptedAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(Ct);
        }

        (await FinalizePlanPhotoAsync(client, planId, "Body", requestId)).StatusCode.Should().Be(HttpStatusCode.Created);

        (await PendingAsync(coach.UserId)).Should().BeEmpty("a request answer notifies through its own submit");
    }

    [Fact]
    public async Task FinalizePlanPhoto_ClientWithNoLiveCoachLink_Returns201AndOpensNoBurst()
    {
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).Inactive().CreateAsync(Ct);
        var planId = await SeedNutritionPlanAsync(client, coach);

        (await FinalizePlanPhotoAsync(client, planId, "Body")).StatusCode.Should().Be(HttpStatusCode.Created);

        (await PendingAsync(coach.UserId)).Should().BeEmpty();
    }

    [Fact]
    public async Task FinalizeUnlinkedPhoto_BurstUpsertFails_StillReturns201()
    {
        var clientUserId = Guid.NewGuid();
        var coachUserId = Guid.NewGuid();
        var db = new MockDbBuilder()
            .With(new ClientProfile { UserId = clientUserId, PublicId = Guid.NewGuid() })
            .With(new PlanPhotoViewer { PlanPhotoId = 0, ProfessionalUserId = coachUserId })
            .Build();
        db.UpsertPendingNotificationEmailAsync(
                Arg.Any<Guid>(), Arg.Any<NotificationEvent>(), Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("queue down")));
        var endpoint = Factory.Create<FinalizeUnlinkedPhotoEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(clientUserId, AppRoles.Client))),
            db,
            Substitute.For<IRealtimeNotifier>(),
            Substitute.For<IClientLinkAuthorizationService>(),
            Substitute.For<ILogger<FinalizeUnlinkedPhotoEndpoint>>(),
            new FakeBlobStorageService());

        await endpoint.HandleAsync(new FinalizeUnlinkedPhotoRequest
        {
            BlobUrl = $"plan-photos/unlinked/{clientUserId}/{Guid.NewGuid()}.jpg",
            Category = PlanPhotoCategory.Body,
        }, Ct);

        endpoint.HttpContext.Response.StatusCode.Should().Be(201);
        await db.Received(1).UpsertPendingNotificationEmailAsync(
            coachUserId, NotificationEvent.ProgressPhotosSubmitted, Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }
}
