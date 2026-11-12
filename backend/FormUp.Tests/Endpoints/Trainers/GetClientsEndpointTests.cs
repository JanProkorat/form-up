using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Trainers;

/// <summary>
/// Integration tests for <c>GET /trainer/clients</c>. Uses the Testcontainers-backed
/// <see cref="FitnessApiFactory"/> host — never the mock-Mongo harness, which returns every
/// seeded document regardless of <c>FilterDefinition</c> and would make every Active/Paused
/// assertion below unfalsifiable.
/// </summary>
[Collection(TestCollection.Name)]
public class GetClientsEndpointTests(FitnessApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static string UniqueEmail(string tag) => $"{Guid.NewGuid():N}@get-clients-{tag}.com";

    // ── basic shape ──────────────────────────────────────────────────────────

    [Fact]
    public async Task List_NoStatusParam_ReturnsEveryLiveLinkExcludingArchived()
    {
        // Pre-existing behaviour: omitting status must not silently default to "active" —
        // it means every live link, Active and Paused combined, Archived excluded.
        var (http, trainerId) = await SetupTrainerAsync();

        var (activeClientId, _) = await SetupLinkedClientAsync(trainerId);
        await SeedActiveTrainingPlanAsync(trainerId, activeClientId, DateTime.UtcNow.AddDays(-1));

        var (pausedClientId, _) = await SetupLinkedClientAsync(trainerId);
        // No plan seeded — pausedClientId stays Paused.

        var (archivedClientId, archivedPublicId) = await SetupLinkedClientAsync(trainerId);
        await SetLinkActiveAsync(archivedPublicId, false);

        var response = await http.GetAsync("/trainer/clients", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Deserialize(response);

        body!.Clients.Select(c => c.UserId).Should().BeEquivalentTo([activeClientId, pausedClientId]);
        body.Clients.Should().NotContain(c => c.UserId == archivedClientId);
    }

    [Fact]
    public async Task List_TabCountsPending_CountsOneInvitePerEmail_ExpiredSupersededByReinviteNotCounted()
    {
        var (http, trainerId) = await SetupTrainerAsync();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var profile = await db.ProfessionalProfiles
                .FirstAsync(pp => pp.UserId == trainerId, TestContext.Current.CancellationToken);
            var reinvited = UniqueEmail("reinvited");
            var lone = UniqueEmail("lone-expired");

            foreach (var (email, ageDays) in new[] { (reinvited, 10), (reinvited, 1), (lone, 9) })
            {
                db.PendingInvites.Add(new PendingInvite
                {
                    PublicId = Guid.NewGuid(),
                    ProfessionalProfileId = profile.Id,
                    Email = email,
                    SentAt = DateTime.UtcNow.AddDays(-ageDays),
                    Status = PendingInviteStatus.Pending,
                });
            }

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var response = await http.GetAsync("/trainer/clients", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Deserialize(response);

        // reinvited: live row only (1) + lone expired kept so it can be cancelled (1).
        body!.TabCounts.Pending.Should().Be(2);
    }

    [Fact]
    public async Task List_StatusArchived_ReturnsOnlyArchivedLinks()
    {
        var (http, trainerId) = await SetupTrainerAsync();

        var (_, activePublicId) = await SetupLinkedClientAsync(trainerId);
        var (archivedClientId, archivedPublicId) = await SetupLinkedClientAsync(trainerId);
        await SetLinkActiveAsync(archivedPublicId, false);

        var response = await http.GetAsync("/trainer/clients?status=Archived", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Deserialize(response);

        body!.Clients.Should().ContainSingle(c => c.UserId == archivedClientId);
        body.Clients.Single().Status.Should().Be("Archived");
    }

    [Fact]
    public async Task List_NoProfessionalProfile_Returns404()
    {
        // A caller authenticated with the Trainer role but with no ProfessionalProfile row
        // cannot be produced through normal registration (it's created at registration time),
        // so this mirrors the existing endpoint's guard by deleting the row post-registration.
        var http = factory.CreateClient();
        var email = UniqueEmail("no-profile");
        await TestHelpers.RegisterAsync(http, email, "TestPass1!", "Test", "Trainer", "Trainer");
        var (token, _) = await TestHelpers.LoginAsync(http, email, "TestPass1!");
        TestHelpers.SetBearerToken(http, token);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.FirstAsync(u => u.Email == email, TestContext.Current.CancellationToken);
            var profile = await db.ProfessionalProfiles
                .FirstAsync(pp => pp.UserId == user.Id, TestContext.Current.CancellationToken);
            db.ProfessionalProfiles.Remove(profile);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var response = await http.GetAsync("/trainer/clients", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── #840 join-key regression ────────────────────────────────────────────

    [Fact]
    public async Task List_PlanKeyedOnApplicationUserId_ClassifiesActive_EvenThoughClientProfilePublicIdDiffers()
    {
        // The only shape that catches a regression back to keying on ClientProfile.PublicId:
        // the plan's clientId is the ApplicationUser.Id, and ClientProfile.PublicId is a
        // deliberately DIFFERENT Guid (which it always is — the two are unrelated identifiers).
        // A join on PublicId would match zero documents and silently read Paused.
        var (http, trainerId) = await SetupTrainerAsync();
        var (clientUserId, clientPublicId) = await SetupLinkedClientAsync(trainerId);

        clientPublicId.Should().NotBe(clientUserId, "PublicId and the ApplicationUser join key must never coincide in this fixture");

        await SeedActiveTrainingPlanAsync(trainerId, clientUserId, DateTime.UtcNow.AddDays(-1));

        var response = await http.GetAsync("/trainer/clients", TestContext.Current.CancellationToken);
        var body = await Deserialize(response);

        var client = body!.Clients.Should().ContainSingle(c => c.UserId == clientUserId).Subject;
        client.Status.Should().Be("Active");
        client.HasActiveTrainingPlan.Should().BeTrue();
    }

    // ── tab classification ───────────────────────────────────────────────────

    [Fact]
    public async Task List_ActivePlanWithNullStartDate_NeverClassifiesActive()
    {
        var (http, trainerId) = await SetupTrainerAsync();
        var (clientUserId, _) = await SetupLinkedClientAsync(trainerId);

        await SeedActiveTrainingPlanAsync(trainerId, clientUserId, startDate: null);

        var response = await http.GetAsync("/trainer/clients", TestContext.Current.CancellationToken);
        var body = await Deserialize(response);

        var client = body!.Clients.Should().ContainSingle(c => c.UserId == clientUserId).Subject;
        client.Status.Should().Be("Paused");
        client.HasActiveTrainingPlan.Should().BeFalse();
    }

    [Fact]
    public async Task List_NutritionOnlyLink_ClientHasActiveTrainingPlan_ClassifiesPausedAndOmitsTrainingPlan()
    {
        var (http, trainerId) = await SetupTrainerAsync();
        var (clientUserId, _) = await SetupLinkedClientAsync(
            trainerId, canViewNutritionPlans: true, canViewTrainingPlans: false);

        await SeedActiveTrainingPlanAsync(trainerId, clientUserId, DateTime.UtcNow.AddDays(-1));

        var response = await http.GetAsync("/trainer/clients", TestContext.Current.CancellationToken);
        var body = await Deserialize(response);

        var client = body!.Clients.Should().ContainSingle(c => c.UserId == clientUserId).Subject;
        client.Status.Should().Be("Paused");
        client.HasActiveTrainingPlan.Should().BeFalse();
        client.ActivePlans.Should().BeEmpty();
    }

    [Fact]
    public async Task List_GrantsNothingLink_AlwaysPaused()
    {
        var (http, trainerId) = await SetupTrainerAsync();
        var (clientUserId, _) = await SetupLinkedClientAsync(
            trainerId, canViewNutritionPlans: false, canViewTrainingPlans: false);

        await SeedActiveTrainingPlanAsync(trainerId, clientUserId, DateTime.UtcNow.AddDays(-1));

        var response = await http.GetAsync("/trainer/clients", TestContext.Current.CancellationToken);
        var body = await Deserialize(response);

        var client = body!.Clients.Should().ContainSingle(c => c.UserId == clientUserId).Subject;
        client.Status.Should().Be("Paused");
        client.HasActiveTrainingPlan.Should().BeFalse();
        client.HasActiveNutritionPlan.Should().BeFalse();
    }

    [Fact]
    public async Task List_PlanWindowEndsToday_ClassifiesActive()
    {
        // The window is half-open [StartDate, StartDate + WeekCount*7), so the last in-window
        // day is StartDate + WeekCount*7 - 1. A 1-week plan starting 6 days ago ends exactly today.
        var (http, trainerId) = await SetupTrainerAsync();
        var (clientUserId, _) = await SetupLinkedClientAsync(trainerId);

        await SeedActiveTrainingPlanAsync(trainerId, clientUserId, DateTime.UtcNow.AddDays(-6), weekCount: 1);

        var response = await http.GetAsync("/trainer/clients", TestContext.Current.CancellationToken);
        var body = await Deserialize(response);

        var client = body!.Clients.Should().ContainSingle(c => c.UserId == clientUserId).Subject;
        client.Status.Should().Be("Active");
        client.HasActiveTrainingPlan.Should().BeTrue();
    }

    [Fact]
    public async Task List_PlanStartsTomorrow_ClassifiesPaused()
    {
        var (http, trainerId) = await SetupTrainerAsync();
        var (clientUserId, _) = await SetupLinkedClientAsync(trainerId);

        await SeedActiveTrainingPlanAsync(trainerId, clientUserId, DateTime.UtcNow.AddDays(1), weekCount: 4);

        var response = await http.GetAsync("/trainer/clients", TestContext.Current.CancellationToken);
        var body = await Deserialize(response);

        var client = body!.Clients.Should().ContainSingle(c => c.UserId == clientUserId).Subject;
        client.Status.Should().Be("Paused");
        client.HasActiveTrainingPlan.Should().BeFalse();
    }

    // ── security: cross-coach scoping ────────────────────────────────────────

    [Fact]
    public async Task List_ArchivedTab_SecondCoachsArchivedLink_NeverAppearsInCallersArchivedTabOrTabCounts()
    {
        var (http, callerId) = await SetupTrainerAsync();
        var (_, otherCoachId) = await SetupTrainerAsync();

        var (_, otherArchivedPublicId) = await SetupLinkedClientAsync(otherCoachId);
        await SetLinkActiveAsync(otherArchivedPublicId, false);

        var (callerArchivedClientId, callerArchivedPublicId) = await SetupLinkedClientAsync(callerId);
        await SetLinkActiveAsync(callerArchivedPublicId, false);

        var response = await http.GetAsync("/trainer/clients?status=Archived", TestContext.Current.CancellationToken);
        var body = await Deserialize(response);

        body!.Clients.Should().ContainSingle(c => c.UserId == callerArchivedClientId);
        body.TabCounts.Archived.Should().Be(1, "the other coach's archived link must never inflate the caller's counts");
    }

    // ── tags: no existence oracle ────────────────────────────────────────────

    [Fact]
    public async Task List_UnknownTagIdAndForeignTagId_BehaveIdentically_Returns200EmptyNever404()
    {
        var (http, trainerId) = await SetupTrainerAsync();
        await SetupLinkedClientAsync(trainerId);

        var (_, otherCoachId) = await SetupTrainerAsync();
        var foreignTagId = await CreateTagAsync(otherCoachId, "Foreign");

        var unknownResponse = await http.GetAsync(
            $"/trainer/clients?tagIds={Guid.NewGuid()}", TestContext.Current.CancellationToken);
        var foreignResponse = await http.GetAsync(
            $"/trainer/clients?tagIds={foreignTagId}", TestContext.Current.CancellationToken);

        unknownResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        foreignResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var unknownBody = await Deserialize(unknownResponse);
        var foreignBody = await Deserialize(foreignResponse);

        unknownBody!.Clients.Should().BeEmpty();
        foreignBody!.Clients.Should().BeEmpty();
    }

    [Fact]
    public async Task List_OwnedTagId_FiltersToTaggedClientOnly()
    {
        var (http, trainerId) = await SetupTrainerAsync();
        var tagId = await CreateTagAsync(trainerId, "VIP");

        var (taggedClientId, taggedPublicId) = await SetupLinkedClientAsync(trainerId);
        var (_, _) = await SetupLinkedClientAsync(trainerId);

        var assign = await http.PutAsJsonAsync($"/trainer/clients/{taggedPublicId}/tags",
            new { TagIds = new[] { tagId } }, TestContext.Current.CancellationToken);
        assign.StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await http.GetAsync($"/trainer/clients?tagIds={tagId}", TestContext.Current.CancellationToken);
        var body = await Deserialize(response);

        body!.Clients.Should().ContainSingle(c => c.UserId == taggedClientId);
        body.Clients.Single().Tags.Should().ContainSingle(t => t.Name == "VIP");
    }

    [Fact]
    public async Task List_MultipleTagIds_MatchesAnyRequestedTag_NotEveryOne()
    {
        // ANY-of, not ALL-of: a client matches if it carries at least one requested tag id —
        // selecting more tags widens the result set, as a filter dropdown normally does.
        var (http, trainerId) = await SetupTrainerAsync();
        var firstTagId = await CreateTagAsync(trainerId, "First");
        var secondTagId = await CreateTagAsync(trainerId, "Second");

        var (firstOnlyClientId, firstOnlyPublicId) = await SetupLinkedClientAsync(trainerId);
        var (secondOnlyClientId, secondOnlyPublicId) = await SetupLinkedClientAsync(trainerId);
        var (bothClientId, bothPublicId) = await SetupLinkedClientAsync(trainerId);

        await AssignTagsAsync(http, firstOnlyPublicId, firstTagId);
        await AssignTagsAsync(http, secondOnlyPublicId, secondTagId);
        await AssignTagsAsync(http, bothPublicId, firstTagId, secondTagId);

        var response = await http.GetAsync(
            $"/trainer/clients?tagIds={firstTagId}&tagIds={secondTagId}", TestContext.Current.CancellationToken);
        var body = await Deserialize(response);

        body!.Clients.Select(c => c.UserId).Should().BeEquivalentTo(
            [firstOnlyClientId, secondOnlyClientId, bothClientId]);
    }

    // ── filter chips ─────────────────────────────────────────────────────────

    [Fact]
    public async Task List_UnreadMessagesFilter_ReturnsOnlyClientsWithUnreadFromClient()
    {
        var (http, trainerId) = await SetupTrainerAsync();
        var (unreadClientId, unreadPublicId) = await SetupLinkedClientAsync(trainerId);
        var (readClientId, readPublicId) = await SetupLinkedClientAsync(trainerId);

        await SeedConversationWithMessageAsync(trainerId, unreadClientId, senderIsClient: true, isRead: false);
        await SeedConversationWithMessageAsync(trainerId, readClientId, senderIsClient: true, isRead: true);

        var response = await http.GetAsync(
            "/trainer/clients?filter=UnreadMessages", TestContext.Current.CancellationToken);
        var body = await Deserialize(response);

        body!.Clients.Select(c => c.UserId).Should().BeEquivalentTo([unreadClientId]);
        body.Clients.Single().UnreadMessageCount.Should().Be(1);
    }

    [Fact]
    public async Task List_NoMessagesFilter_ReturnsClientsWithNoConversationOrEmptyConversation()
    {
        var (http, trainerId) = await SetupTrainerAsync();
        var (noConversationClientId, _) = await SetupLinkedClientAsync(trainerId);
        var (hasMessagesClientId, _) = await SetupLinkedClientAsync(trainerId);

        await SeedConversationWithMessageAsync(trainerId, hasMessagesClientId, senderIsClient: true, isRead: true);

        var response = await http.GetAsync(
            "/trainer/clients?filter=NoMessages", TestContext.Current.CancellationToken);
        var body = await Deserialize(response);

        body!.Clients.Select(c => c.UserId).Should().BeEquivalentTo([noConversationClientId]);
    }

    [Fact]
    public async Task List_CheckInFilters_SplitNewUnreviewedFromPastDueUnanswered()
    {
        var (http, trainerId) = await SetupTrainerAsync();
        var (newClientId, _) = await SetupLinkedClientAsync(trainerId);
        var (reviewedClientId, _) = await SetupLinkedClientAsync(trainerId);
        var (missingClientId, _) = await SetupLinkedClientAsync(trainerId);

        await CheckInFixtures.AddForUsersAsync(factory, trainerId, newClientId, submitted: true);
        await CheckInFixtures.AddForUsersAsync(factory, trainerId, reviewedClientId, submitted: true, reviewed: true);
        await CheckInFixtures.AddForUsersAsync(factory, trainerId, missingClientId, submitted: false);

        var fresh = await http.GetAsync("/trainer/clients?filter=NewCheckIns", TestContext.Current.CancellationToken);
        (await Deserialize(fresh))!.Clients.Select(c => c.UserId).Should().BeEquivalentTo([newClientId]);

        var missing = await http.GetAsync("/trainer/clients?filter=MissingCheckIns", TestContext.Current.CancellationToken);
        (await Deserialize(missing))!.Clients.Select(c => c.UserId).Should().BeEquivalentTo([missingClientId]);
    }

    [Fact]
    public async Task List_EndingSoonFilter_ReturnsActivePlanEndingWithinFourteenDays()
    {
        var (http, trainerId) = await SetupTrainerAsync();
        var (endingSoonClientId, _) = await SetupLinkedClientAsync(trainerId);
        var (notEndingSoonClientId, _) = await SetupLinkedClientAsync(trainerId);

        // 1-week plan starting 6 days ago ends 1 day from now — within the 14-day window.
        await SeedActiveTrainingPlanAsync(trainerId, endingSoonClientId, DateTime.UtcNow.AddDays(-6), weekCount: 1);
        // 10-week plan starting today ends far beyond 14 days.
        await SeedActiveTrainingPlanAsync(trainerId, notEndingSoonClientId, DateTime.UtcNow, weekCount: 10);

        var response = await http.GetAsync(
            "/trainer/clients?filter=EndingSoon", TestContext.Current.CancellationToken);
        var body = await Deserialize(response);

        body!.Clients.Select(c => c.UserId).Should().BeEquivalentTo([endingSoonClientId]);
    }

    [Fact]
    public async Task List_FilterCounts_ComputedWithSearchAndTagsButNotChipFilterApplied()
    {
        var (http, trainerId) = await SetupTrainerAsync();
        var (unreadClientId, _) = await SetupLinkedClientAsync(trainerId);
        var (otherClientId, _) = await SetupLinkedClientAsync(trainerId);

        await SeedConversationWithMessageAsync(trainerId, unreadClientId, senderIsClient: true, isRead: false);

        // Even though the request itself applies the UnreadMessages chip, FilterCounts.All must
        // still report BOTH clients — the chip counts are computed as if no chip were selected.
        var response = await http.GetAsync(
            "/trainer/clients?filter=UnreadMessages", TestContext.Current.CancellationToken);
        var body = await Deserialize(response);

        body!.Clients.Select(c => c.UserId).Should().BeEquivalentTo([unreadClientId]);
        body.FilterCounts.All.Should().Be(2);
        body.FilterCounts.UnreadMessages.Should().Be(1);

        _ = otherClientId;
    }

    // ── pagination ───────────────────────────────────────────────────────────

    [Fact]
    public async Task List_Pagination_RespectsPageSize()
    {
        var (http, trainerId) = await SetupTrainerAsync();
        for (var i = 0; i < 3; i++)
        {
            await SetupLinkedClientAsync(trainerId);
        }

        var response = await http.GetAsync(
            "/trainer/clients?page=1&pageSize=2", TestContext.Current.CancellationToken);
        var body = await Deserialize(response);

        body!.TotalCount.Should().Be(3);
        body.Clients.Should().HaveCount(2);
        body.Page.Should().Be(1);
        body.PageSize.Should().Be(2);
    }

    // ── validation ───────────────────────────────────────────────────────────

    [Fact]
    public async Task List_PageLessThanOne_Returns400()
    {
        var (http, _) = await SetupTrainerAsync();

        var response = await http.GetAsync("/trainer/clients?page=0", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task List_PageSizeOverLimit_Returns400()
    {
        var (http, _) = await SetupTrainerAsync();

        var response = await http.GetAsync("/trainer/clients?pageSize=101", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task List_TooManyTagIds_Returns400()
    {
        var (http, _) = await SetupTrainerAsync();
        var tagIds = string.Join('&', Enumerable.Range(0, 21).Select(_ => $"tagIds={Guid.NewGuid()}"));

        var response = await http.GetAsync($"/trainer/clients?{tagIds}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task List_InvalidStatusValue_Returns400()
    {
        var (http, _) = await SetupTrainerAsync();

        var response = await http.GetAsync("/trainer/clients?status=NotARealStatus", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── outside the plan in force ────────────────────────────────────────────

    /// <summary>
    /// A coach on an explicit Free plan row (cap 2; with no row nobody is ever over the cap) with <paramref name="count"/> Connected clients linked 10 days apart,
    /// oldest first: the last ones are over the cap.
    /// </summary>
    private async Task<(Actor Coach, List<Actor> Clients)> CoachWithClientsAsync(int count, bool onFreePlan = true)
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);

        if (onFreePlan)
        {
            await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, "free");
        }

        var clients = new List<Actor>();

        for (var i = 0; i < count; i++)
        {
            var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
            await TestActors.Link(factory, coach, client)
                .WithActivatedAtUtc(DateTime.UtcNow.AddDays(-(100 - i * 10)))
                .CreateAsync(TestContext.Current.CancellationToken);
            clients.Add(client);
        }

        return (coach, clients);
    }

    private static async Task<GetClientsResponseDto> ListAsync(Actor coach, string query = "")
    {
        var response = await coach.Http.GetAsync($"/trainer/clients{query}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return (await Deserialize(response))!;
    }

    [Fact]
    public async Task List_CoachOverTheCap_FlagsOnlyTheOverCapRowsAndCountsThem()
    {
        var (coach, clients) = await CoachWithClientsAsync(3);

        var body = await ListAsync(coach);

        body.Clients.Single(c => c.UserId == clients[2].UserId).IsOutsidePlan.Should().BeTrue();
        body.Clients.Where(c => c.UserId != clients[2].UserId).Should().OnlyContain(c => !c.IsOutsidePlan);
        body.TabCounts.OutsidePlan.Should().Be(1);
    }

    [Fact]
    public async Task List_TabOutsidePlan_ListsExactlyTheOverCapLiveRowsActiveAndPausedWithSearchApplied()
    {
        var (coach, clients) = await CoachWithClientsAsync(4);
        await SeedActiveTrainingPlanAsync(coach.UserId, clients[2].UserId, DateTime.UtcNow.Date.AddDays(-1));

        var all = await ListAsync(coach, "?tab=OutsidePlan");

        all.Clients.Select(c => c.UserId).Should().BeEquivalentTo([clients[2].UserId, clients[3].UserId]);
        all.Clients.Select(c => c.Status).Should().BeEquivalentTo(["Active", "Paused"]);
        all.TotalCount.Should().Be(2);

        var searched = await ListAsync(coach, $"?tab=OutsidePlan&search={Uri.EscapeDataString(clients[3].Email)}");

        searched.Clients.Should().ContainSingle(c => c.UserId == clients[3].UserId);
        searched.TabCounts.OutsidePlan.Should().Be(1, "search narrows the tab counts like Active and Paused");
    }

    [Theory]
    [InlineData("?tab=Active&status=Active")]
    [InlineData("?tab=All&status=Archived")]
    [InlineData("?tab=Bogus")]
    [InlineData("?plan=Bogus")]
    [InlineData("?tab=99")]
    public async Task List_TabWithStatusOrInvalidEnum_Returns400(string query)
    {
        var (coach, _) = await CoachWithClientsAsync(1);

        var response = await coach.Http.GetAsync($"/trainer/clients{query}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task List_ArchivedRowOfAnOverCapCoach_IsNeverFlagged()
    {
        var (coach, clients) = await CoachWithClientsAsync(4);
        await SetLinkActiveAsync(clients[0].PublicId, false);

        var archived = await ListAsync(coach, "?status=Archived");
        var live = await ListAsync(coach);

        archived.Clients.Should().ContainSingle().Which.IsOutsidePlan.Should().BeFalse();
        live.Clients.Where(c => c.IsOutsidePlan).Select(c => c.UserId).Should().BeEquivalentTo([clients[3].UserId]);
        live.TabCounts.OutsidePlan.Should().Be(1);
    }

    [Fact]
    public async Task List_OutsidePlanClientsLinkedMoreRecently_ListedAfterAllInPlanClientsAcrossPages()
    {
        // Free cap 2: clients[0] and [1] are in plan, [2..4] outside, and every outside client is newer.
        var (coach, clients) = await CoachWithClientsAsync(5);

        var all = await ListAsync(coach);

        all.Clients.Select(c => c.UserId).Should().Equal(
            [clients[1].UserId, clients[0].UserId, clients[4].UserId, clients[3].UserId, clients[2].UserId]);

        var paged = new List<Guid>();

        for (var page = 1; page <= 3; page++)
        {
            var body = await ListAsync(coach, $"?page={page}&pageSize=2");
            paged.AddRange(body.Clients.Select(c => c.UserId));
        }

        paged.Should().Equal(all.Clients.Select(c => c.UserId));
    }

    [Fact]
    public async Task List_WithinCapUnlimitedPlanOrPlanChoiceRequired_FlagsNobody()
    {
        var (withinCap, _) = await CoachWithClientsAsync(2);
        var (unlimited, _) = await CoachWithClientsAsync(3, onFreePlan: false);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, unlimited, "studio");
        var (choiceRequired, _) = await CoachWithClientsAsync(3, onFreePlan: false);
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        await CoachSubscriptionBuilder.SubscribeWithEndedGiftAsync(factory, choiceRequired, plan);

        foreach (var coach in new[] { withinCap, unlimited, choiceRequired })
        {
            var body = await ListAsync(coach);

            body.Clients.Should().OnlyContain(c => !c.IsOutsidePlan);
            body.TabCounts.OutsidePlan.Should().Be(0);
            (await ListAsync(coach, "?tab=OutsidePlan")).Clients.Should().BeEmpty();
        }
    }

    // ── clients-page tabs and plan filter ────────────────────────────────────

    /// <summary>
    /// Free cap 2, five clients linked oldest first, the oldest archived: [1] and [2] in plan,
    /// [3] and [4] outside. Plans: [1] ending soon, [3] long-running; [2] and [4] have none.
    /// </summary>
    private async Task<(Actor Coach, List<Actor> Clients)> MixedRosterAsync()
    {
        var (coach, clients) = await CoachWithClientsAsync(5);
        await SetLinkActiveAsync(clients[0].PublicId, false);
        await SeedActiveTrainingPlanAsync(coach.UserId, clients[1].UserId, DateTime.UtcNow.Date.AddDays(-1), weekCount: 1);
        await SeedActiveTrainingPlanAsync(coach.UserId, clients[3].UserId, DateTime.UtcNow.Date.AddDays(-1), weekCount: 10);

        return (coach, clients);
    }

    private static Guid[] Ids(List<Actor> clients, params int[] indexes) =>
        indexes.Select(i => clients[i].UserId).ToArray();

    [Theory]
    [InlineData("All", new[] { 2, 1, 0, 4, 3 })]
    [InlineData("Active", new[] { 2, 1 })]
    [InlineData("OutsidePlan", new[] { 4, 3 })]
    [InlineData("Archived", new[] { 0 })]
    public async Task List_Tab_ReturnsExpectedRowsInOrder(string tab, int[] expected)
    {
        var (coach, clients) = await MixedRosterAsync();

        var body = await ListAsync(coach, $"?tab={tab}");

        body.Clients.Select(c => c.UserId).Should().Equal(Ids(clients, expected));
        body.TotalCount.Should().Be(expected.Length);
    }

    [Theory]
    [InlineData("Active", new[] { 1, 3 })]
    [InlineData("None", new[] { 2, 4 })]
    [InlineData("EndingSoon", new[] { 1 })]
    public async Task List_PlanFilterOnAllTab_ReturnsExpectedRows_ArchivedNeverMatches(string plan, int[] expected)
    {
        var (coach, clients) = await MixedRosterAsync();

        var body = await ListAsync(coach, $"?tab=All&plan={plan}");

        body.Clients.Select(c => c.UserId).Should().BeEquivalentTo(Ids(clients, expected));
        body.Clients.Should().NotContain(c => c.UserId == clients[0].UserId);
    }

    [Fact]
    public async Task List_TabCombinedWithPlan_AppliesPlanAfterTab()
    {
        var (coach, clients) = await MixedRosterAsync();

        var inPlanEnding = await ListAsync(coach, "?tab=Active&plan=EndingSoon");
        var outsideActive = await ListAsync(coach, "?tab=OutsidePlan&plan=Active");
        var archivedNone = await ListAsync(coach, "?tab=Archived&plan=None");

        inPlanEnding.Clients.Select(c => c.UserId).Should().Equal(clients[1].UserId);
        outsideActive.Clients.Select(c => c.UserId).Should().Equal(clients[3].UserId);
        archivedNone.Clients.Should().BeEmpty();
    }

    [Fact]
    public async Task List_TabCounts_AreIndependentOfTabAndPlan()
    {
        var (coach, _) = await MixedRosterAsync();

        var baseline = await ListAsync(coach);

        baseline.TabCounts.Should().BeEquivalentTo(new TabCountsDto
        {
            All = 5, InPlan = 2, Active = 2, Paused = 2, Archived = 1, OutsidePlan = 2, Pending = 0
        });

        foreach (var query in new[] { "?tab=All", "?tab=Active", "?tab=OutsidePlan", "?tab=Archived", "?tab=Active&plan=None", "?plan=EndingSoon" })
        {
            (await ListAsync(coach, query)).TabCounts.Should().BeEquivalentTo(baseline.TabCounts, query);
        }
    }

    [Theory]
    [InlineData("?tab=All", 5, 2, 2, 1)]
    [InlineData("?tab=Active", 2, 1, 1, 1)]
    [InlineData("?tab=OutsidePlan", 2, 1, 1, 0)]
    [InlineData("?tab=Archived", 1, 0, 0, 0)]
    [InlineData("?tab=All&plan=None", 5, 2, 2, 1)]
    [InlineData("?tab=All&filter=EndingSoon", 5, 2, 2, 1)]
    public async Task List_PlanCounts_ComputedOnSelectedTabBeforePlanAndChipFilters(
        string query, int all, int active, int none, int endingSoon)
    {
        var (coach, _) = await MixedRosterAsync();

        var body = await ListAsync(coach, query);

        body.PlanCounts.Should().BeEquivalentTo(new PlanCountsDto
        {
            All = all, Active = active, None = none, EndingSoon = endingSoon
        });
    }

    [Fact]
    public async Task List_WithoutTabPlanOrStatus_KeepsLegacyRowsAndCounts()
    {
        var (coach, clients) = await MixedRosterAsync();

        var body = await ListAsync(coach);

        // Every live link, Archived excluded, in-plan first then newest-linked first.
        body.Clients.Select(c => c.UserId).Should().Equal(Ids(clients, 2, 1, 4, 3));
        body.TotalCount.Should().Be(4);
        body.TabCounts.Active.Should().Be(2);
        body.TabCounts.Paused.Should().Be(2);
        body.TabCounts.Archived.Should().Be(1);
        body.TabCounts.OutsidePlan.Should().Be(2);
        body.FilterCounts.All.Should().Be(4);
        body.PlanCounts.All.Should().Be(4);

        (await ListAsync(coach, "?status=Active")).Clients.Select(c => c.UserId)
            .Should().BeEquivalentTo(Ids(clients, 1, 3));
    }

    // ── winding-down outside-plan clients (published weeks still run) ────────

    private static readonly DateTime TodayUtc = DateTime.UtcNow.Date;

    private static DateOnly EndOfFirstPublishedWeek(DateTime startDate) => DateOnly.FromDateTime(startDate.AddDays(6));

    /// <summary>
    /// Free cap 2, four clients: [0] and [1] in plan, [2] and [3] outside. A published week starting
    /// <paramref name="daysAgo"/> days back ends 6 days after its start.
    /// </summary>
    private async Task<(Actor Coach, List<Actor> Clients)> CoachWithPublishedWeekOnOutsideClientAsync(int daysAgo)
    {
        var (coach, clients) = await CoachWithClientsAsync(4);
        await SeedPublishedTrainingPlanAsync(coach.UserId, clients[2].UserId, TodayUtc.AddDays(-daysAgo));

        return (coach, clients);
    }

    [Fact]
    public async Task List_OutsidePlanClientWhosePublishedWeeksStillRun_ListsInActiveAndOutsidePlanWithReadOnlyFrom()
    {
        var (coach, clients) = await CoachWithPublishedWeekOnOutsideClientAsync(daysAgo: 3);
        // An in-plan client with published weeks never gets the marker.
        await SeedPublishedTrainingPlanAsync(coach.UserId, clients[1].UserId, TodayUtc.AddDays(-3));
        var expectedEnd = EndOfFirstPublishedWeek(TodayUtc.AddDays(-3));

        var active = await ListAsync(coach, "?tab=Active");
        var outside = await ListAsync(coach, "?tab=OutsidePlan");

        active.Clients.Select(c => c.UserId).Should().Equal(Ids(clients, 1, 0, 2));
        outside.Clients.Select(c => c.UserId).Should().Equal(Ids(clients, 3, 2));
        active.Clients.Single(c => c.UserId == clients[2].UserId).ReadOnlyFrom.Should().Be(expectedEnd);
        outside.Clients.Single(c => c.UserId == clients[2].UserId).ReadOnlyFrom.Should().Be(expectedEnd);
        outside.Clients.Single(c => c.UserId == clients[3].UserId).ReadOnlyFrom.Should().BeNull();
        active.Clients.Where(c => c.UserId != clients[2].UserId).Should().OnlyContain(c => c.ReadOnlyFrom == null);
    }

    [Fact]
    public async Task List_OutsidePlanClientWhosePublishedWeeksEnded_IsOnlyInOutsidePlanWithoutReadOnlyFrom()
    {
        var (coach, clients) = await CoachWithPublishedWeekOnOutsideClientAsync(daysAgo: 7);

        var active = await ListAsync(coach, "?tab=Active");
        var outside = await ListAsync(coach, "?tab=OutsidePlan");

        active.Clients.Select(c => c.UserId).Should().Equal(Ids(clients, 1, 0));
        outside.Clients.Select(c => c.UserId).Should().Equal(Ids(clients, 3, 2));
        outside.Clients.Should().OnlyContain(c => c.ReadOnlyFrom == null);
    }

    [Fact]
    public async Task List_TabCountsInPlan_CountsTheActiveTabDefinitionIncludingWindingDownClients()
    {
        var (coach, _) = await CoachWithPublishedWeekOnOutsideClientAsync(daysAgo: 3);

        var all = await ListAsync(coach, "?tab=All");
        var outside = await ListAsync(coach, "?tab=OutsidePlan");

        all.TabCounts.InPlan.Should().Be(3);
        all.TabCounts.OutsidePlan.Should().Be(2, "winding-down clients stay in the Outside plan tab too");
        outside.TabCounts.InPlan.Should().Be(3);
        (await ListAsync(coach, "?tab=Active")).TotalCount.Should().Be(3);
    }

    [Fact]
    public async Task List_WindingDownClientOnLaterPage_StillCountsAndPagesInActiveTab()
    {
        var (coach, clients) = await CoachWithPublishedWeekOnOutsideClientAsync(daysAgo: 3);

        var page1 = await ListAsync(coach, "?tab=Active&page=1&pageSize=2");
        var page2 = await ListAsync(coach, "?tab=Active&page=2&pageSize=2");

        page1.TotalCount.Should().Be(3);
        page1.Clients.Select(c => c.UserId).Should().Equal(Ids(clients, 1, 0));
        page2.Clients.Select(c => c.UserId).Should().Equal(Ids(clients, 2));
        page2.Clients.Single().ReadOnlyFrom.Should().NotBeNull();
    }

    [Fact]
    public async Task List_ArchivedPlanOrAnotherCoachsPlan_DoesNotKeepAnOutsidePlanClientRunning()
    {
        var (coach, clients) = await CoachWithClientsAsync(4);
        var otherCoach = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);
        await SeedPublishedTrainingPlanAsync(
            coach.UserId, clients[2].UserId, TodayUtc.AddDays(-3), TrainingPlanStatus.Archived);
        await SeedPublishedTrainingPlanAsync(otherCoach.UserId, clients[3].UserId, TodayUtc.AddDays(-3));

        var active = await ListAsync(coach, "?tab=Active");
        var outside = await ListAsync(coach, "?tab=OutsidePlan");

        active.Clients.Select(c => c.UserId).Should().Equal(Ids(clients, 1, 0));
        outside.Clients.Should().OnlyContain(c => c.ReadOnlyFrom == null);
    }

    [Fact]
    public async Task List_PublishedWeeksEndingExactlyToday_StillRunButNotOnceTodayHasPassed()
    {
        var (coach, clients) = await CoachWithClientsAsync(4);
        var pinnedNow = DateTimeOffset.UtcNow;
        var pinnedToday = pinnedNow.UtcDateTime.Date;
        // [2] ends on the pinned day itself, [3] ended the day before.
        await SeedPublishedTrainingPlanAsync(coach.UserId, clients[2].UserId, pinnedToday.AddDays(-6));
        await SeedPublishedTrainingPlanAsync(coach.UserId, clients[3].UserId, pinnedToday.AddDays(-7));

        SettableTimeProvider.Now = pinnedNow;
        var pinnedHttp = PinnedFactory.CreateClient();
        pinnedHttp.DefaultRequestHeaders.Authorization = coach.Http.DefaultRequestHeaders.Authorization;

        var active = await ListAsync(coach with { Http = pinnedHttp }, "?tab=Active");

        active.Clients.Select(c => c.UserId).Should().Equal(Ids(clients, 1, 0, 2));
        active.Clients.Single(c => c.UserId == clients[2].UserId).ReadOnlyFrom
            .Should().Be(DateOnly.FromDateTime(pinnedToday));
    }

    private sealed class SettableTimeProvider : TimeProvider
    {
        public static DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static WebApplicationFactory<Program>? _pinnedFactory;

    /// <summary>
    /// A host derived from the shared fixture whose clock reads <see cref="SettableTimeProvider.Now"/>,
    /// memoized in a static field and never disposed (the same pattern as
    /// <c>GetTodayPlanProjectionIntegrationTests</c>).
    /// </summary>
    private WebApplicationFactory<Program> PinnedFactory => _pinnedFactory ??= factory.WithWebHostBuilder(builder =>
    {
        builder.ConfigureServices(services =>
        {
            foreach (var descriptor in services.Where(d => d.ServiceType == typeof(TimeProvider)).ToList())
            {
                services.Remove(descriptor);
            }

            services.AddSingleton<TimeProvider, SettableTimeProvider>();
        });
    });

    // ── fixtures ─────────────────────────────────────────────────────────────

    private async Task<(HttpClient Http, Guid UserId)> SetupTrainerAsync()
    {
        var http = factory.CreateClient();
        var email = UniqueEmail("trainer");

        await TestHelpers.RegisterAsync(http, email, "TestPass1!", "Test", "Trainer", "Trainer");
        var (token, _) = await TestHelpers.LoginAsync(http, email, "TestPass1!");
        TestHelpers.SetBearerToken(http, token);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.AsNoTracking()
            .FirstAsync(u => u.Email == email, TestContext.Current.CancellationToken);

        return (http, user.Id);
    }

    private async Task<(Guid ClientUserId, Guid ClientPublicId)> SetupLinkedClientAsync(
        Guid professionalUserId, bool canViewNutritionPlans = true, bool canViewTrainingPlans = true)
    {
        var clientUserId = await TestHelpers.RegisterLinkedClientAsync(
            factory, professionalUserId, TestContext.Current.CancellationToken,
            canViewNutritionPlans, canViewTrainingPlans);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var clientProfile = await db.ClientProfiles.AsNoTracking()
            .FirstAsync(cp => cp.UserId == clientUserId, TestContext.Current.CancellationToken);

        return (clientUserId, clientProfile.PublicId);
    }

    private async Task SetLinkActiveAsync(Guid clientPublicId, bool isActive)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var clientProfile = await db.ClientProfiles
            .FirstAsync(cp => cp.PublicId == clientPublicId, TestContext.Current.CancellationToken);
        var link = await db.ClientProfessionalLinks
            .FirstAsync(l => l.ClientProfileId == clientProfile.Id, TestContext.Current.CancellationToken);
        link.ConnectionState = isActive ? ConnectionState.Connected : ConnectionState.Ended;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task SeedActiveTrainingPlanAsync(
        Guid authorUserId, Guid clientUserId, DateTime? startDate, int weekCount = 4, string name = "Training Plan")
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        await mongo.TrainingPlans.InsertOneAsync(new TrainingPlan
        {
            ExternalId = Guid.NewGuid(),
            ClientId = clientUserId,
            TrainerId = authorUserId,
            Name = name,
            Status = TrainingPlanStatus.Active,
            StartDate = startDate,
            Weeks = Enumerable.Range(1, weekCount).Select(n => new TrainingWeek { WeekNumber = n }).ToList()
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    private async Task SeedPublishedTrainingPlanAsync(
        Guid authorUserId, Guid clientUserId, DateTime startDate, TrainingPlanStatus status = TrainingPlanStatus.Active)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        await mongo.TrainingPlans.InsertOneAsync(new TrainingPlan
        {
            ExternalId = Guid.NewGuid(),
            ClientId = clientUserId,
            TrainerId = authorUserId,
            Name = "Published Plan",
            Status = status,
            StartDate = startDate,
            Weeks = [new TrainingWeek { WeekNumber = 1, Status = WeekStatus.Published }]
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    private async Task<Guid> CreateTagAsync(Guid professionalUserId, string name)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var professionalProfile = await db.ProfessionalProfiles
            .FirstAsync(pp => pp.UserId == professionalUserId, TestContext.Current.CancellationToken);

        var tag = new ClientTag
        {
            PublicId = Guid.NewGuid(),
            OwnerProfessionalProfileId = professionalProfile.Id,
            Name = name,
            ColorHex = "#3b82f6"
        };
        db.ClientTags.Add(tag);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return tag.PublicId;
    }

    private static async Task AssignTagsAsync(HttpClient http, Guid clientPublicId, params Guid[] tagIds)
    {
        var response = await http.PutAsJsonAsync($"/trainer/clients/{clientPublicId}/tags",
            new { TagIds = tagIds }, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task SeedConversationWithMessageAsync(
        Guid professionalUserId, Guid clientUserId, bool senderIsClient, bool isRead)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var conversation = new Conversation
        {
            PublicId = Guid.NewGuid(),
            ProfessionalUserId = professionalUserId,
            ClientUserId = clientUserId
        };
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.ChatMessages.Add(new ChatMessage
        {
            PublicId = Guid.NewGuid(),
            ConversationId = conversation.Id,
            SenderUserId = senderIsClient ? clientUserId : professionalUserId,
            Text = "hello",
            IsRead = isRead
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }


    private static async Task<GetClientsResponseDto?> Deserialize(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<GetClientsResponseDto>(
            JsonOptions, TestContext.Current.CancellationToken);

    private sealed class GetClientsResponseDto
    {
        public List<ClientSummaryDto> Clients { get; set; } = [];
        public int TotalCount { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
        public TabCountsDto TabCounts { get; set; } = new();
        public FilterCountsDto FilterCounts { get; set; } = new();
        public PlanCountsDto PlanCounts { get; set; } = new();
    }

    private sealed class PlanCountsDto
    {
        public int All { get; set; }
        public int Active { get; set; }
        public int None { get; set; }
        public int EndingSoon { get; set; }
    }

    private sealed class TabCountsDto
    {
        public int All { get; set; }
        public int InPlan { get; set; }
        public int Active { get; set; }
        public int Paused { get; set; }
        public int Archived { get; set; }
        public int OutsidePlan { get; set; }
        public int Pending { get; set; }
    }

    private sealed class FilterCountsDto
    {
        public int All { get; set; }
        public int UnreadMessages { get; set; }
        public int NoMessages { get; set; }
        public int NewCheckIns { get; set; }
        public int MissingCheckIns { get; set; }
        public int EndingSoon { get; set; }
    }

    private sealed class ClientSummaryDto
    {
        public Guid PublicId { get; set; }
        public Guid UserId { get; set; }
        public string Email { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string? AvatarBlobUrl { get; set; }
        public List<TagDto> Tags { get; set; } = [];
        public int UnreadMessageCount { get; set; }
        public bool HasActiveNutritionPlan { get; set; }
        public bool HasActiveTrainingPlan { get; set; }
        public bool IsOutsidePlan { get; set; }
        public DateOnly? ReadOnlyFrom { get; set; }
        public List<object> ActivePlans { get; set; } = [];
    }

    private sealed class TagDto
    {
        public Guid TagId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string ColorHex { get; set; } = string.Empty;
    }
}
