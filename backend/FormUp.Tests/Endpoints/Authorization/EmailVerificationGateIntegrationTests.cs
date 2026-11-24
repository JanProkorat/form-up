using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Authorization;

/// <summary>
/// An unverified account cannot create a client-coach link, and a verified one can (#1517). The route-by-route proof
/// is <c>EmailVerificationGateArchitectureTests</c>; these two cover the flows the issue names.
/// </summary>
[Collection(TestCollection.Name)]
public class EmailVerificationGateIntegrationTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

        return body.RootElement.TryGetProperty("errorCode", out var code) ? code.GetString() : null;
    }

    private async Task<bool> LinkExistsAsync(Actor coach, Actor client)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.ClientProfessionalLinks.AnyAsync(
            link => link.ClientProfileId == client.ProfileId && link.ProfessionalProfileId == coach.ProfileId, Ct);
    }

    private async Task ConfirmEmailAsync(Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Users.Where(user => user.Id == userId)
            .ExecuteUpdateAsync(set => set.SetProperty(user => user.EmailConfirmed, true), Ct);
    }

    [Fact]
    public async Task AcceptInvite_ByUnverifiedClient_Returns403_NoLink_ThenSucceedsOnceVerified()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).Unverified().CreateAsync(Ct);

        var created = await coach.Http.PostAsJsonAsync("/trainer/pending-invites", new { Email = client.Email }, Ct);
        created.StatusCode.Should().Be(HttpStatusCode.OK);
        var inviteId = (await created.Content.ReadFromJsonAsync<InviteRef>(Ct))!.PublicId;

        var refused = await client.Http.PostAsJsonAsync($"/client/invites/{inviteId}/accept", new { }, Ct);

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(refused)).Should().Be(ErrorCodes.EmailNotVerified);
        (await LinkExistsAsync(coach, client)).Should().BeFalse();

        await ConfirmEmailAsync(client.UserId);
        var accepted = await client.Http.PostAsJsonAsync($"/client/invites/{inviteId}/accept", new { }, Ct);

        accepted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await LinkExistsAsync(coach, client)).Should().BeTrue();
    }

    [Fact]
    public async Task AcceptJoinRequest_ByUnverifiedCoach_Returns403_RequestStaysPending_ThenSucceedsOnceVerified()
    {
        var coach = await TestActors.Trainer(factory).Unverified().CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var requestId = Guid.NewGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.ClientRequests.Add(new ClientRequest
            {
                PublicId = requestId,
                ClientProfileId = client.ProfileId,
                ProfessionalProfileId = coach.ProfileId,
                Status = ClientRequestStatus.Pending,
            });
            await db.SaveChangesAsync(Ct);
        }

        var refused = await coach.Http.PostAsJsonAsync($"/trainer/client-requests/{requestId}/accept", new { }, Ct);

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(refused)).Should().Be(ErrorCodes.EmailNotVerified);
        (await LinkExistsAsync(coach, client)).Should().BeFalse();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.ClientRequests.SingleAsync(request => request.PublicId == requestId, Ct)).Status
                .Should().Be(ClientRequestStatus.Pending);
        }

        await ConfirmEmailAsync(coach.UserId);
        var accepted = await coach.Http.PostAsJsonAsync($"/trainer/client-requests/{requestId}/accept", new { }, Ct);

        accepted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await LinkExistsAsync(coach, client)).Should().BeTrue();
    }

    private sealed record InviteRef(Guid PublicId);
}
