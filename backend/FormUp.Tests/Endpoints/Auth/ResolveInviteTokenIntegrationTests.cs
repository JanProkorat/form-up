using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Auth;

/// <summary>
/// Integration tests for the anonymous <c>POST /auth/invite/resolve</c> lookup.
/// </summary>
[Collection(TestCollection.Name)]
public class ResolveInviteTokenIntegrationTests(FitnessApiFactory factory)
{
    private async Task<string> SeedTokenAsync(
        string email, Func<InvitationTokenBuilder, InvitationTokenBuilder>? configure = null)
    {
        var trainer = await TestActors.Trainer(factory).CreateAsync();
        var token = Guid.NewGuid().ToString("N");
        var builder = EntityBuilder.InvitationToken
            .WithProfessionalProfileId(trainer.ProfileId)
            .WithEmail(email)
            .WithToken(token);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.InvitationTokens.Add((configure?.Invoke(builder) ?? builder).Build());
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return token;
    }

    private async Task<(HttpStatusCode Status, string Body)> ResolveAsync(string token)
    {
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/auth/invite/resolve", new { token }, TestContext.Current.CancellationToken);

        return (response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Resolve_ValidToken_Returns200WithInvitedEmail()
    {
        var email = $"{Guid.NewGuid():N}@invitee.test";
        var token = await SeedTokenAsync(email);

        var (status, body) = await ResolveAsync(token);

        status.Should().Be(HttpStatusCode.OK);
        body.Should().Contain(email);
    }

    [Fact]
    public async Task Resolve_UnknownExpiredAndUsedTokens_ReturnIdenticalBare404()
    {
        var expired = await SeedTokenAsync("expired@invitee.test", b => b.Expired());
        var used = await SeedTokenAsync("used@invitee.test", b => b.Used());

        var unknownResult = await ResolveAsync(Guid.NewGuid().ToString("N"));
        var expiredResult = await ResolveAsync(expired);
        var usedResult = await ResolveAsync(used);

        unknownResult.Status.Should().Be(HttpStatusCode.NotFound);
        expiredResult.Should().Be(unknownResult);
        usedResult.Should().Be(unknownResult);
        unknownResult.Body.Should().NotContain("invitee.test");
    }

    [Fact]
    public async Task Resolve_EmptyToken_Returns400()
    {
        var (status, _) = await ResolveAsync("");

        status.Should().Be(HttpStatusCode.BadRequest);
    }
}
