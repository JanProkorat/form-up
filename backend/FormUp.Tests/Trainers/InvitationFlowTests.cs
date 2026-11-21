using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Trainers;

/// <summary>
/// Integration tests for the full invitation flow:
/// trainer invites client, client accepts, trainer sees client in list and dashboard.
/// </summary>
[Collection(TestCollection.Shard4)]
public class InvitationFlowTests(FitnessApiFactory factory)
{
    private static string UniqueEmail() => $"{Guid.NewGuid():N}@test.com";

    /// <summary>
    /// Creates a pending invite for <paramref name="clientEmail"/> via the real
    /// POST /trainer/pending-invites route, then reads the one-time token minted alongside it
    /// straight from the database — the response body carries no token, and the email itself is
    /// enqueued to a background worker (#1109), so it cannot be read reliably in a test.
    /// </summary>
    private async Task<string> CreatePendingInviteAndGetTokenAsync(HttpClient client, string clientEmail)
    {
        var inviteResponse = await client.PostAsJsonAsync("/trainer/pending-invites", new
        {
            Email = clientEmail
        }, cancellationToken: TestContext.Current.CancellationToken);

        inviteResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Application.Infrastructure.Data.ApplicationDbContext>();
        var invitationToken = await db.InvitationTokens.FirstAsync(t => t.Email == clientEmail);

        return invitationToken.Token;
    }

