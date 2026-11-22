using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Endpoints.Billing;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Users;

/// <summary>
/// Integration tests for GDPR account deletion (DELETE /users/me).
/// </summary>
[Collection(TestCollection.Name)]
public class DeleteAccountTests(FitnessApiFactory factory)
{
    private static string UniqueEmail() => $"{Guid.NewGuid():N}@test.com";

    [Fact]
    public async Task DeleteAccount_AuthenticatedUser_Returns204()
    {
        var client = factory.CreateClient();
        var email = UniqueEmail();

        await TestHelpers.RegisterAsync(client, email, "TestPass1!", "John", "Doe", "Client");
        var (accessToken, _) = await TestHelpers.LoginAsync(client, email, "TestPass1!");

        TestHelpers.SetBearerToken(client, accessToken);

        var response = await client.DeleteAsync("/users/me", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DeleteAccount_CannotLoginAfterDeletion()
    {
        var client = factory.CreateClient();
        var email = UniqueEmail();

        await TestHelpers.RegisterAsync(client, email, "TestPass1!", "John", "Doe", "Client");
        var (accessToken, _) = await TestHelpers.LoginAsync(client, email, "TestPass1!");

        TestHelpers.SetBearerToken(client, accessToken);
        await client.DeleteAsync("/users/me", TestContext.Current.CancellationToken);

        // Clear bearer token
        client.DefaultRequestHeaders.Authorization = null;

        var loginResponse = await client.PostAsJsonAsync("/auth/login", new
        {
            Email = email,
            Password = "TestPass1!"
        }, cancellationToken: TestContext.Current.CancellationToken);

        loginResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DeleteAccount_ProfileEndpointReturns401AfterDeletion()
    {
        var client = factory.CreateClient();
        var email = UniqueEmail();

        await TestHelpers.RegisterAsync(client, email, "TestPass1!", "John", "Doe", "Client");
        var (accessToken, _) = await TestHelpers.LoginAsync(client, email, "TestPass1!");

        TestHelpers.SetBearerToken(client, accessToken);
        await client.DeleteAsync("/users/me", TestContext.Current.CancellationToken);

        // The old token still passes JWT validation but user no longer exists
        var profileResponse = await client.GetAsync("/users/me", TestContext.Current.CancellationToken);

        profileResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(Actor Coach, string SubscriptionId)> PayingCoachAsync(SubscriptionStatus? status = null)
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory);
        var subscriptionId = await BillingTestData.SubscribeLiveAsync(factory, coach, plan);

        if (status is { } value)
        {
            await BillingTestData.UpdateRowAsync(factory, coach.ProfileId, row => row.Status = value);
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.RefreshTokens.Add(new RefreshToken { UserId = coach.UserId, Token = Guid.NewGuid().ToString("N"), ExpiresAt = DateTime.UtcNow.AddDays(7) });
        await db.SaveChangesAsync(Ct);

        return (coach, subscriptionId);
    }

    private async Task<(bool User, bool Profile, bool Subscription, bool RefreshTokens)> ExistingRowsAsync(Actor coach)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return (
            await db.Users.AnyAsync(u => u.Id == coach.UserId, Ct),
            await db.ProfessionalProfiles.AnyAsync(p => p.UserId == coach.UserId, Ct),
            await db.CoachSubscriptions.AnyAsync(cs => cs.ProfessionalProfileId == coach.ProfileId, Ct),
            await db.RefreshTokens.AnyAsync(rt => rt.UserId == coach.UserId, Ct));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(SubscriptionStatus.Incomplete)]
    public async Task DeleteAccount_CoachWithProviderSubscription_CancelsItOnce_AndRemovesTheAccount(SubscriptionStatus? status)
    {
        var fake = BillingTestData.ResetProvider(factory);
        var (coach, subscriptionId) = await PayingCoachAsync(status);

        var response = await coach.Http.DeleteAsync("/users/me", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        fake.CancelledNow.Should().Equal(subscriptionId);
        (await ExistingRowsAsync(coach)).Should().Be((false, false, false, false));
        BillingTestData.ResetProvider(factory);
    }

    [Fact]
    public async Task DeleteAccount_WhenTheProviderFails_Returns502_AndKeepsEverything()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var (coach, _) = await PayingCoachAsync();
        fake.Fail = true;

        var response = await coach.Http.DeleteAsync("/users/me", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.BillingProviderError);
        (await ExistingRowsAsync(coach)).Should().Be((true, true, true, true));
        BillingTestData.ResetProvider(factory);
    }

    [Fact]
    public async Task DeleteAccount_FreePlanCoachWhileBillingIsNotConfigured_StillDeletes_WithoutCallingTheProvider()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        fake.IsConfigured = false;

        var response = await coach.Http.DeleteAsync("/users/me", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        fake.CancelledNow.Should().BeEmpty();
        BillingTestData.ResetProvider(factory);
    }
}
