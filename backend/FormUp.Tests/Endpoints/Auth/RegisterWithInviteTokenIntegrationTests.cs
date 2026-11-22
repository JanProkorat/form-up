using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Auth;

/// <summary>
/// Integration tests for the invite binding on <c>POST /auth/register</c>: a valid token locks the
/// email and the Client role, a bad token is ignored.
/// </summary>
[Collection(TestCollection.Name)]
public class RegisterWithInviteTokenIntegrationTests(FitnessApiFactory factory)
{
    private static string NewEmail() => $"{Guid.NewGuid():N}@invitee.test";

    private async Task<string> SeedTokenAsync(string email, bool expired = false)
    {
        var trainer = await TestActors.Trainer(factory).CreateAsync();
        var token = Guid.NewGuid().ToString("N");
        var builder = EntityBuilder.InvitationToken
            .WithProfessionalProfileId(trainer.ProfileId)
            .WithEmail(email)
            .WithToken(token);

        if (expired)
        {
            builder.Expired();
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.InvitationTokens.Add(builder.Build());
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return token;
    }

    private async Task<HttpResponseMessage> RegisterAsync(string email, string[] roles, string? inviteToken)
    {
        using var client = factory.CreateClient();

        return await client.PostAsJsonAsync("/auth/register", new
        {
            Email = email,
            Password = "TestPass1!",
            ConfirmPassword = "TestPass1!",
            FirstName = "Invited",
            LastName = "Person",
            Roles = roles,
            GdprConsent = true,
            HealthDataConsent = roles.Contains("Client") ? (bool?)true : null,
            InviteToken = inviteToken
        }, TestContext.Current.CancellationToken);
    }

    private async Task<ApplicationUser?> FindUserAsync(string email)
    {
        using var scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        return await userManager.FindByEmailAsync(email);
    }

    [Fact]
    public async Task Register_ValidTokenTamperedEmail_Returns400AndCreatesNoUser()
    {
        var token = await SeedTokenAsync(NewEmail());
        var otherEmail = NewEmail();

        var response = await RegisterAsync(otherEmail, ["Client"], token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain("INVITE_EMAIL_MISMATCH");
        (await FindUserAsync(otherEmail)).Should().BeNull();
    }

    [Fact]
    public async Task Register_ValidTokenTrainerRole_Returns400AndCreatesNoUser()
    {
        var email = NewEmail();
        var token = await SeedTokenAsync(email);

        var response = await RegisterAsync(email, ["Trainer"], token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain("INVITE_ROLE_MISMATCH");
        (await FindUserAsync(email)).Should().BeNull();
    }

    [Fact]
    public async Task Register_ValidTokenClientPlusNutritionist_Returns400AndCreatesNoUser()
    {
        var email = NewEmail();
        var token = await SeedTokenAsync(email);

        var response = await RegisterAsync(email, ["Client", "Nutritionist"], token);

        // The role-combination rule in RegisterValidator rejects this before the invite check runs.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await FindUserAsync(email)).Should().BeNull();
    }

    [Fact]
    public async Task Register_ValidTokenMatchingEmailInDifferentCase_Returns201AsClient()
    {
        var email = NewEmail();
        var token = await SeedTokenAsync(email);

        var response = await RegisterAsync(email.ToUpperInvariant(),["Client"], token);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var user = await FindUserAsync(email);
        user.Should().NotBeNull();
        user!.EmailConfirmed.Should().BeFalse("the invite does not verify the address");

        using var scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        (await userManager.GetRolesAsync(user)).Should().Equal("Client");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Register_UnknownOrExpiredToken_IsIgnoredAndSignsUpNormally(bool expiredToken)
    {
        var invitedEmail = NewEmail();
        var token = expiredToken ? await SeedTokenAsync(invitedEmail, expired: true) : Guid.NewGuid().ToString("N");
        var email = NewEmail();

        var response = await RegisterAsync(email, ["Trainer"], token);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await FindUserAsync(email)).Should().NotBeNull();
    }
}
