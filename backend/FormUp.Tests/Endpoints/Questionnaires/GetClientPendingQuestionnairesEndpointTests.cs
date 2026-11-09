using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.Questionnaires.Shared;
using FormUp.Tests.Builders;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Questionnaires;

/// <summary>
/// Integration tests for GET /client/questionnaires/pending —
/// verifies that both PendingDiaryRequests and Items are correctly populated,
/// and that ordering (diary first, questionnaire second) is respected.
/// </summary>
[Collection(TestCollection.Name)]
public class GetClientPendingQuestionnairesEndpointTests(FitnessApiFactory factory)
{
    private static string UniqueEmail(string tag = "pq") =>
        $"{Guid.NewGuid():N}@{tag}-pending.com";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    // ── Setup helpers ──────────────────────────────────────────────────────────

    private async Task<(HttpClient Http, Guid UserId, string Email)> SetupClientAsync()
    {
        var http = factory.CreateClient();
        var email = UniqueEmail("client");
        await TestHelpers.RegisterAsync(http, email, "TestPass1!", "Client", "BannerTest", "Client");
        var (token, _) = await TestHelpers.LoginAsync(http, email, "TestPass1!");
        TestHelpers.SetBearerToken(http, token);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.FirstAsync(u => u.Email == email, TestContext.Current.CancellationToken);
        return (http, user.Id, email);
    }

    private async Task<(HttpClient Http, Guid UserId)> SetupNutritionistAsync()
    {
        var http = factory.CreateClient();
        var email = UniqueEmail("nutr");
        await TestHelpers.RegisterAsync(http, email, "TestPass1!", "Nutr", "BannerTest", "Nutritionist");
        var (token, _) = await TestHelpers.LoginAsync(http, email, "TestPass1!");
        TestHelpers.SetBearerToken(http, token);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.FirstAsync(u => u.Email == email, TestContext.Current.CancellationToken);
        return (http, user.Id);
    }

    private async Task<long> InsertLinkAsync(Guid clientUserId, Guid professionalUserId,
        UserRole role = UserRole.Nutritionist, bool isActive = true, DateTime? activatedAtUtc = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var profProfile = await db.ProfessionalProfiles
            .FirstAsync(p => p.UserId == professionalUserId, TestContext.Current.CancellationToken);
        var clientProfile = await db.ClientProfiles
            .FirstAsync(p => p.UserId == clientUserId, TestContext.Current.CancellationToken);

        var link = new ClientProfessionalLink
        {
            ClientProfileId = clientProfile.Id,
            ProfessionalProfileId = profProfile.Id,
            ProfessionalRole = role,
            ConnectionState = isActive ? ConnectionState.Connected : ConnectionState.Ended,
            CanViewNutritionPlans = role == UserRole.Nutritionist,
            CanViewTrainingPlans = role == UserRole.Trainer,
            PublicId = Guid.NewGuid(),
            DateCreated = DateTime.UtcNow,
            ActivatedAtUtc = activatedAtUtc,
        };
        db.ClientProfessionalLinks.Add(link);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return link.Id;
    }

    private async Task<long> InsertPendingInviteAsync(Guid professionalUserId, string inviteeEmail)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var profProfile = await db.ProfessionalProfiles
            .FirstAsync(p => p.UserId == professionalUserId, TestContext.Current.CancellationToken);

