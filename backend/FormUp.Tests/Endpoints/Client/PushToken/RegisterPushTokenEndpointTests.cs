using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Client.PushToken;

/// <summary>
/// <c>POST /client/push-token</c>: a token belongs to one device install, so registering it takes it over.
/// </summary>
[Collection(TestCollection.Shard2)]
public class RegisterPushTokenEndpointTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewToken() => $"ExponentPushToken[{Guid.NewGuid():N}]";

    private async Task SeedTokenAsync(Guid userId, string token)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.DevicePushTokens.Add(new DevicePushToken { UserId = userId, Token = token, Platform = "ios" });
        await db.SaveChangesAsync(Ct);
    }

    private async Task<List<Guid>> TokenOwnersAsync(string token)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.DevicePushTokens.Where(t => t.Token == token).Select(t => t.UserId).ToListAsync(Ct);
    }

    [Fact]
    public async Task Register_TokenHeldByAnotherUser_TakesItOver_AndKeepsTheOldUsersOtherTokens()
    {
        var previous = await TestActors.Client(factory).CreateAsync(Ct);
        var current = await TestActors.Client(factory).CreateAsync(Ct);
        var sharedToken = NewToken();
        var previousOtherToken = NewToken();
        await SeedTokenAsync(previous.UserId, sharedToken);
        await SeedTokenAsync(previous.UserId, previousOtherToken);

        var response = await current.Http.PostAsJsonAsync(
            "/client/push-token", new { token = sharedToken, platform = "ios" }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await TokenOwnersAsync(sharedToken)).Should().Equal(current.UserId);
        (await TokenOwnersAsync(previousOtherToken)).Should().Equal(previous.UserId);
    }

    [Fact]
    public async Task Register_SameTokenTwiceBySameUser_KeepsOneRow()
    {
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var token = NewToken();

        await client.Http.PostAsJsonAsync("/client/push-token", new { token, platform = "ios" }, Ct);
        var response = await client.Http.PostAsJsonAsync("/client/push-token", new { token, platform = "android" }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await TokenOwnersAsync(token)).Should().Equal(client.UserId);
    }
}
