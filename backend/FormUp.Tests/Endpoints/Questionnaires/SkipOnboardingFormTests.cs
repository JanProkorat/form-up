using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static FormUp.Tests.Endpoints.ClientPayments.ClientPaymentFixtures;
using static FormUp.Tests.Endpoints.PaymentSettings.PaymentSettingsFixtures;

namespace FormUp.Tests.Endpoints.Questionnaires;

/// <summary>
/// A coach who picks "no onboarding form" must not have the default form offered to the client.
/// </summary>
[Collection(TestCollection.Name)]
public class SkipOnboardingFormTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private record RequestCreated(Guid PublicId);

    private async Task<long> AddFormAsync(Actor coach, bool isDefault)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var form = EntityBuilder.Questionnaire.WithProfessionalUserId(coach.UserId).Build();
        form.IsDefault = isDefault;
        db.Questionnaires.Add(form);
        await db.SaveChangesAsync(Ct);

        return form.Id;
    }

    private async Task<JsonElement[]> PendingItemsAsync(Actor client)
    {
        var response = await client.Http.GetAsync("/client/questionnaires/pending", Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        return body.GetProperty("items").EnumerateArray().ToArray();
    }

    private async Task<(Actor Coach, Actor Client)> CoachWithDefaultFormAsync()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await AddFormAsync(coach, isDefault: true);

        return (coach, client);
    }

    private async Task<ClientProfessionalLink> StoredLinkAsync(Actor coach, Actor client)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.ClientProfessionalLinks.AsNoTracking().SingleAsync(
            l => l.ClientProfileId == client.ProfileId && l.ProfessionalProfileId == coach.ProfileId, Ct);
    }

    private static async Task<Guid> SendRequestAsync(Actor coach, Actor client)
    {
        var response = await client.Http.PostAsJsonAsync("/client/requests", new { ProfessionalPublicId = coach.PublicId }, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        return (await response.Content.ReadFromJsonAsync<RequestCreated>(Ct))!.PublicId;
    }

    [Fact]
    public async Task Pending_SkippedLinkWithDefaultForm_ListsNoQuestionnaire()
    {
        var (coach, client) = await CoachWithDefaultFormAsync();
        await TestActors.Link(factory, coach, client).SkippingOnboardingForm().CreateAsync(Ct);

        (await PendingItemsAsync(client)).Should().BeEmpty();
    }

    [Fact]
    public async Task Pending_LinkNotSkipped_StillListsTheDefaultForm()
    {
        var (coach, client) = await CoachWithDefaultFormAsync();
        var link = TestActors.Link(factory, coach, client);
        await link.CreateAsync(Ct);

        var items = await PendingItemsAsync(client);

        items.Should().ContainSingle(item => item.GetProperty("linkPublicId").GetGuid() == link.PublicId);
    }

    [Fact]
    public async Task Pending_SkippedLinkWithExplicitForm_StillListsTheExplicitForm()
    {
        var (coach, client) = await CoachWithDefaultFormAsync();
        var explicitFormId = await AddFormAsync(coach, isDefault: false);
        var link = TestActors.Link(factory, coach, client).SkippingOnboardingForm().WithQuestionnaireId(explicitFormId);
        await link.CreateAsync(Ct);

        (await PendingItemsAsync(client)).Should().ContainSingle();
    }

    [Fact]
    public async Task GetQuestionnaire_SkippedLinkWithoutExplicitForm_Returns404()
    {
        var (coach, client) = await CoachWithDefaultFormAsync();
        var link = TestActors.Link(factory, coach, client).SkippingOnboardingForm();
        await link.CreateAsync(Ct);

        var response = await client.Http.GetAsync($"/client/questionnaire?linkPublicId={link.PublicId}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AcceptRequest_WithSkip_CreatesASkippedLinkAndNoFormIsOffered()
    {
        var (coach, client) = await CoachWithDefaultFormAsync();
        var requestId = await SendRequestAsync(coach, client);

        var accepted = await coach.Http.PostAsJsonAsync(
            $"/trainer/client-requests/{requestId}/accept", new { SkipOnboardingForm = true }, Ct);

        accepted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await StoredLinkAsync(coach, client)).SkipOnboardingForm.Should().BeTrue();
        (await PendingItemsAsync(client)).Should().BeEmpty();
    }

    [Fact]
    public async Task AcceptRequest_WithSkipAndQuestionnaire_Returns400WithTheErrorCode()
    {
        var (coach, client) = await CoachWithDefaultFormAsync();
        var requestId = await SendRequestAsync(coach, client);

        var response = await coach.Http.PostAsJsonAsync(
            $"/trainer/client-requests/{requestId}/accept",
            new { SkipOnboardingForm = true, QuestionnairePublicId = Guid.NewGuid() },
            Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("SKIP_ONBOARDING_FORM_WITH_QUESTIONNAIRE");
    }

    [Fact]
    public async Task AcceptRequest_ReactivatingASkippedLinkWithoutTheChoice_ResetsTheFlag()
    {
        var (coach, client) = await CoachWithDefaultFormAsync();
        await TestActors.Link(factory, coach, client).SkippingOnboardingForm().Inactive().CreateAsync(Ct);
        var requestId = await SendRequestAsync(coach, client);

        var accepted = await coach.Http.PostAsJsonAsync($"/trainer/client-requests/{requestId}/accept", new { }, Ct);

        accepted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var link = await StoredLinkAsync(coach, client);
        link.ConnectionState.Should().Be(ConnectionState.Connected);
        link.SkipOnboardingForm.Should().BeFalse();
    }

    [Fact]
    public async Task ProposalAccepted_AfterAProposalWithSkip_CreatesASkippedLink()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await SubscribeAsync(factory, coach, canUsePayments: true);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await AddFormAsync(coach, isDefault: true);
        var packageId = await CreatePackageAsync(coach, 2500m);
        var requestId = await SendRequestAsync(coach, client);

        var proposed = await coach.Http.PostAsJsonAsync(
            $"/trainer/client-requests/{requestId}/propose-package",
            new { PackagePublicId = packageId, SkipOnboardingForm = true },
            Ct);
        proposed.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var accepted = await client.Http.PostAsJsonAsync($"/client/requests/{requestId}/accept-proposal", new { }, Ct);

        accepted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await StoredLinkAsync(coach, client)).SkipOnboardingForm.Should().BeTrue();
        (await PendingItemsAsync(client)).Should().BeEmpty();
    }

    private record InviteCreated(Guid PublicId);

    private static async Task<Guid> InviteAsync(Actor coach, Actor client, bool skipOnboardingForm)
    {
        var response = await coach.Http.PostAsJsonAsync(
            "/trainer/pending-invites", new { client.Email, SkipOnboardingForm = skipOnboardingForm }, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return (await response.Content.ReadFromJsonAsync<InviteCreated>(Ct))!.PublicId;
    }

    [Fact]
    public async Task AcceptInvite_WithSkip_CreatesASkippedLinkAndNoFormIsOffered()
    {
        var (coach, client) = await CoachWithDefaultFormAsync();
        var inviteId = await InviteAsync(coach, client, skipOnboardingForm: true);

        var accepted = await client.Http.PostAsJsonAsync($"/client/invites/{inviteId}/accept", new { }, Ct);

        accepted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await StoredLinkAsync(coach, client)).SkipOnboardingForm.Should().BeTrue();
        (await PendingItemsAsync(client)).Should().BeEmpty();
    }

    [Fact]
    public async Task AcceptInvite_WithoutSkip_StillListsTheDefaultForm()
    {
        var (coach, client) = await CoachWithDefaultFormAsync();
        var inviteId = await InviteAsync(coach, client, skipOnboardingForm: false);

        var accepted = await client.Http.PostAsJsonAsync($"/client/invites/{inviteId}/accept", new { }, Ct);

        accepted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var link = await StoredLinkAsync(coach, client);
        link.SkipOnboardingForm.Should().BeFalse();
        (await PendingItemsAsync(client)).Should().ContainSingle(item => item.GetProperty("linkPublicId").GetGuid() == link.PublicId);
    }

    [Fact]
    public async Task AcceptInvitationToken_AfterAnInviteWithSkip_CreatesASkippedLink()
    {
        var (coach, client) = await CoachWithDefaultFormAsync();
        await InviteAsync(coach, client, skipOnboardingForm: true);

        string token;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            token = await db.InvitationTokens.AsNoTracking()
                .Where(t => t.ProfessionalProfileId == coach.ProfileId && t.Email == client.Email)
                .Select(t => t.Token)
                .SingleAsync(Ct);
        }

        var accepted = await client.Http.PostAsJsonAsync("/auth/invite/accept", new { Token = token }, Ct);

        accepted.StatusCode.Should().Be(HttpStatusCode.OK);
        (await StoredLinkAsync(coach, client)).SkipOnboardingForm.Should().BeTrue();
        (await PendingItemsAsync(client)).Should().BeEmpty();
    }
}
