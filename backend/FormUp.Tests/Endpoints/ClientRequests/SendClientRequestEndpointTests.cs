using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.ClientRequests;

/// <summary>
/// Integration test for <c>POST /client/requests</c>
/// (<see cref="Application.Features.ClientRequests.SendClientRequest.SendClientRequestEndpoint"/>),
/// covering the explicit <c>SaveChangesAsync</c> call added for #663 — the new
/// <c>ClientRequest</c> must persist even independent of
/// <c>NotificationService.CreateAsync</c>'s own downstream save on the same
/// scoped context. Uses <see cref="FitnessApiFactory"/> (Testcontainers-backed
/// PostgreSQL + MongoDB) because the endpoint's success path calls
/// <c>Send.CreatedAtAsync</c>, which requires a real <c>LinkGenerator</c> —
/// unavailable in a lightweight <c>Factory.Create&lt;T&gt;()</c> unit test.
/// </summary>
[Collection(TestCollection.Shard2)]
public class SendClientRequestEndpointTests(FitnessApiFactory factory)
{
    private static string UniqueEmail(string tag) => $"{Guid.NewGuid():N}@{tag}-client-request.com";

    [Fact]
    public async Task SendRequest_ValidProfessional_Returns201_AndPersistsClientRequest()
    {
        // Arrange — a Trainer accepting new clients, and a Client to send the request.
        var profHttp = factory.CreateClient();
        var profEmail = UniqueEmail("prof");
        await TestHelpers.RegisterAsync(profHttp, profEmail, "TestPass1!", "Tom", "Trainer", "Trainer");

        Guid professionalPublicId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var profUser = await db.Users.FirstAsync(
                u => u.Email == profEmail, TestContext.Current.CancellationToken);
            var profProfile = await db.ProfessionalProfiles.FirstAsync(
                p => p.UserId == profUser.Id, TestContext.Current.CancellationToken);
            professionalPublicId = profProfile.PublicId;
        }

        var clientHttp = factory.CreateClient();
        var clientEmail = UniqueEmail("client");
        await TestHelpers.RegisterAsync(clientHttp, clientEmail, "TestPass1!", "Petr", "Novak", "Client");
        var (clientToken, _) = await TestHelpers.LoginAsync(clientHttp, clientEmail, "TestPass1!");
        TestHelpers.SetBearerToken(clientHttp, clientToken);

        // Act
        var response = await clientHttp.PostAsJsonAsync(
            "/client/requests",
            new { ProfessionalPublicId = professionalPublicId, Message = "Hi, let's work together!" },
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var clientUser = await verifyDb.Users.FirstAsync(
            u => u.Email == clientEmail, TestContext.Current.CancellationToken);
        var clientProfile = await verifyDb.ClientProfiles.FirstAsync(
            cp => cp.UserId == clientUser.Id, TestContext.Current.CancellationToken);

        var persisted = await verifyDb.ClientRequests.FirstOrDefaultAsync(
            r => r.ClientProfileId == clientProfile.Id, TestContext.Current.CancellationToken);

        persisted.Should().NotBeNull(
            "the request must be durably persisted by the handler's own SaveChangesAsync call");
    }
}
