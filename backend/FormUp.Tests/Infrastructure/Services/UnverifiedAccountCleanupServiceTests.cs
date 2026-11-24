using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Application.Infrastructure.Services;
using FormUp.Tests.Builders;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace FormUp.Tests.Infrastructure.Services;

/// <summary>
/// Integration tests for <see cref="UnverifiedAccountCleanupService"/>, driving
/// <c>SweepAsync</c> directly (the hosted loop is removed in test hosts, see #726).
/// </summary>
[Collection(TestCollection.Name)]
public class UnverifiedAccountCleanupServiceTests(FitnessApiFactory factory)
{
    private static readonly DateTime StaleCreatedAt = DateTime.UtcNow.AddDays(-30);

    private UnverifiedAccountCleanupService Service =>
        factory.Services.GetRequiredService<UnverifiedAccountCleanupService>();

    private FakeBlobStorageService Blobs =>
        (FakeBlobStorageService)factory.Services.GetRequiredService<IBlobStorageService>();

    private IMongoContext Mongo => factory.Services.GetRequiredService<IMongoContext>();

    private async Task<bool> UserExistsAsync(Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.Users.AnyAsync(u => u.Id == userId, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Sweep_StaleUnverifiedClient_DeletesAccountAndEverythingItOwns()
    {
        var ct = TestContext.Current.CancellationToken;
        var coach = await TestActors.Trainer(factory).Verified().CreateAsync(ct);
        var client = await TestActors.Client(factory).Unverified().CreatedAt(StaleCreatedAt).CreateAsync(ct);
        var linkId = await TestActors.Link(factory, coach, client).CreateAsync(ct);

        var diaryRequestId = Guid.NewGuid();
        var photoPath = $"plan-photos/{Guid.NewGuid():N}.jpg";
        var chatImagePath = $"chat-images/{Guid.NewGuid():N}.jpg";
        var mealPhotoPath = $"meal-photos/{Guid.NewGuid():N}.jpg";
        long conversationId;
        Guid planPhotoId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            db.PhotoDiaryRequests.Add(new PhotoDiaryRequest
            {
                Id = diaryRequestId,
                ProfessionalId = coach.UserId,
                LinkId = linkId,
            });

            var conversation = new Conversation
            {
                ProfessionalUserId = coach.UserId,
                ClientUserId = client.UserId,
            };
            conversation.Messages.Add(new ChatMessage { SenderUserId = coach.UserId, Text = "from coach" });
            conversation.Messages.Add(new ChatMessage
            {
                SenderUserId = client.UserId,
                Text = "from client",
                ImageBlobUrl = chatImagePath,
            });
            db.Conversations.Add(conversation);

            var planPhoto = new PlanPhoto
            {
                ClientProfileId = client.ProfileId,
                UploadedByUserId = client.UserId,
                Category = PlanPhotoCategory.Body,
                BlobUrl = photoPath,
                TakenAt = DateTime.UtcNow,
            };
            db.PlanPhotos.Add(planPhoto);

            await db.SaveChangesAsync(ct);
            conversationId = conversation.Id;
            planPhotoId = planPhoto.PublicId;
        }

        var planId = Guid.NewGuid();
        await Mongo.NutritionPlans.InsertOneAsync(
            new NutritionPlan { ExternalId = planId, ClientId = client.UserId, NutritionistId = coach.UserId },
            cancellationToken: ct);
        await Mongo.MealLogs.InsertOneAsync(
            new MealLog { ClientId = client.UserId, Photos = [new MealPhoto { BlobUrl = mealPhotoPath }] },
            cancellationToken: ct);

        var deleted = await Service.SweepAsync(DateTime.UtcNow, ct);

        deleted.Should().BeGreaterThanOrEqualTo(1);
        (await UserExistsAsync(client.UserId)).Should().BeFalse();
        (await UserExistsAsync(coach.UserId)).Should().BeTrue("the verified coach is not a candidate");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            (await db.ClientProfessionalLinks.AnyAsync(l => l.Id == linkId, ct)).Should().BeFalse();
            (await db.PhotoDiaryRequests.AnyAsync(r => r.Id == diaryRequestId, ct)).Should().BeFalse();
            (await db.Conversations.AnyAsync(c => c.Id == conversationId, ct)).Should().BeFalse();
            (await db.ChatMessages.AnyAsync(m => m.ConversationId == conversationId, ct)).Should().BeFalse();
            (await db.PlanPhotos.AnyAsync(p => p.PublicId == planPhotoId, ct)).Should().BeFalse();
            (await db.ProfessionalProfiles.AnyAsync(p => p.UserId == coach.UserId, ct)).Should().BeTrue();
        }

        (await Mongo.NutritionPlans.CountDocumentsAsync(
            Builders<NutritionPlan>.Filter.Eq(x => x.ClientId, client.UserId), cancellationToken: ct))
            .Should().Be(0);
        (await Mongo.MealLogs.CountDocumentsAsync(
            Builders<MealLog>.Filter.Eq(x => x.ClientId, client.UserId), cancellationToken: ct))
            .Should().Be(0);

        Blobs.DeletedPaths.Should().Contain([photoPath, chatImagePath, mealPhotoPath]);
    }

