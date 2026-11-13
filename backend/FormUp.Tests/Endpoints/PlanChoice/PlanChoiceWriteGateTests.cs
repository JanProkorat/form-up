using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.PlanChoice;

/// <summary>
/// The global write gate through the real pipeline: a coach whose given plan ended is refused every write except the
/// exemption list, the handler never runs on a refusal, and everyone else is untouched.
/// </summary>
[Collection(TestCollection.Name)]
public class PlanChoiceWriteGateTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Actor> EndedGiftCoachAsync(UserRole role = UserRole.Trainer)
    {
        var coach = role == UserRole.Nutritionist
            ? await TestActors.Nutritionist(factory).CreateAsync(Ct)
            : await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeWithEndedGiftAsync(factory, coach);

        return coach;
    }

    private static async Task AssertPlanChoiceRequiredAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.PlanChoiceRequired);
    }

    private static async Task AssertNotRefusedByGateAsync(HttpResponseMessage response)
    {
        (await response.Content.ReadAsStringAsync(Ct)).Should().NotContain(ErrorCodes.PlanChoiceRequired);
    }

    [Theory]
    [InlineData(UserRole.Trainer, "POST", "/training/plans")]
    [InlineData(UserRole.Nutritionist, "POST", "/nutrition/plans")]
    [InlineData(UserRole.Trainer, "POST", "/trainer/pending-invites")]
    [InlineData(UserRole.Trainer, "PUT", "/users/me/subscription/keep-clients")]
    [InlineData(UserRole.Trainer, "POST", "/conversations")]
    public async Task BlockedWrite_CoachWithEndedGift_Returns403PlanChoiceRequired_EvenWithAnEmptyBody(
        UserRole role, string method, string route)
    {
        var coach = await EndedGiftCoachAsync(role);

        var response = await coach.Http.SendAsync(
            new HttpRequestMessage(new HttpMethod(method), route) { Content = JsonContent.Create(new { }) }, Ct);

        await AssertPlanChoiceRequiredAsync(response);
    }

    [Theory]
    [InlineData("/nutrition/meal-templates")]
    [InlineData("/nutrition/plans")]
    public async Task BlockedWrite_WithAnInvalidBody_AnswersOneCleanProblemBody(string route)
    {
        var coach = await EndedGiftCoachAsync(UserRole.Nutritionist);

        var response = await coach.Http.PostAsJsonAsync(route, new { Name = "" }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var raw = await response.Content.ReadAsStringAsync(Ct);
        var problem = System.Text.Json.JsonDocument.Parse(raw).RootElement;
        problem.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.PlanChoiceRequired);
        problem.TryGetProperty("errors", out _).Should().BeFalse("a validation answer must not follow the gate's answer");
    }

    [Fact]
    public async Task BlockedWrite_HandlerDoesNotRun_NoSideEffect()
    {
        var coach = await EndedGiftCoachAsync();
        var invite = new PendingInvite
        {
            ProfessionalProfileId = coach.ProfileId,
            Email = $"{Guid.NewGuid():N}@invite-fixture.com",
            Status = PendingInviteStatus.Pending,
        };

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.PendingInvites.Add(invite);
            await db.SaveChangesAsync(Ct);
        }

        var response = await coach.Http.DeleteAsync($"/trainer/pending-invites/{invite.PublicId}", Ct);

        await AssertPlanChoiceRequiredAsync(response);

        using var verifyScope = factory.Services.CreateScope();
        var stored = await verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .PendingInvites.AsNoTracking().SingleAsync(i => i.Id == invite.Id, Ct);
        stored.Status.Should().Be(PendingInviteStatus.Pending, "the handler must not run after the gate refuses");
    }

    [Fact]
    public async Task Reads_CoachWithEndedGift_Pass()
    {
        var coach = await EndedGiftCoachAsync();

        var subscription = await coach.Http.GetAsync("/users/me/subscription", Ct);
        var conversations = await coach.Http.GetAsync("/conversations", Ct);

        subscription.StatusCode.Should().Be(HttpStatusCode.OK);
        conversations.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<(Actor Coach, Actor Client, Guid ConversationId)> CoachWithConversationAsync()
    {
        var coach = await EndedGiftCoachAsync();
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).CreateAsync(Ct);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var conversation = new Conversation { ProfessionalUserId = coach.UserId, ClientUserId = client.UserId };
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync(Ct);

        return (coach, client, conversation.PublicId);
    }

    [Fact]
    public async Task SendMessage_InAnExistingConversation_IsAllowed_WhileStartConversationIsRefused()
    {
        var (coach, client, conversationId) = await CoachWithConversationAsync();

        var sent = await coach.Http.PostAsJsonAsync($"/conversations/{conversationId}/messages", new { Text = "hello" }, Ct);
        var started = await coach.Http.PostAsJsonAsync("/conversations", new { RecipientId = client.PublicId, Text = "hi" }, Ct);

        sent.IsSuccessStatusCode.Should().BeTrue(await sent.Content.ReadAsStringAsync(Ct));
        await AssertPlanChoiceRequiredAsync(started);
    }

    [Theory]
    [InlineData("POST", "/conversations/{conversation}/read")]
    [InlineData("PATCH", "/conversations/{conversation}/archive")]
    [InlineData("PATCH", "/conversations/{conversation}/unarchive")]
    [InlineData("POST", "/conversations/{conversation}/messages/image-upload-url")]
    public async Task ChatHousekeeping_CoachWithEndedGift_IsNotRefusedByTheGate(string method, string route)
    {
        var (coach, _, conversationId) = await CoachWithConversationAsync();

        var response = await coach.Http.SendAsync(
            new HttpRequestMessage(new HttpMethod(method), route.Replace("{conversation}", conversationId.ToString()))
            {
                Content = JsonContent.Create(new { }),
            },
            Ct);

        await AssertNotRefusedByGateAsync(response);
    }

    [Theory]
    [InlineData("POST", "/users/me/password")]
    [InlineData("PUT", "/users/me")]
    [InlineData("PUT", "/users/me/timezone")]
    [InlineData("PUT", "/users/me/notification-preferences")]
    [InlineData("PUT", "/users/me/avatar")]
    [InlineData("POST", "/users/me/avatar/upload-url")]
    [InlineData("DELETE", "/users/me/avatar")]
    [InlineData("PUT", "/professionals/me/avatar")]
    [InlineData("POST", "/professionals/me/avatar/upload-url")]
    [InlineData("DELETE", "/professionals/me/avatar")]
    [InlineData("POST", "/users/me/coach-account/disable")]
    [InlineData("DELETE", "/users/me/roles/Trainer")]
    [InlineData("DELETE", "/users/me")]
    public async Task AccountHousekeeping_CoachWithEndedGift_IsNotRefusedByTheGate(string method, string route)
    {
        var coach = await EndedGiftCoachAsync();

        var response = await coach.Http.SendAsync(
            new HttpRequestMessage(new HttpMethod(method), route) { Content = JsonContent.Create(new { }) }, Ct);

        await AssertNotRefusedByGateAsync(response);
    }

    [Theory]
    [InlineData("/client/notifications/read-all")]
    [InlineData("/client/notifications/{id}/read")]
    public async Task NotificationRead_PureCoachWithEndedGift_IsNotRefusedByTheGate(string route)
    {
        var coach = await EndedGiftCoachAsync();

        var response = await coach.Http.PostAsync(route.Replace("{id}", Guid.NewGuid().ToString()), null, Ct);

        await AssertNotRefusedByGateAsync(response);
    }

    [Fact]
    public async Task BlockedWrite_CoachWhoIsAlsoAClient_KeepsTheClientSide()
    {
        var dual = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Client).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeWithEndedGiftAsync(factory, dual);

        var clientOnlyWrite = await dual.Http.PostAsJsonAsync("/client/push-token", new { }, Ct);
        var coachSide = await dual.Http.PostAsJsonAsync("/training/plans", new { }, Ct);

        await AssertNotRefusedByGateAsync(clientOnlyWrite);
        await AssertPlanChoiceRequiredAsync(coachSide);
    }

    [Fact]
    public async Task Write_CoachWithAGiftStillInForce_IsNotRefused()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(
            factory, coach, "pro", SubscriptionStatus.Active,
            freeUntil: DateTimeOffset.UtcNow.AddDays(5), givenFreeAt: DateTimeOffset.UtcNow.AddDays(-5));

        var response = await coach.Http.PostAsJsonAsync("/training/plans", new { }, Ct);

        await AssertNotRefusedByGateAsync(response);
    }

    [Fact]
    public async Task Write_CoachWithAGiftThatHasNoEnd_IsNotRefused()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(
            factory, coach, "pro", SubscriptionStatus.Active, givenFreeAt: DateTimeOffset.UtcNow.AddDays(-5));

        var response = await coach.Http.PostAsJsonAsync("/training/plans", new { }, Ct);

        await AssertNotRefusedByGateAsync(response);
    }

    [Theory]
    [InlineData(SubscriptionStatus.Canceled)]
    [InlineData(SubscriptionStatus.Incomplete)]
    [InlineData(SubscriptionStatus.PastDue)]
    public async Task Write_PaidPlanThatEndedOrFailed_IsNotAPlanChoice(SubscriptionStatus status)
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(
            factory, coach, "pro", status, freeUntil: DateTimeOffset.UtcNow.AddDays(-1));

        var response = await coach.Http.PostAsJsonAsync("/training/plans", new { }, Ct);

        await AssertNotRefusedByGateAsync(response);
    }

    [Fact]
    public async Task Write_CoachWithNoSubscription_IsNotRefused()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);

        var response = await coach.Http.PostAsJsonAsync("/training/plans", new { }, Ct);

        await AssertNotRefusedByGateAsync(response);
    }

    [Fact]
    public async Task Write_ClientWhoseCoachIsInPlanChoice_IsNotRefused()
    {
        var coach = await EndedGiftCoachAsync();
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).CreateAsync(Ct);

        var response = await client.Http.PostAsync("/client/notifications/read-all", null, Ct);

        response.IsSuccessStatusCode.Should().BeTrue();
    }
}
