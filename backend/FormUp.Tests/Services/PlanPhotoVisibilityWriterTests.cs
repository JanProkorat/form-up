using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FormUp.Tests.Services;

/// <summary>
/// Real-database tests for <see cref="PlanPhotoVisibilityWriter"/>, including the no-plan branch
/// that no upload endpoint exposes.
/// </summary>
[Collection(TestCollection.Shard3)]
public class PlanPhotoVisibilityWriterTests(FitnessApiFactory factory)
{
    private async Task<List<Guid>> WriteAsync(
        Actor client, PlanPhotoCategory category, Guid? planId, Guid? creator)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var photo = new PlanPhoto
        {
            ClientProfileId = client.ProfileId,
            Category = category,
            PlanId = planId,
            BlobUrl = $"https://example.invalid/{Guid.NewGuid():N}.jpg",
            TakenAt = DateTime.UtcNow,
            UploadedByUserId = client.UserId,
        };
        db.PlanPhotos.Add(photo);

        await PlanPhotoVisibilityWriter.AddViewersAsync(
            db, NullLogger.Instance, [photo], creator, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return await db.PlanPhotoViewers
            .AsNoTracking()
            .Where(viewer => viewer.PlanPhotoId == photo.Id)
            .Select(viewer => viewer.ProfessionalUserId)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AddViewers_UnlinkedBodyPhoto_WritesRowsForLiveCapableLinksOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await TestActors.Client(factory).CreateAsync(ct);
        var liveTrainer = await TestActors.Trainer(factory).CreateAsync(ct);
        var liveNutritionist = await TestActors.Nutritionist(factory).CreateAsync(ct);
        var inactiveCoach = await TestActors.Trainer(factory).CreateAsync(ct);
        var grantsNothingCoach = await TestActors.Trainer(factory).CreateAsync(ct);
        await TestActors.Link(factory, liveTrainer, client).CanViewNutritionPlans(false).CreateAsync(ct);
        await TestActors.Link(factory, liveNutritionist, client).CanViewTrainingPlans(false).CreateAsync(ct);
        await TestActors.Link(factory, inactiveCoach, client).Inactive().CreateAsync(ct);
        await TestActors.Link(factory, grantsNothingCoach, client)
            .CanViewNutritionPlans(false).CanViewTrainingPlans(false).CreateAsync(ct);

        var viewers = await WriteAsync(client, PlanPhotoCategory.Body, planId: null, creator: null);

        viewers.Should().BeEquivalentTo([liveTrainer.UserId, liveNutritionist.UserId]);
    }

    [Fact]
    public async Task AddViewers_BodyPhotoCarryingPlanId_TreatedAsUnlinkedNotPlanCreator()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await TestActors.Client(factory).CreateAsync(ct);
        var planCreator = await TestActors.Nutritionist(factory).CreateAsync(ct);
        var linkedTrainer = await TestActors.Trainer(factory).CreateAsync(ct);
        await TestActors.Link(factory, linkedTrainer, client).CreateAsync(ct);

        var viewers = await WriteAsync(
            client, PlanPhotoCategory.Body, planId: Guid.NewGuid(), creator: planCreator.UserId);

        viewers.Should().BeEquivalentTo([linkedTrainer.UserId]);
    }

    [Theory]
    [InlineData(PlanPhotoCategory.Food)]
    [InlineData(PlanPhotoCategory.Training)]
    public async Task AddViewers_DomainPhoto_WritesRowForPlanCreatorEvenWhenLinkIsInactive(
        PlanPhotoCategory category)
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await TestActors.Client(factory).CreateAsync(ct);
        var planCreator = await TestActors.Trainer(factory).CreateAsync(ct);
        var otherLinkedCoach = await TestActors.Nutritionist(factory).CreateAsync(ct);
        await TestActors.Link(factory, planCreator, client).Inactive().CreateAsync(ct);
        await TestActors.Link(factory, otherLinkedCoach, client).CreateAsync(ct);

        var viewers = await WriteAsync(client, category, planId: Guid.NewGuid(), creator: planCreator.UserId);

        viewers.Should().BeEquivalentTo([planCreator.UserId]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task AddViewers_DomainPhotoWithoutCreator_WritesNoRow(string? creatorText)
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await TestActors.Client(factory).CreateAsync(ct);
        var linkedCoach = await TestActors.Nutritionist(factory).CreateAsync(ct);
        await TestActors.Link(factory, linkedCoach, client).CreateAsync(ct);
        Guid? creator = creatorText is null ? null : Guid.Parse(creatorText);

        var viewers = await WriteAsync(client, PlanPhotoCategory.Food, planId: Guid.NewGuid(), creator: creator);

        viewers.Should().BeEmpty();
    }
}