    [Fact]
    public async Task AcceptInvitation_FullFlow_CreatesLink()
    {
        var client = factory.CreateClient();
        var trainerEmail = UniqueEmail();
        var clientEmail = UniqueEmail();

        // 1. Trainer registers, logs in, has profile
        var trainerToken = await RegisterAndLoginTrainer(client, trainerEmail);
        TestHelpers.SetBearerToken(client, trainerToken);

        // 2. Trainer invites client
        var invitationToken = await CreatePendingInviteAndGetTokenAsync(client, clientEmail);

        // 3. Client registers and logs in
        await TestHelpers.RegisterAsync(client, clientEmail, "TestPass1!", "Jane", "Client", "Client");
        var (clientAccessToken, _) = await TestHelpers.LoginAsync(client, clientEmail, "TestPass1!");

        TestHelpers.SetBearerToken(client, clientAccessToken);

        // 4. Client accepts invitation
        var acceptResponse = await client.PostAsJsonAsync("/auth/invite/accept", new
        {
            Token = invitationToken
        }, cancellationToken: TestContext.Current.CancellationToken);

        acceptResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var acceptBody = await acceptResponse.Content.ReadFromJsonAsync<AcceptResult>(cancellationToken: TestContext.Current.CancellationToken);
        acceptBody!.Message.Should().Be("Invitation accepted successfully.");

        // 5. Trainer checks clients list
        TestHelpers.SetBearerToken(client, trainerToken);

        var clientsResponse = await client.GetAsync("/trainer/clients", TestContext.Current.CancellationToken);
        clientsResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var clientsBody = await clientsResponse.Content.ReadFromJsonAsync<ClientsResult>(cancellationToken: TestContext.Current.CancellationToken);
        clientsBody!.TotalCount.Should().BeGreaterThan(0);

        var linkedClient = clientsBody.Clients.Should().Contain(c => c.Email == clientEmail).Subject;
        linkedClient.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task AcceptInvitation_ExpiredToken_Returns400()
    {
        var client = factory.CreateClient();
        var trainerEmail = UniqueEmail();
        var clientEmail = UniqueEmail();

        var trainerToken = await RegisterAndLoginTrainer(client, trainerEmail);
        TestHelpers.SetBearerToken(client, trainerToken);

        var invitationToken = await CreatePendingInviteAndGetTokenAsync(client, clientEmail);

        // Expire the token in the database
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Application.Infrastructure.Data.ApplicationDbContext>();
            var token = db.InvitationTokens.First(t => t.Token == invitationToken);
            token.ExpiresAt = DateTime.UtcNow.AddDays(-1);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await TestHelpers.RegisterAsync(client, clientEmail, "TestPass1!", "Exp", "Client", "Client");
        var (clientAccessToken, _) = await TestHelpers.LoginAsync(client, clientEmail, "TestPass1!");

        TestHelpers.SetBearerToken(client, clientAccessToken);

        var acceptResponse = await client.PostAsJsonAsync("/auth/invite/accept", new
        {
            Token = invitationToken
        }, cancellationToken: TestContext.Current.CancellationToken);

        acceptResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AcceptInvitation_UsedToken_Returns400()
    {
        var client = factory.CreateClient();
        var trainerEmail = UniqueEmail();
        var clientEmail1 = UniqueEmail();
        var clientEmail2 = UniqueEmail();

        var trainerToken = await RegisterAndLoginTrainer(client, trainerEmail);
        TestHelpers.SetBearerToken(client, trainerToken);

        var invitationToken = await CreatePendingInviteAndGetTokenAsync(client, clientEmail1);

        // First client accepts
        await TestHelpers.RegisterAsync(client, clientEmail1, "TestPass1!", "Used", "Client", "Client");
        var (clientToken, _) = await TestHelpers.LoginAsync(client, clientEmail1, "TestPass1!");

        TestHelpers.SetBearerToken(client, clientToken);

        var firstAccept = await client.PostAsJsonAsync("/auth/invite/accept", new
        {
            Token = invitationToken
        }, cancellationToken: TestContext.Current.CancellationToken);

        firstAccept.StatusCode.Should().Be(HttpStatusCode.OK);

        // Second client tries to use same token
        await TestHelpers.RegisterAsync(client, clientEmail2, "TestPass1!", "Used2", "Client", "Client");
        var (clientToken2, _) = await TestHelpers.LoginAsync(client, clientEmail2, "TestPass1!");

        TestHelpers.SetBearerToken(client, clientToken2);

        var secondAccept = await client.PostAsJsonAsync("/auth/invite/accept", new
        {
            Token = invitationToken
        }, cancellationToken: TestContext.Current.CancellationToken);

        secondAccept.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetClientDashboard_AfterAccept_ReturnsClientInfo()
    {
        var client = factory.CreateClient();
        var trainerEmail = UniqueEmail();
        var clientEmail = UniqueEmail();

        var trainerToken = await RegisterAndLoginTrainer(client, trainerEmail);
        TestHelpers.SetBearerToken(client, trainerToken);

        var invitationToken = await CreatePendingInviteAndGetTokenAsync(client, clientEmail);

        await TestHelpers.RegisterAsync(client, clientEmail, "TestPass1!", "Dash", "Client", "Client");
        var (clientAccessToken, _) = await TestHelpers.LoginAsync(client, clientEmail, "TestPass1!");

        TestHelpers.SetBearerToken(client, clientAccessToken);
        await client.PostAsJsonAsync("/auth/invite/accept", new { Token = invitationToken }, cancellationToken: TestContext.Current.CancellationToken);

        // Trainer gets client list to find PublicId
        TestHelpers.SetBearerToken(client, trainerToken);
        var clientsResponse = await client.GetAsync("/trainer/clients", TestContext.Current.CancellationToken);
        var clientsBody = await clientsResponse.Content.ReadFromJsonAsync<ClientsResult>(cancellationToken: TestContext.Current.CancellationToken);
        var linkedClient = clientsBody!.Clients.First(c => c.Email == clientEmail);

        // Trainer gets dashboard
        var dashResponse = await client.GetAsync($"/trainer/clients/{linkedClient.PublicId}", TestContext.Current.CancellationToken);

        dashResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var dash = await dashResponse.Content.ReadFromJsonAsync<DashboardResult>(cancellationToken: TestContext.Current.CancellationToken);
        dash!.FirstName.Should().Be("Dash");
        dash.LastName.Should().Be("Client");
        dash.Email.Should().Be(clientEmail);
        dash.IsActive.Should().BeTrue();
        dash.TotalMeasurements.Should().Be(0);
        dash.TotalProgressPhotos.Should().Be(0);
    }

    [Fact]
    public async Task GetClientDashboard_UnlinkedClient_Returns404()
    {
        var client = factory.CreateClient();
        var trainerEmail = UniqueEmail();

        var trainerToken = await RegisterAndLoginTrainer(client, trainerEmail);
        TestHelpers.SetBearerToken(client, trainerToken);

        var response = await client.GetAsync($"/trainer/clients/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Registers a trainer, logs in, and creates a ProfessionalProfile in the database.
    /// Returns the access token.
    /// </summary>
    private async Task<string> RegisterAndLoginTrainer(HttpClient client, string email)
    {
        await TestHelpers.RegisterAsync(client, email, "TestPass1!", "Trainer", "User", "Trainer");
        var (accessToken, _) = await TestHelpers.LoginAsync(client, email, "TestPass1!");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Application.Infrastructure.Data.ApplicationDbContext>();
        var user = await db.Users.FirstAsync(u => u.Email == email);

        if (!await db.ProfessionalProfiles.AnyAsync(tp => tp.UserId == user.Id))
        {
            db.ProfessionalProfiles.Add(new Application.Domain.Entities.ProfessionalProfile
            {
                UserId = user.Id,
                Bio = "Test trainer",
                Specialization = "Testing"
            });
            await db.SaveChangesAsync();
        }

        return accessToken;
    }

    private record AcceptResult(string Message, Guid TrainerPublicId);
    private record ClientSummary(Guid PublicId, string Email, string FirstName, string LastName, bool IsActive);
    private record ClientsResult(List<ClientSummary> Clients, int TotalCount, int Page, int PageSize);
    private record DashboardResult(
        Guid ClientPublicId, string Email, string FirstName, string LastName,
        bool IsActive, int TotalMeasurements, int TotalProgressPhotos);
}
