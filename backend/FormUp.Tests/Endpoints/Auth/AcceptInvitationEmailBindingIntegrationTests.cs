using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Auth;

/// <summary>
/// POST /auth/invite/accept only honours a token issued to the caller's own email (#2333).
/// </summary>
[Collection(TestCollection.Name)]
public class AcceptInvitationEmailBindingIntegrationTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> SeedTokenAsync(Actor coach, string email)
    {
        var token = Guid.NewGuid().ToString("N");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.InvitationTokens.Add(new InvitationToken
        {
            ProfessionalProfileId = coach.ProfileId,
            Email = email,
            Token = token,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
        });
        await db.SaveChangesAsync(Ct);

        return token;
    }

    private static Task<HttpResponseMessage> AcceptAsync(Actor caller, string token) =>
        caller.Http.PostAsJsonAsync("/auth/invite/accept", new { Token = token }, Ct);

    /// <summary>The 400 body's stable parts: title, detail and the coded errors (no trace ids).</summary>
    private static async Task<string> ErrorShapeAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var root = json.RootElement;

        return string.Join("|",
            root.TryGetProperty("title", out var title) ? title.ToString() : "",
            root.TryGetProperty("detail", out var detail) ? detail.ToString() : "",
            root.TryGetProperty("errors", out var errors) ? errors.GetRawText() : "");
    }

    private async Task<bool> LinkExistsAsync(Actor coach, Actor client)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.ClientProfessionalLinks.AnyAsync(
            l => l.ClientProfileId == client.ProfileId && l.ProfessionalProfileId == coach.ProfileId, Ct);
    }

    [Fact]
    public async Task Accept_TokenForAnotherEmail_Returns400_AndWritesNothing()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var caller = await TestActors.Client(factory).CreateAsync(Ct);
        var token = await SeedTokenAsync(coach, $"{Guid.NewGuid():N}@someone-else.com");

        var response = await AcceptAsync(caller, token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.InvalidInvitation);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await LinkExistsAsync(coach, caller)).Should().BeFalse();
        (await db.ClientProfiles.CountAsync(cp => cp.UserId == caller.UserId, Ct)).Should().Be(1);
        (await db.InvitationTokens.SingleAsync(t => t.Token == token, Ct)).IsUsed.Should().BeFalse();
        (await db.Users.SingleAsync(u => u.Id == caller.UserId, Ct)).EmailConfirmed.Should().BeFalse();
        factory.Services.GetRequiredService<FakeEmailService>().SentVerifications
            .Should().NotContain(v => v.Email == caller.Email);
    }

    [Fact]
    public async Task Accept_UnknownToken_ReturnsSameBodyAsMismatch()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var caller = await TestActors.Client(factory).CreateAsync(Ct);
        var mismatchToken = await SeedTokenAsync(coach, $"{Guid.NewGuid():N}@someone-else.com");

        var mismatch = await AcceptAsync(caller, mismatchToken);
        var unknown = await AcceptAsync(caller, Guid.NewGuid().ToString("N"));

        mismatch.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorShapeAsync(unknown)).Should().Be(await ErrorShapeAsync(mismatch));
        (await unknown.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.InvalidInvitation);
    }

    [Fact]
    public async Task Accept_MatchingEmailDifferingInCaseAndWhitespace_Returns200()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var caller = await TestActors.Client(factory).CreateAsync(Ct);
        var token = await SeedTokenAsync(coach, $"  {caller.Email.ToUpperInvariant()} ");

        var response = await AcceptAsync(caller, token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await LinkExistsAsync(coach, caller)).Should().BeTrue();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.InvitationTokens.SingleAsync(t => t.Token == token, Ct)).IsUsed.Should().BeTrue();
        (await db.Users.SingleAsync(u => u.Id == caller.UserId, Ct)).EmailConfirmed.Should().BeFalse();
    }
}