    [Fact]
    public async Task Sweep_StaleUnverifiedCoachLinkedToVerifiedClient_DeletesCoachData_KeepsClient()
    {
        var ct = TestContext.Current.CancellationToken;
        var coach = await TestActors.Trainer(factory).Unverified().CreatedAt(StaleCreatedAt).CreateAsync(ct);
        var client = await TestActors.Client(factory).Verified().CreateAsync(ct);
        var linkId = await TestActors.Link(factory, coach, client).CreateAsync(ct);

        await Mongo.TrainingPlanTemplates.InsertOneAsync(
            new TrainingPlanTemplate { ExternalId = Guid.NewGuid(), OwnerId = coach.UserId }, cancellationToken: ct);
        await Mongo.TrainingPlans.InsertOneAsync(
            new TrainingPlan { ExternalId = Guid.NewGuid(), ClientId = client.UserId, TrainerId = coach.UserId },
            cancellationToken: ct);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Conversations.Add(new Conversation { ProfessionalUserId = coach.UserId, ClientUserId = client.UserId });
            await db.SaveChangesAsync(ct);
        }

        await Service.SweepAsync(DateTime.UtcNow, ct);

        (await UserExistsAsync(coach.UserId)).Should().BeFalse();
        (await UserExistsAsync(client.UserId)).Should().BeTrue();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            (await db.ClientProfessionalLinks.AnyAsync(l => l.Id == linkId, ct)).Should().BeFalse();
            (await db.ClientProfiles.AnyAsync(p => p.Id == client.ProfileId, ct)).Should().BeTrue();
            (await db.Conversations.AnyAsync(c => c.ProfessionalUserId == coach.UserId, ct)).Should().BeFalse();
        }

        (await Mongo.TrainingPlanTemplates.CountDocumentsAsync(
            Builders<TrainingPlanTemplate>.Filter.Eq(x => x.OwnerId, coach.UserId), cancellationToken: ct))
            .Should().Be(0);
        (await Mongo.TrainingPlans.CountDocumentsAsync(
            Builders<TrainingPlan>.Filter.Eq(x => x.TrainerId, coach.UserId), cancellationToken: ct))
            .Should().Be(0);
    }

    [Fact]
    public async Task Sweep_VerifiedAdminAndTooYoungUnverifiedUsers_AreUntouched()
    {
        var ct = TestContext.Current.CancellationToken;
        var verifiedOld = await TestActors.Client(factory).Verified().CreatedAt(StaleCreatedAt).CreateAsync(ct);
        var adminOld = await TestActors.Professional(factory, UserRole.Admin).CreatedAt(StaleCreatedAt).CreateAsync(ct);
        var youngUnverified = await TestActors.Client(factory).Unverified().CreatedAt(DateTime.UtcNow.AddDays(-2)).CreateAsync(ct);

        await Service.SweepAsync(DateTime.UtcNow, ct);

        (await UserExistsAsync(verifiedOld.UserId)).Should().BeTrue();
        (await UserExistsAsync(adminOld.UserId)).Should().BeTrue();
        (await UserExistsAsync(youngUnverified.UserId)).Should().BeTrue();
    }
}
