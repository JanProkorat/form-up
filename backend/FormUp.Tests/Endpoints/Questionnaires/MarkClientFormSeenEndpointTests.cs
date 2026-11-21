using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.Questionnaires.GetClientForms;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Questionnaires;

/// <summary>
/// POST /trainer/clients/{ClientPublicId}/forms/{ResponsePublicId}/seen: the coach opens a submitted form.
/// </summary>
[Collection(TestCollection.Shard4)]
public class MarkClientFormSeenEndpointTests(FitnessApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(Actor Coach, Actor Client, long LinkId, long FormId)> SetupAsync()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var linkId = await TestActors.Link(factory, coach, client).CreateAsync(Ct);
        var form = await InsertFormAsync(coach);

        return (coach, client, linkId, form);
    }

    private async Task<long> InsertFormAsync(Actor coach)
    {
        var form = EntityBuilder.Questionnaire.WithProfessionalUserId(coach.UserId).Build();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Questionnaires.Add(form);
        await db.SaveChangesAsync(Ct);

        return form.Id;
    }

    private async Task<Guid> InsertResponseAsync(
        Actor coach, Actor client, long linkId, long formId, QuestionnaireResponseStatus status)
    {
        var response = EntityBuilder.QuestionnaireResponse
            .ForQuestionnaire(formId)
            .OnLink(linkId)
            .FromClient(client.UserId)
            .FromProfessional(coach.UserId)
            .WithStatus(status, status == QuestionnaireResponseStatus.Submitted ? DateTime.UtcNow.AddHours(-1) : null)
            .Build();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.QuestionnaireResponses.Add(response);
        await db.SaveChangesAsync(Ct);

        return response.PublicId;
    }

    private static Task<HttpResponseMessage> MarkSeenAsync(Actor coach, Guid clientPublicId, Guid responsePublicId) =>
        coach.Http.PostAsync(
            $"/trainer/clients/{clientPublicId}/forms/{responsePublicId}/seen",
            new StringContent("{}", Encoding.UTF8, "application/json"),
            Ct);

    private static async Task<ClientFormItem> FormAsync(Actor coach, Actor client, Guid responsePublicId)
    {
        var response = await coach.Http.GetAsync($"/trainer/clients/{client.PublicId}/forms", Ct);
        var forms = (await response.Content.ReadFromJsonAsync<GetClientFormsResponse>(JsonOptions, Ct))!;

        return forms.Current.Single(f => f.ResponsePublicId == responsePublicId);
    }

    [Fact]
    public async Task MarkSeen_SubmittedForm_FlipsItFromNewToSeen()
    {
        var (coach, client, linkId, formId) = await SetupAsync();
        var responseId = await InsertResponseAsync(coach, client, linkId, formId, QuestionnaireResponseStatus.Submitted);
        var before = await FormAsync(coach, client, responseId);

        var response = await MarkSeenAsync(coach, client.PublicId, responseId);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        before.IsNew.Should().BeTrue();
        before.SeenAt.Should().BeNull();
        var after = await FormAsync(coach, client, responseId);
        after.IsNew.Should().BeFalse();
        after.SeenAt.Should().NotBeNull();
    }

    [Fact]
    public async Task MarkSeen_SecondCall_Returns204AndKeepsTheFirstTimestamp()
    {
        var (coach, client, linkId, formId) = await SetupAsync();
        var responseId = await InsertResponseAsync(coach, client, linkId, formId, QuestionnaireResponseStatus.Submitted);
        (await MarkSeenAsync(coach, client.PublicId, responseId)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var firstSeenAt = (await FormAsync(coach, client, responseId)).SeenAt;

        var second = await MarkSeenAsync(coach, client.PublicId, responseId);

        second.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await FormAsync(coach, client, responseId)).SeenAt.Should().Be(firstSeenAt);
    }

    [Theory]
    [InlineData(QuestionnaireResponseStatus.Pending)]
    [InlineData(QuestionnaireResponseStatus.InProgress)]
    public async Task MarkSeen_FormNotSubmittedYet_Returns409WithErrorCode(QuestionnaireResponseStatus status)
    {
        var (coach, client, linkId, formId) = await SetupAsync();
        var responseId = await InsertResponseAsync(coach, client, linkId, formId, status);

        var response = await MarkSeenAsync(coach, client.PublicId, responseId);

        var raw = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict, raw);
        JsonDocument.Parse(raw).RootElement.GetProperty("errorCode").GetString()
            .Should().Be(ErrorCodes.QuestionnaireResponseNotSubmitted);
    }

    [Fact]
    public async Task MarkSeen_ResponseOfAnotherCoach_Returns404AndLeavesItNew()
    {
        var (coach, client, _, _) = await SetupAsync();
        var otherCoach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var otherLinkId = await TestActors.Link(factory, otherCoach, client).AsRole(UserRole.Nutritionist).CreateAsync(Ct);
        var theirs = await InsertResponseAsync(
            otherCoach, client, otherLinkId, await InsertFormAsync(otherCoach), QuestionnaireResponseStatus.Submitted);

        var response = await MarkSeenAsync(coach, client.PublicId, theirs);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.QuestionnaireResponses.AsNoTracking().SingleAsync(r => r.PublicId == theirs, Ct))
            .ReviewedByCoachAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task MarkSeen_ResponseOfAnotherClientThanTheRoute_Returns404()
    {
        var (coach, client, _, formId) = await SetupAsync();
        var otherClient = await TestActors.Client(factory).CreateAsync(Ct);
        var otherLinkId = await TestActors.Link(factory, coach, otherClient).CreateAsync(Ct);
        var otherResponse = await InsertResponseAsync(
            coach, otherClient, otherLinkId, formId, QuestionnaireResponseStatus.Submitted);

        var response = await MarkSeenAsync(coach, client.PublicId, otherResponse);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task MarkSeen_UnknownResponse_Returns404()
    {
        var (coach, client, _, _) = await SetupAsync();

        var response = await MarkSeenAsync(coach, client.PublicId, Guid.NewGuid());

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task MarkSeen_CoachWithoutALiveLink_Returns404()
    {
        var (coach, client, linkId, formId) = await SetupAsync();
        var responseId = await InsertResponseAsync(coach, client, linkId, formId, QuestionnaireResponseStatus.Submitted);
        var stranger = await TestActors.Trainer(factory).CreateAsync(Ct);

        var response = await MarkSeenAsync(stranger, client.PublicId, responseId);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
