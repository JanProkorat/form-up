using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.NutritionPlans;

/// <summary>
/// Integration tests for the notification payload of nutrition publish/complete: the push data
/// and the stored <c>Notification.Data</c> must carry <c>planKind=nutrition</c> and <c>planId</c>
/// so a tapped push can tell a nutrition plan from a training plan, and the SignalR event fires.
/// </summary>
[Collection(TestCollection.Shard4)]
public class NutritionPlanNotificationIntegrationTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(Actor Professional, Actor Client)> CreateLinkedPairAsync(UserRole role)
    {
        var professional = role == UserRole.Trainer
            ? await TestActors.Trainer(factory).CreateAsync(Ct)
            : await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, professional, client).AsRole(role).CreateAsync(Ct);
        return (professional, client);
    }

    private async Task SeedNutritionPlanAsync(
        Guid planId, Guid nutritionistId, Guid clientUserId, NutritionPlanStatus status)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        await mongo.NutritionPlans.InsertOneAsync(new NutritionPlan
        {
            ExternalId = planId,
            ClientId = clientUserId,
            NutritionistId = nutritionistId,
            Name = "Live Update Plan",
            Status = status,
            StartDate = DateTime.UtcNow.Date,
            Version = 1,
            DateCreated = DateTime.UtcNow.AddDays(-1),
            Weeks = [new PlanWeek { WeekNumber = 1, Status = WeekStatus.Draft }],
        }, cancellationToken: Ct);
    }

    private (Dictionary<string, string> Data, string Type) AssertSinglePush(Guid clientUserId)
    {
        var push = factory.Services.GetRequiredService<FakePushNotificationService>();
        var call = push.Calls.Should().ContainSingle(c => c.UserId == clientUserId).Subject;
        var data = call.Data.Should().BeAssignableTo<IDictionary<string, string>>().Subject;
        return (new Dictionary<string, string>(data), data["type"]);
    }

    private async Task<Dictionary<string, string>> StoredDataAsync(Guid clientUserId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var notification = await db.Notifications
            .AsNoTracking()
            .SingleAsync(n => n.RecipientUserId == clientUserId, Ct);
        notification.Data.Should().NotBeNull();
        return JsonSerializer.Deserialize<Dictionary<string, string>>(notification.Data!)!;
    }

    [Fact]
    public async Task PublishWeek_NotifiesClient_WithNutritionPlanKindAndPlanId()
    {
        var (nutritionist, client) = await CreateLinkedPairAsync(UserRole.Nutritionist);
        var planId = Guid.NewGuid();
        await SeedNutritionPlanAsync(planId, nutritionist.UserId, client.UserId, NutritionPlanStatus.Draft);

        var response = await nutritionist.Http.PostAsync(
            $"/nutrition/plans/{planId}/weeks/1/publish",
            JsonContent.Create(new { PlanId = planId, WeekNumber = 1, Version = 1 }), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        var (pushData, type) = AssertSinglePush(client.UserId);
        type.Should().Be("PlanPublished");
        pushData["planKind"].Should().Be("nutrition");
        pushData["planId"].Should().Be(planId.ToString());
        pushData["weekNumber"].Should().Be("1");

        var stored = await StoredDataAsync(client.UserId);
        stored["planKind"].Should().Be("nutrition");
        stored["planId"].Should().Be(planId.ToString());

        var notifier = factory.Services.GetRequiredService<FakeRealtimeNotifier>();
        notifier.Calls.Should().Contain(c => c.UserId == client.UserId && c.EventType == "nutritionplanpublished");
    }

    [Fact]
    public async Task CompletePlan_NotifiesClient_WithNutritionPlanKindAndPlanId()
    {
        var (nutritionist, client) = await CreateLinkedPairAsync(UserRole.Nutritionist);
        var planId = Guid.NewGuid();
        await SeedNutritionPlanAsync(planId, nutritionist.UserId, client.UserId, NutritionPlanStatus.Active);

        var response = await nutritionist.Http.PostAsync(
            $"/nutrition/plans/{planId}/complete",
            JsonContent.Create(new { PlanId = planId, Version = 1 }), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        var (pushData, type) = AssertSinglePush(client.UserId);
        type.Should().Be("PlanPublished");
        pushData["planKind"].Should().Be("nutrition");
        pushData["planId"].Should().Be(planId.ToString());
        pushData["planName"].Should().Be("Live Update Plan");

        var stored = await StoredDataAsync(client.UserId);
        stored["planKind"].Should().Be("nutrition");
        stored["planId"].Should().Be(planId.ToString());

        var notifier = factory.Services.GetRequiredService<FakeRealtimeNotifier>();
        notifier.Calls.Should().Contain(c => c.UserId == client.UserId && c.EventType == "nutritionplancompleted");
    }

    [Fact]
    public async Task TrainingPublishWeek_NotifiesClient_WithoutNutritionPlanKind()
    {
        var (trainer, client) = await CreateLinkedPairAsync(UserRole.Trainer);
        var planId = Guid.NewGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            await mongo.TrainingPlans.InsertOneAsync(new TrainingPlan
            {
                ExternalId = planId,
                ClientId = client.UserId,
                TrainerId = trainer.UserId,
                Name = "Training Plan",
                Status = TrainingPlanStatus.Draft,
                StartDate = DateTime.UtcNow.Date,
                Version = 1,
                DateCreated = DateTime.UtcNow.AddDays(-1),
                Weeks = [new TrainingWeek { WeekNumber = 1, Status = WeekStatus.Draft }],
            }, cancellationToken: Ct);
        }

        var response = await trainer.Http.PostAsync(
            $"/training/plans/{planId}/weeks/1/publish",
            JsonContent.Create(new { PlanId = planId, WeekNumber = 1, Version = 1 }), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        var (pushData, type) = AssertSinglePush(client.UserId);
        type.Should().Be("PlanPublished");
        pushData.Should().NotContainKey("planKind");

        var stored = await StoredDataAsync(client.UserId);
        stored.Should().NotContainKey("planKind");
    }
}
