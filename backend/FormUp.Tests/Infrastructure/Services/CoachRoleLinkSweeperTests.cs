using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Application.Infrastructure.Services;
using FormUp.Tests.Builders;
using FormUp.Tests.Endpoints.ClientPayments;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FormUp.Tests.Infrastructure.Services;

/// <summary>
/// Integration tests for <see cref="CoachRoleLinkSweeper"/>, driven through <c>TickAsync</c>.
/// </summary>
[Collection(TestCollection.Name)]
public class CoachRoleLinkSweeperTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTime Today => DateTime.UtcNow.Date;

    private Task TickAsync() =>
        factory.Services.GetRequiredService<CoachRoleLinkSweeper>().TickAsync(DateTime.UtcNow, Ct);

    private async Task MarkRemovedAsync(Actor coach, UserRole role, bool removed = true)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var profile = await db.ProfessionalProfiles.FirstAsync(p => p.UserId == coach.UserId, Ct);
        var value = removed ? DateTime.UtcNow : (DateTime?)null;

        if (role == UserRole.Trainer)
        {
            profile.TrainerRoleRemovedAt = value;
        }
        else
        {
            profile.NutritionistRoleRemovedAt = value;
        }

        await db.SaveChangesAsync(Ct);
    }

    private async Task<bool> IsLinkActiveAsync(long linkId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.ClientProfessionalLinks.AsNoTracking().Where(l => l.Id == linkId).Select(l => l.ConnectionState != ConnectionState.Ended).FirstAsync(Ct);
    }

    private async Task<int> CountNotificationsAsync(Guid userId, NotificationType type)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Notifications.AsNoTracking().CountAsync(n => n.RecipientUserId == userId && n.Type == type, Ct);
    }

    private async Task<Guid> SeedTrainingPlanAsync(
        Guid clientUserId, Guid trainerUserId, DateTime startDate, int weekCount, TrainingPlanStatus status = TrainingPlanStatus.Active)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        var externalId = Guid.NewGuid();

        await mongo.TrainingPlans.InsertOneAsync(new TrainingPlan
        {
            Id = ObjectId.GenerateNewId(),
            ExternalId = externalId,
            ClientId = clientUserId,
            TrainerId = trainerUserId,
            Name = "Sweeper training plan",
            Status = status,
            StartDate = startDate,
            Weeks = Enumerable.Range(1, weekCount)
                .Select(week => new TrainingWeek { WeekNumber = week, Status = WeekStatus.Draft, Days = [] })
                .ToList(),
            Version = 1,
            DateCreated = DateTime.UtcNow,
        }, cancellationToken: Ct);

        return externalId;
    }

    private async Task<Guid> SeedNutritionPlanAsync(
        Guid clientUserId, Guid nutritionistUserId, DateTime startDate, int weekCount, NutritionPlanStatus status = NutritionPlanStatus.Active)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        var externalId = Guid.NewGuid();

        await mongo.NutritionPlans.InsertOneAsync(new NutritionPlan
        {
            Id = ObjectId.GenerateNewId(),
            ExternalId = externalId,
            ClientId = clientUserId,
            NutritionistId = nutritionistUserId,
            Name = "Sweeper nutrition plan",
            Status = status,
            StartDate = startDate,
            Weeks = Enumerable.Range(1, weekCount)
                .Select(week => new PlanWeek { WeekNumber = week, Status = WeekStatus.Draft, Days = [] })
                .ToList(),
            Version = 1,
            DateCreated = DateTime.UtcNow,
        }, cancellationToken: Ct);

        return externalId;
    }

    private async Task<TrainingPlan> ReadTrainingPlanAsync(Guid externalId)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        return await mongo.TrainingPlans.Find(p => p.ExternalId == externalId).FirstAsync(Ct);
    }

    private async Task<NutritionPlan> ReadNutritionPlanAsync(Guid externalId)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        return await mongo.NutritionPlans.Find(p => p.ExternalId == externalId).FirstAsync(Ct);
    }

    private async Task<(Actor Coach, Actor Client, long LinkId)> TrainingOnlyLinkAsync()
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var linkId = await TestActors.Link(factory, coach, client).CanViewNutritionPlans(false).CreateAsync(Ct);
        await MarkRemovedAsync(coach, UserRole.Trainer);
        return (coach, client, linkId);
    }

    [Fact]
    public async Task Tick_RunningPlanOfRemovedRole_KeepsLink()
    {
        var (coach, client, linkId) = await TrainingOnlyLinkAsync();
        var planId = await SeedTrainingPlanAsync(client.UserId, coach.UserId, Today.AddDays(-7), weekCount: 4);

        await TickAsync();

        (await IsLinkActiveAsync(linkId)).Should().BeTrue();
        (await ReadTrainingPlanAsync(planId)).Status.Should().Be(TrainingPlanStatus.Active);
    }

    [Fact]
    public async Task Tick_PlanWindowEnded_EndsLinkAndArchivesPlan()
    {
        var (coach, client, linkId) = await TrainingOnlyLinkAsync();
        var planId = await SeedTrainingPlanAsync(client.UserId, coach.UserId, Today.AddDays(-60), weekCount: 2);

        await TickAsync();

        (await IsLinkActiveAsync(linkId)).Should().BeFalse();
        var plan = await ReadTrainingPlanAsync(planId);
        plan.Status.Should().Be(TrainingPlanStatus.Archived);
        plan.Version.Should().Be(2);
    }

    [Fact]
    public async Task Tick_EndedLink_EndsTheClientsPackageAndDropsTheUpcomingPeriod()
    {
        var world = await ClientPaymentFixtures.CreateWorldAsync(factory);
        await ClientPaymentFixtures.SetTermsOkAsync(world, await ClientPaymentFixtures.CreatePackageAsync(world.Coach));
        await MarkRemovedAsync(world.Coach, UserRole.Trainer);

        await TickAsync();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var terms = await db.ClientPaymentTerms.AsNoTracking()
            .Where(t => t.ClientPaymentAssignment.ProfessionalProfileId == world.Coach.ProfileId
                && t.ClientPaymentAssignment.ClientProfileId == world.Client.ProfileId)
            .SingleAsync(Ct);
        var starts = await db.ClientPayments.AsNoTracking()
            .Where(p => p.ClientPaymentAssignment.ProfessionalProfileId == world.Coach.ProfileId
                && p.ClientPaymentAssignment.ClientProfileId == world.Client.ProfileId)
            .Select(p => p.PeriodStart)
            .ToListAsync(Ct);

        terms.EndsAtUtc.Should().NotBeNull();
        starts.Should().Equal(ClientPaymentFixtures.Today);
    }

    [Fact]
    public async Task Tick_NoPlanAtAll_EndsLinkOnFirstTick()
    {
        var (_, _, linkId) = await TrainingOnlyLinkAsync();

        await TickAsync();

        (await IsLinkActiveAsync(linkId)).Should().BeFalse();
    }

    [Fact]
    public async Task Tick_LinkAlsoGrantsOtherActiveRole_SurvivesWithFlagsUntouched()
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var linkId = await TestActors.Link(factory, coach, client).CreateAsync(Ct);
        await MarkRemovedAsync(coach, UserRole.Trainer);

        await TickAsync();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var link = await db.ClientProfessionalLinks.AsNoTracking().FirstAsync(l => l.Id == linkId, Ct);
        link.ConnectionState.Should().Be(ConnectionState.Connected);
        link.CanViewTrainingPlans.Should().BeTrue();
        link.CanViewNutritionPlans.Should().BeTrue();
    }

    [Fact]
    public async Task Tick_RoleAddedBackBeforeTick_KeepsLink()
    {
        var (coach, _, linkId) = await TrainingOnlyLinkAsync();
        await MarkRemovedAsync(coach, UserRole.Trainer, removed: false);

        await TickAsync();

        (await IsLinkActiveAsync(linkId)).Should().BeTrue();
    }

    [Fact]
    public async Task Tick_ArchivesOnlyTheEndedDisciplinesPlans()
    {
        var (coach, client, linkId) = await TrainingOnlyLinkAsync();
        var trainingPlanId = await SeedTrainingPlanAsync(client.UserId, coach.UserId, Today.AddDays(-60), weekCount: 2);
        var nutritionPlanId = await SeedNutritionPlanAsync(
            client.UserId, coach.UserId, Today.AddDays(-60), weekCount: 2, NutritionPlanStatus.Draft);

        await TickAsync();

        (await IsLinkActiveAsync(linkId)).Should().BeFalse();
        (await ReadTrainingPlanAsync(trainingPlanId)).Status.Should().Be(TrainingPlanStatus.Archived);
        var nutritionPlan = await ReadNutritionPlanAsync(nutritionPlanId);
        nutritionPlan.Status.Should().Be(NutritionPlanStatus.Draft);
        nutritionPlan.Version.Should().Be(1);
    }

    [Fact]
    public async Task Tick_EndedLink_NotifiesBothSidesOnce_AndSecondTickDoesNothing()
    {
        var (coach, client, linkId) = await TrainingOnlyLinkAsync();

        await TickAsync();
        await TickAsync();

        (await IsLinkActiveAsync(linkId)).Should().BeFalse();
        (await CountNotificationsAsync(client.UserId, NotificationType.CollaborationEndedByRoleRemoval)).Should().Be(1);
        (await CountNotificationsAsync(coach.UserId, NotificationType.CollaborationEndedByRoleRemovalCoach)).Should().Be(1);
    }

    private async Task SetCoachAccountActiveUntilAsync(Actor coach, DateTime activeUntil)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var profile = await db.ProfessionalProfiles.FirstAsync(p => p.UserId == coach.UserId, Ct);
        profile.CoachAccountActiveUntil = activeUntil;
        await db.SaveChangesAsync(Ct);
    }

    private async Task<ProfessionalProfile> ReadProfileAsync(Actor coach)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.ProfessionalProfiles.AsNoTracking().FirstAsync(p => p.UserId == coach.UserId, Ct);
    }

    [Fact]
    public async Task Tick_CoachAccountEnded_RemovesBothRoles_NotifiesClientOnce_AndEndsLinkInSameTick()
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var linkId = await TestActors.Link(factory, coach, client).CreateAsync(Ct);
        await SetCoachAccountActiveUntilAsync(coach, DateTime.UtcNow.AddMinutes(-5));

        await TickAsync();

        var profile = await ReadProfileAsync(coach);
        profile.TrainerRoleRemovedAt.Should().NotBeNull();
        profile.NutritionistRoleRemovedAt.Should().NotBeNull();
        (await CountNotificationsAsync(client.UserId, NotificationType.CoachRoleRemoved)).Should().Be(1);
        (await IsLinkActiveAsync(linkId)).Should().BeFalse();
    }

    [Fact]
    public async Task Tick_CoachAccountEnded_SecondTickChangesNothing()
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).CreateAsync(Ct);
        await SetCoachAccountActiveUntilAsync(coach, DateTime.UtcNow.AddMinutes(-5));

        await TickAsync();
        var afterFirst = await ReadProfileAsync(coach);
        await TickAsync();
        var afterSecond = await ReadProfileAsync(coach);

        afterSecond.TrainerRoleRemovedAt.Should().Be(afterFirst.TrainerRoleRemovedAt);
        afterSecond.NutritionistRoleRemovedAt.Should().Be(afterFirst.NutritionistRoleRemovedAt);
        (await CountNotificationsAsync(client.UserId, NotificationType.CoachRoleRemoved)).Should().Be(1);
    }

    private async Task<List<long>> SelectEndedProfileIdsAsync() =>
        await factory.Services.GetRequiredService<CoachRoleLinkSweeper>()
            .SelectEndedAccountProfileIdsAsync(DateTime.UtcNow, Ct);

    [Fact]
    public async Task SelectEndedAccountProfileIds_BothRolesAlreadyMarkedRemoved_IsNotSelected()
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        await SetCoachAccountActiveUntilAsync(coach, DateTime.UtcNow.AddMinutes(-5));
        await MarkRemovedAsync(coach, UserRole.Trainer);
        await MarkRemovedAsync(coach, UserRole.Nutritionist);

        var selected = await SelectEndedProfileIdsAsync();

        selected.Should().NotContain((await ReadProfileAsync(coach)).Id);
    }

    [Fact]
    public async Task SelectEndedAccountProfileIds_OneRoleNotYetMarkedRemoved_IsSelected()
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        await SetCoachAccountActiveUntilAsync(coach, DateTime.UtcNow.AddMinutes(-5));
        await MarkRemovedAsync(coach, UserRole.Trainer);

        var selected = await SelectEndedProfileIdsAsync();

        selected.Should().Contain((await ReadProfileAsync(coach)).Id);
    }

    [Fact]
    public async Task SelectEndedAccountProfileIds_UnmarkedRoleNotHeldByUser_IsNotSelected()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await SetCoachAccountActiveUntilAsync(coach, DateTime.UtcNow.AddMinutes(-5));
        await MarkRemovedAsync(coach, UserRole.Trainer);

        var selected = await SelectEndedProfileIdsAsync();

        selected.Should().NotContain((await ReadProfileAsync(coach)).Id);
    }

    [Fact]
    public async Task Tick_CoachAccountEndsInTheFuture_KeepsRoles()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await SetCoachAccountActiveUntilAsync(coach, DateTime.UtcNow.AddDays(3));

        await TickAsync();

        (await ReadProfileAsync(coach)).TrainerRoleRemovedAt.Should().BeNull();
    }
}
