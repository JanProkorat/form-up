using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Questionnaires;

/// <summary>
/// Integration tests for POST /client/questionnaire/response — this endpoint's migration to
/// <see cref="FormUp.Application.Domain.Interfaces.IClientLinkAuthorizationService"/>
/// (issue #960) had no test coverage of any kind. Unlike the other sites this epic migrated,
/// this one changed control flow rather than swapping one predicate for another: the capability
/// lookup runs, then a second supplemental query resolves the link's database Id for the FK the
/// service does not expose.
/// </summary>
[Collection(TestCollection.Name)]
public class CreateResponseEndpointTests(FitnessApiFactory factory)
{
    private static string UniqueEmail(string tag) => $"{Guid.NewGuid():N}@{tag}-cr.com";

    // ── Setup helpers ──────────────────────────────────────────────────────────

    private async Task<(HttpClient Http, Guid UserId)> SetupClientAsync()
    {
        var http = factory.CreateClient();
        var email = UniqueEmail("client");
        await TestHelpers.RegisterAsync(http, email, "TestPass1!", "Test", "CRTest", "Client");
        var (token, _) = await TestHelpers.LoginAsync(http, email, "TestPass1!");
        TestHelpers.SetBearerToken(http, token);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.FirstAsync(u => u.Email == email, TestContext.Current.CancellationToken);
        return (http, user.Id);
    }

    private async Task<Guid> SetupProfessionalAsync()
    {
        var http = factory.CreateClient();
        var email = UniqueEmail("professional");
        await TestHelpers.RegisterAsync(http, email, "TestPass1!", "Coach", "CRTest", "Trainer");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.FirstAsync(u => u.Email == email, TestContext.Current.CancellationToken);
        return user.Id;
    }

    private async Task<long> InsertActiveLinkAsync(Guid clientUserId, Guid professionalUserId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var professionalProfile = await db.ProfessionalProfiles
            .FirstAsync(p => p.UserId == professionalUserId, TestContext.Current.CancellationToken);
        var clientProfile = await db.ClientProfiles
            .FirstAsync(p => p.UserId == clientUserId, TestContext.Current.CancellationToken);

        var link = new ClientProfessionalLink
        {
            ClientProfileId = clientProfile.Id,
            ProfessionalProfileId = professionalProfile.Id,
            ProfessionalRole = UserRole.Trainer,
            ConnectionState = ConnectionState.Connected,
            CanViewNutritionPlans = true,
            CanViewTrainingPlans = true,
            PublicId = Guid.NewGuid(),
            DateCreated = DateTime.UtcNow,
        };
        db.ClientProfessionalLinks.Add(link);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return link.Id;
    }

    private async Task<Guid> InsertQuestionnaireAsync(Guid professionalUserId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var questionnaire = new Questionnaire
        {
            PublicId = Guid.NewGuid(),
            ProfessionalId = professionalUserId,
            Title = "Onboarding",
            IsActive = true,
            DateCreated = DateTime.UtcNow,
        };
        db.Questionnaires.Add(questionnaire);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return questionnaire.PublicId;
    }

    // ── Deny: no active link between the client and the questionnaire's owner → 404 ──

    [Fact]
    public async Task CreateResponse_NoActiveLinkToQuestionnaireOwner_Returns404()
    {
        var (clientHttp, _) = await SetupClientAsync();
        var professionalId = await SetupProfessionalAsync();
        var questionnairePublicId = await InsertQuestionnaireAsync(professionalId);

        // Deliberately no ClientProfessionalLink inserted between this client and this professional.

        var response = await clientHttp.PostAsJsonAsync(
            "/client/questionnaire/response",
            new { QuestionnairePublicId = questionnairePublicId },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Happy path: active link present → 201, persisted response carries the link's Id ──

    [Fact]
    public async Task CreateResponse_ActiveLinkPresent_Returns201AndPersistsLinkId()
    {
        var (clientHttp, clientUserId) = await SetupClientAsync();
        var professionalId = await SetupProfessionalAsync();
        var questionnairePublicId = await InsertQuestionnaireAsync(professionalId);
        var linkId = await InsertActiveLinkAsync(clientUserId, professionalId);

        var response = await clientHttp.PostAsJsonAsync(
            "/client/questionnaire/response",
            new { QuestionnairePublicId = questionnairePublicId },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await response.Content.ReadFromJsonAsync<ResponseBody>(
            cancellationToken: TestContext.Current.CancellationToken);

        body.Should().NotBeNull();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var persisted = await db.QuestionnaireResponses
            .AsNoTracking()
            .FirstAsync(r => r.PublicId == body!.ResponsePublicId, TestContext.Current.CancellationToken);

        persisted.LinkId.Should().Be(linkId);
        persisted.Kind.Should().Be(QuestionnaireResponseKind.Onboarding);
    }

    private record ResponseBody(Guid ResponsePublicId);
}