        var invite = new PendingInvite
        {
            ProfessionalProfileId = profProfile.Id,
            Email = inviteeEmail,
            SentAt = DateTime.UtcNow,
            Status = PendingInviteStatus.Pending,
            PublicId = Guid.NewGuid(),
            DateCreated = DateTime.UtcNow,
        };
        db.PendingInvites.Add(invite);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return invite.Id;
    }

    private async Task<Guid> InsertDiaryRequestAsync(
        Guid profUserId,
        long? linkId = null,
        long? inviteId = null,
        PhotoDiaryStatus status = PhotoDiaryStatus.Pending,
        DateTimeOffset? createdAt = null,
        string? message = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var request = new PhotoDiaryRequest
        {
            Id = Guid.NewGuid(),
            ProfessionalId = profUserId,
            LinkId = linkId,
            PendingInviteId = inviteId,
            DurationDays = 7,
            Status = status,
            Mode = PhotoDiaryMode.Workflow,
            Message = message,
            AcceptedAt = status is PhotoDiaryStatus.Accepted or PhotoDiaryStatus.InProgress or PhotoDiaryStatus.Completed
                ? DateTimeOffset.UtcNow : null,
            CompletedAt = status == PhotoDiaryStatus.Completed ? DateTimeOffset.UtcNow : null,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.PhotoDiaryRequests.Add(request);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return request.Id;
    }

    // ── Diary request via active link → returned in PendingDiaryRequests ──────

    [Fact]
    public async Task DiaryRequest_ViaActiveLink_AppearsInPendingDiaryRequests()
    {
        var (_, profId) = await SetupNutritionistAsync();
        var (clientHttp, clientUserId, _) = await SetupClientAsync();

        var linkId = await InsertLinkAsync(clientUserId, profId);
        var requestId = await InsertDiaryRequestAsync(profId, linkId: linkId);

        var response = await clientHttp.GetAsync(
            "/client/questionnaires/pending",
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<BannerResponse>(
            JsonOptions, TestContext.Current.CancellationToken);

        body.Should().NotBeNull();
        var diaryItem = body!.PendingDiaryRequests.Should()
            .ContainSingle(i => i.RequestPublicId == requestId).Which;
        diaryItem.DurationDays.Should().Be(7);
        diaryItem.Status.Should().Be("Pending");
        diaryItem.ProfessionalRole.Should().Be("Nutritionist");
        diaryItem.ProfessionalName.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task DiaryRequest_CarriesModeAndMessage()
    {
        var (_, profId) = await SetupNutritionistAsync();
        var (clientHttp, clientUserId, _) = await SetupClientAsync();

        var linkId = await InsertLinkAsync(clientUserId, profId);
        var requestId = await InsertDiaryRequestAsync(profId, linkId: linkId, message: "Breakfast, please");

        var response = await clientHttp.GetAsync(
            "/client/questionnaires/pending",
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<BannerResponse>(
            JsonOptions, TestContext.Current.CancellationToken);

        var diaryItem = body!.PendingDiaryRequests.Should()
            .ContainSingle(i => i.RequestPublicId == requestId).Which;
        diaryItem.Message.Should().Be("Breakfast, please");
        diaryItem.Mode.Should().Be(PhotoDiaryMode.Workflow);
    }

    [Fact]
    public async Task DiaryRequest_PendingPastEndDate_NotReturned()
    {
        var (_, profId) = await SetupNutritionistAsync();
        var (clientHttp, clientUserId, _) = await SetupClientAsync();

        var linkId = await InsertLinkAsync(clientUserId, profId);
        // 7-day window that ended a day ago: still Pending in the table, but no longer a banner.
        var expiredId = await InsertDiaryRequestAsync(
            profId, linkId: linkId, createdAt: DateTimeOffset.UtcNow.AddDays(-8));

        var response = await clientHttp.GetAsync(
            "/client/questionnaires/pending",
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<BannerResponse>(
            JsonOptions, TestContext.Current.CancellationToken);

        body!.PendingDiaryRequests.Should().NotContain(i => i.RequestPublicId == expiredId);
    }

    // ── Diary request via deactivated link → NOT returned ──────────────────────

    [Fact]
    public async Task DiaryRequest_ViaDeactivatedLink_NotReturned()
    {
        var (_, profId) = await SetupNutritionistAsync();
        var (clientHttp, clientUserId, _) = await SetupClientAsync();

        var linkId = await InsertLinkAsync(clientUserId, profId, isActive: false);
        var requestId = await InsertDiaryRequestAsync(profId, linkId: linkId);

        var response = await clientHttp.GetAsync(
            "/client/questionnaires/pending",
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<BannerResponse>(
            JsonOptions, TestContext.Current.CancellationToken);

        body!.PendingDiaryRequests.Should().NotContain(i => i.RequestPublicId == requestId);
    }

    // ── Deactivated link + matching pending invite → invite request survives ──

    [Fact]
    public async Task DiaryRequest_DeactivatedLinkWithMatchingInvite_OnlyInviteRoutedRequestReturned()
    {
        var (_, profId) = await SetupNutritionistAsync();
        var (clientHttp, clientUserId, clientEmail) = await SetupClientAsync();

        var deactivatedLinkId = await InsertLinkAsync(clientUserId, profId, isActive: false);
        var inviteId = await InsertPendingInviteAsync(profId, clientEmail);

        var linkRoutedRequestId = await InsertDiaryRequestAsync(profId, linkId: deactivatedLinkId);
        var inviteRoutedRequestId = await InsertDiaryRequestAsync(profId, inviteId: inviteId);

        var response = await clientHttp.GetAsync(
            "/client/questionnaires/pending",
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<BannerResponse>(
            JsonOptions, TestContext.Current.CancellationToken);

        body!.PendingDiaryRequests.Should().Contain(i => i.RequestPublicId == inviteRoutedRequestId);
        body.PendingDiaryRequests.Should().NotContain(i => i.RequestPublicId == linkRoutedRequestId);
    }

    // ── Diary request via pending invite → returned (email-matched) ───────────

    [Fact]
    public async Task DiaryRequest_ViaPendingInvite_AppearsInPendingDiaryRequests()
    {
        var (_, profId) = await SetupNutritionistAsync();
        var (clientHttp, _, clientEmail) = await SetupClientAsync();

        var inviteId = await InsertPendingInviteAsync(profId, clientEmail);
        var requestId = await InsertDiaryRequestAsync(profId, inviteId: inviteId);

        var response = await clientHttp.GetAsync(
            "/client/questionnaires/pending",
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<BannerResponse>(
            JsonOptions, TestContext.Current.CancellationToken);

        body!.PendingDiaryRequests.Should().Contain(i => i.RequestPublicId == requestId);
    }

    // ── Non-pending statuses are excluded ─────────────────────────────────────

    [Fact]
    public async Task DiaryRequest_NonPendingStatus_NotReturned()
    {
        var (_, profId) = await SetupNutritionistAsync();
        var (clientHttp, clientUserId, _) = await SetupClientAsync();

        var linkId = await InsertLinkAsync(clientUserId, profId);

        var acceptedId = await InsertDiaryRequestAsync(profId, linkId: linkId, status: PhotoDiaryStatus.Accepted);
        var dismissedId = await InsertDiaryRequestAsync(profId, linkId: linkId, status: PhotoDiaryStatus.Dismissed);
        var inProgressId = await InsertDiaryRequestAsync(profId, linkId: linkId, status: PhotoDiaryStatus.InProgress);
        var completedId = await InsertDiaryRequestAsync(profId, linkId: linkId, status: PhotoDiaryStatus.Completed);

        var response = await clientHttp.GetAsync(
            "/client/questionnaires/pending",
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<BannerResponse>(
            JsonOptions, TestContext.Current.CancellationToken);

        var ids = body!.PendingDiaryRequests.Select(i => i.RequestPublicId).ToList();
        ids.Should().NotContain(acceptedId);
        ids.Should().NotContain(dismissedId);
        ids.Should().NotContain(inProgressId);
        ids.Should().NotContain(completedId);
    }

    // ── IDOR: diary request for another client is not returned ────────────────

    [Fact]
    public async Task DiaryRequest_BelongingToAnotherClient_NotReturned()
    {
        var (_, profId) = await SetupNutritionistAsync();
        var (clientHttp, _, _) = await SetupClientAsync();
        var (_, otherClientId, _) = await SetupClientAsync();

        var otherLinkId = await InsertLinkAsync(otherClientId, profId);
        var otherRequestId = await InsertDiaryRequestAsync(profId, linkId: otherLinkId);

        var response = await clientHttp.GetAsync(
            "/client/questionnaires/pending",
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<BannerResponse>(
            JsonOptions, TestContext.Current.CancellationToken);

        body!.PendingDiaryRequests.Should().NotContain(i => i.RequestPublicId == otherRequestId);
    }

    // ── Mixed: both a diary request and a questionnaire → both returned ───────

    [Fact]
    public async Task Mixed_PendingDiaryAndQuestionnaire_BothReturned()
    {
        var (_, profId) = await SetupNutritionistAsync();
        var (clientHttp, clientUserId, _) = await SetupClientAsync();

        var linkId = await InsertLinkAsync(clientUserId, profId);
        var diaryRequestId = await InsertDiaryRequestAsync(profId, linkId: linkId);

        // Seed a questionnaire for this professional so the pending questionnaire list is non-empty
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var questionnaire = new Questionnaire
            {
                PublicId = Guid.NewGuid(),
                ProfessionalId = profId,
                Title = "Onboarding",
                IsDefault = true,
                IsActive = true,
                DateCreated = DateTime.UtcNow,
            };
            db.Questionnaires.Add(questionnaire);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var response = await clientHttp.GetAsync(
            "/client/questionnaires/pending",
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<BannerResponse>(
            JsonOptions, TestContext.Current.CancellationToken);

        body!.PendingDiaryRequests.Should().Contain(i => i.RequestPublicId == diaryRequestId);
        body.Items.Should().NotBeEmpty("the questionnaire seeded for this professional should appear");
    }

    // ── Empty: no pending of either type → both arrays are empty ─────────────

    [Fact]
    public async Task EmptyClient_NoPendingOfEitherType_BothArraysEmpty()
    {
        var (clientHttp, _, _) = await SetupClientAsync();

        var response = await clientHttp.GetAsync(
            "/client/questionnaires/pending",
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<BannerResponse>(
            JsonOptions, TestContext.Current.CancellationToken);

        body.Should().NotBeNull();
        body!.PendingDiaryRequests.Should().BeEmpty();
        body.Items.Should().BeEmpty();
    }

    // ── Ordering: diary CreatedAt DESC ────────────────────────────────────────

    [Fact]
    public async Task DiaryRequests_OrderedByCreatedAtDescending()
    {
        var (_, profId) = await SetupNutritionistAsync();
        var (clientHttp, clientUserId, _) = await SetupClientAsync();

        var linkId = await InsertLinkAsync(clientUserId, profId);

        var olderTime = DateTimeOffset.UtcNow.AddDays(-2);
        var newerTime = DateTimeOffset.UtcNow.AddDays(-1);

        var olderRequestId = await InsertDiaryRequestAsync(profId, linkId: linkId, createdAt: olderTime);
        var newerRequestId = await InsertDiaryRequestAsync(profId, linkId: linkId, createdAt: newerTime);

        var response = await clientHttp.GetAsync(
            "/client/questionnaires/pending",
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<BannerResponse>(
            JsonOptions, TestContext.Current.CancellationToken);

        var diaryIds = body!.PendingDiaryRequests
            .Select(i => i.RequestPublicId)
            .ToList();

        // newerRequestId should appear before olderRequestId
        diaryIds.Should().Contain(newerRequestId);
        diaryIds.Should().Contain(olderRequestId);
        diaryIds.IndexOf(newerRequestId).Should().BeLessThan(diaryIds.IndexOf(olderRequestId));
    }

    // ── Returning client: onboarding form follows the current collaboration ────

    private async Task<long> InsertOnboardingQuestionnaireAsync(Guid professionalUserId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var questionnaire = new Questionnaire
        {
            PublicId = Guid.NewGuid(),
            ProfessionalId = professionalUserId,
            Title = "Onboarding",
            IsDefault = true,
            IsActive = true,
            Type = QuestionnaireType.Onboarding,
            DateCreated = DateTime.UtcNow,
        };
        db.Questionnaires.Add(questionnaire);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return questionnaire.Id;
    }

    private async Task<Guid> InsertResponseAsync(
        long questionnaireId, Guid clientUserId, Guid professionalUserId, long linkId,
        QuestionnaireResponseStatus status, DateTime? submittedAt = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var response = new QuestionnaireResponse
        {
            PublicId = Guid.NewGuid(),
            QuestionnaireId = questionnaireId,
            ClientId = clientUserId,
            ProfessionalId = professionalUserId,
            LinkId = linkId,
            Status = status,
            SubmittedAt = submittedAt,
        };
        db.QuestionnaireResponses.Add(response);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return response.PublicId;
    }

    private async Task<List<QuestionnaireItem>> GetItemsAsync(HttpClient clientHttp)
    {
        var response = await clientHttp.GetAsync(
            "/client/questionnaires/pending",
            TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<BannerResponse>(
            JsonOptions, TestContext.Current.CancellationToken);
        return body!.Items;
    }

    [Fact]
    public async Task Onboarding_AnsweredInEarlierCollaboration_ReactivatedLink_PendingAgain()
    {
        var (_, profId) = await SetupNutritionistAsync();
        var (clientHttp, clientUserId, _) = await SetupClientAsync();
        var questionnaireId = await InsertOnboardingQuestionnaireAsync(profId);

        var activatedAt = DateTime.UtcNow;
        var linkId = await InsertLinkAsync(clientUserId, profId, activatedAtUtc: activatedAt);
        await InsertResponseAsync(questionnaireId, clientUserId, profId, linkId,
            QuestionnaireResponseStatus.Submitted, submittedAt: activatedAt.AddDays(-29));

        var items = await GetItemsAsync(clientHttp);

        items.Should().ContainSingle().Which.ResponsePublicId.Should().BeNull();
    }

    [Fact]
    public async Task Onboarding_AnsweredInCurrentCollaboration_NotPending()
    {
        var (_, profId) = await SetupNutritionistAsync();
        var (clientHttp, clientUserId, _) = await SetupClientAsync();
        var questionnaireId = await InsertOnboardingQuestionnaireAsync(profId);

        var activatedAt = DateTime.UtcNow.AddDays(-2);
        var linkId = await InsertLinkAsync(clientUserId, profId, activatedAtUtc: activatedAt);
        await InsertResponseAsync(questionnaireId, clientUserId, profId, linkId,
            QuestionnaireResponseStatus.Submitted, submittedAt: activatedAt.AddHours(2));

        var items = await GetItemsAsync(clientHttp);

        items.Should().BeEmpty();
    }

    [Fact]
    public async Task Onboarding_LegacyLinkWithoutActivationTime_EarlierSubmissionStillSuppresses()
    {
        var (_, profId) = await SetupNutritionistAsync();
        var (clientHttp, clientUserId, _) = await SetupClientAsync();
        var questionnaireId = await InsertOnboardingQuestionnaireAsync(profId);

        var linkId = await InsertLinkAsync(clientUserId, profId);
        await InsertResponseAsync(questionnaireId, clientUserId, profId, linkId,
            QuestionnaireResponseStatus.Submitted, submittedAt: DateTime.UtcNow.AddDays(-29));

        var items = await GetItemsAsync(clientHttp);

        items.Should().BeEmpty();
    }

    [Fact]
    public async Task Onboarding_ReactivatedLinkWithOldInProgressResponse_OldResponseNotShown()
    {
        var (_, profId) = await SetupNutritionistAsync();
        var (clientHttp, clientUserId, _) = await SetupClientAsync();
        var questionnaireId = await InsertOnboardingQuestionnaireAsync(profId);

        // DateCreated is stamped on insert, so the response is created first and the
        // link is re-activated afterwards, as the real flow does.
        var linkId = await InsertLinkAsync(clientUserId, profId, activatedAtUtc: DateTime.UtcNow.AddDays(-30));
        var oldResponseId = await InsertResponseAsync(questionnaireId, clientUserId, profId, linkId,
            QuestionnaireResponseStatus.InProgress);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var link = await db.ClientProfessionalLinks.FirstAsync(l => l.Id == linkId, TestContext.Current.CancellationToken);
            link.ActivatedAtUtc = DateTime.UtcNow.AddSeconds(1);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var items = await GetItemsAsync(clientHttp);

        var item = items.Should().ContainSingle().Which;
        item.ResponsePublicId.Should().NotBe(oldResponseId);
        item.ResponsePublicId.Should().BeNull();
        item.ResponseStatus.Should().BeNull();
    }

    // ── Question count, sent date and ordering ─────────────────────────────────

    private static readonly SeedQuestion[] MixedQuestions =
    [
        new("a", QuestionTypes.ShortText),
        new("b", QuestionTypes.Number),
        new("heading", QuestionTypes.Section),
        new("upload", QuestionTypes.FileUpload),
        new("secret", QuestionTypes.ShortText, Hidden: true),
    ];

    private async Task BackdateResponseAsync(long responseId, DateTime createdAt)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.QuestionnaireResponses
            .Where(r => r.Id == responseId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.DateCreated, createdAt), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task QuestionCount_ExistingResponse_ExcludesHeadingsHiddenAndFileUpload()
    {
        var scenario = await QuestionnaireAnswerScenario.CreateAsync(factory, MixedQuestions);

        var items = await GetItemsAsync(scenario.Client.Http);

        items.Should().ContainSingle(i => i.ResponsePublicId == scenario.ResponsePublicId)
            .Which.QuestionCount.Should().Be(2);
    }

    [Fact]
    public async Task QuestionCount_LinkWithoutResponse_ReportsRealCount()
    {
        var scenario = await QuestionnaireAnswerScenario.CreateAsync(factory, MixedQuestions, withResponse: false);

        var items = await GetItemsAsync(scenario.Client.Http);

        var item = items.Should().ContainSingle(i => i.LinkPublicId == scenario.LinkPublicId).Which;
        item.ResponsePublicId.Should().BeNull();
        item.QuestionCount.Should().Be(2);
    }

    [Fact]
    public async Task SentAt_ExistingResponse_IsResponseCreationTime()
    {
        var sentAt = DateTime.UtcNow.AddDays(-3);
        var scenario = await QuestionnaireAnswerScenario.CreateAsync(
            factory, MixedQuestions, activatedAtUtc: DateTime.UtcNow.AddDays(-10));
        await BackdateResponseAsync(scenario.ResponseId, sentAt);

        var items = await GetItemsAsync(scenario.Client.Http);

        items.Should().ContainSingle(i => i.ResponsePublicId == scenario.ResponsePublicId)
            .Which.SentAt.Should().BeCloseTo(sentAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task SentAt_LinkWithoutResponse_IsLinkActivationTime()
    {
        var activatedAt = DateTime.UtcNow.AddDays(-4);
        var scenario = await QuestionnaireAnswerScenario.CreateAsync(
            factory, MixedQuestions, activatedAtUtc: activatedAt, withResponse: false);

        var items = await GetItemsAsync(scenario.Client.Http);

        items.Should().ContainSingle(i => i.LinkPublicId == scenario.LinkPublicId)
            .Which.SentAt.Should().BeCloseTo(activatedAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task SentAt_LegacyLinkWithoutActivationTime_FallsBackToLinkCreationTime()
    {
        var scenario = await QuestionnaireAnswerScenario.CreateAsync(factory, MixedQuestions, withResponse: false);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var link = await db.ClientProfessionalLinks
            .AsNoTracking()
            .FirstAsync(l => l.Id == scenario.LinkId, TestContext.Current.CancellationToken);
        link.ActivatedAtUtc.Should().BeNull();

        var items = await GetItemsAsync(scenario.Client.Http);

        items.Should().ContainSingle(i => i.LinkPublicId == scenario.LinkPublicId)
            .Which.SentAt.Should().BeCloseTo(link.DateCreated, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Items_AcrossTwoCoaches_AreOldestFirst()
    {
        var first = await QuestionnaireAnswerScenario.CreateAsync(
            factory, MixedQuestions, activatedAtUtc: DateTime.UtcNow.AddDays(-10));
        // Newer link sits on the response path, older one on the no-response path, so the sort
        // has to compare timestamps from both sources.
        var older = await QuestionnaireAnswerScenario.CreateAsync(
            factory, MixedQuestions, activatedAtUtc: DateTime.UtcNow.AddDays(-6),
            existingClient: first.Client, withResponse: false);
        await BackdateResponseAsync(first.ResponseId, DateTime.UtcNow.AddDays(-2));

        var items = await GetItemsAsync(first.Client.Http);

        items.Select(i => i.LinkPublicId)
            .Should().Equal(older.LinkPublicId, first.LinkPublicId);
    }

    // ── Response shape helpers ─────────────────────────────────────────────────

    private record DiaryItem(
        Guid RequestPublicId,
        string ProfessionalName,
        string? ProfessionalRole,
        int DurationDays,
        string Status,
        Guid? PlanId,
        DateTimeOffset CreatedAt,
        PhotoDiaryMode? Mode = null,
        string? Message = null);

    private record QuestionnaireItem(
        Guid LinkPublicId,
        string ProfessionalName,
        string? ProfessionalRole,
        Guid? ResponsePublicId = null,
        string? ResponseStatus = null,
        int QuestionCount = 0,
        DateTime SentAt = default);

    private record BannerResponse(
        List<DiaryItem> PendingDiaryRequests,
        List<QuestionnaireItem> Items);

    [Fact]
    public async Task Pending_CheckIns_HidesPastDueOneAndKeepsNotYetDueWithItsFields()
    {
        var ct = TestContext.Current.CancellationToken;
        var scenario = await CheckInFixtures.CreateScenarioAsync(factory);
        await CheckInFixtures.AddCheckInAsync(
            factory, scenario, CheckInFixtures.Monday(1), DateTime.UtcNow.AddHours(-1));
        var openDue = DateTime.UtcNow.AddDays(1);
        var openId = await CheckInFixtures.AddCheckInAsync(factory, scenario, CheckInFixtures.Monday(0), openDue);

        var response = await scenario.Client.Http.GetAsync("/client/questionnaires/pending", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<
            Application.Features.Questionnaires.GetClientPendingQuestionnaires.GetClientPendingQuestionnairesResponse>(JsonOptions, ct);
        var item = body!.Items.Should().ContainSingle().Subject;
        item.ResponsePublicId.Should().Be(openId);
        item.Kind.Should().Be(QuestionnaireResponseKind.CheckIn);
        item.CheckInWeekStart.Should().Be(CheckInFixtures.Monday(0));
        item.CheckInDueAtUtc.Should().BeCloseTo(openDue, TimeSpan.FromSeconds(1));
    }
}
