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
/// <c>POST /client/push-token/remove</c>: anonymous removal by token value, idempotent.
/// </summary>
[Collection(TestCollection.Shard4)]
public class RemovePushTokenAnonymousEndpointTests(FitnessApiFactory factory)
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
    public async Task Remove_KnownToken_Returns204_AndDeletesOnlyThatTokensRows()
    {
        var first = await TestActors.Client(factory).CreateAsync(Ct);
        var second = await TestActors.Client(factory).CreateAsync(Ct);
        var token = NewToken();
        var otherToken = NewToken();
        await SeedTokenAsync(first.UserId, token);
        await SeedTokenAsync(second.UserId, token);
        await SeedTokenAsync(first.UserId, otherToken);

        using var anonymous = factory.CreateClient();
        var response = await anonymous.PostAsJsonAsync("/client/push-token/remove", new { token }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await response.Content.ReadAsStringAsync(Ct)).Should().BeEmpty();
        (await TokenOwnersAsync(token)).Should().BeEmpty();
        (await TokenOwnersAsync(otherToken)).Should().Equal(first.UserId);
    }

    [Fact]
    public async Task Remove_UnknownToken_Returns204()
    {
        using var anonymous = factory.CreateClient();

        var response = await anonymous.PostAsJsonAsync("/client/push-token/remove", new { token = NewToken() }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await response.Content.ReadAsStringAsync(Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Remove_MalformedToken_Returns400()
    {
        using var anonymous = factory.CreateClient();

        var response = await anonymous.PostAsJsonAsync("/client/push-token/remove", new { token = "not-an-expo-token" }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
