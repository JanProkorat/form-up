using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.ClientTags;

/// <summary>
/// Integration tests for <c>POST /trainer/client-tags</c>.
/// </summary>
[Collection(TestCollection.Shard3)]
public class CreateClientTagEndpointTests(FitnessApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static string UniqueEmail(string tag) => $"{Guid.NewGuid():N}@create-client-tag-{tag}.com";

    [Fact]
    public async Task Create_ValidRequest_Returns201AndPersistsTag()
    {
        var http = await SetupTrainerAsync();

        var response = await http.PostAsJsonAsync("/trainer/client-tags",
            new { Name = "VIP", Description = "High-value client", ColorHex = "#3B82F6" },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<ClientTagDto>(
            JsonOptions, TestContext.Current.CancellationToken);
        body!.Name.Should().Be("VIP");
        body.ColorHex.Should().Be("#3b82f6", "ColorHex is normalised to lowercase on write");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var persisted = await db.ClientTags.AsNoTracking().FirstOrDefaultAsync(
            t => t.PublicId == body.TagId, TestContext.Current.CancellationToken);
        persisted.Should().NotBeNull();
        persisted!.Description.Should().Be("High-value client");
    }

    [Fact]
    public async Task Create_DuplicateName_Returns409()
    {
        var http = await SetupTrainerAsync();

        var first = await http.PostAsJsonAsync("/trainer/client-tags",
            new { Name = "VIP", Description = (string?)null, ColorHex = "#3b82f6" },
            TestContext.Current.CancellationToken);
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await http.PostAsJsonAsync("/trainer/client-tags",
            new { Name = "VIP", Description = (string?)null, ColorHex = "#22c55e" },
            TestContext.Current.CancellationToken);

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Create_ConcurrentDuplicateName_NeverReturns500()
    {
        var http = await SetupTrainerAsync();
        var payload = new { Name = "VIP", Description = (string?)null, ColorHex = "#3b82f6" };

        var firstCall = http.PostAsJsonAsync("/trainer/client-tags", payload, TestContext.Current.CancellationToken);
        var secondCall = http.PostAsJsonAsync("/trainer/client-tags", payload, TestContext.Current.CancellationToken);

        var responses = await Task.WhenAll(firstCall, secondCall);

        responses.Should().OnlyContain(r =>
            r.StatusCode == HttpStatusCode.Created || r.StatusCode == HttpStatusCode.Conflict);
        responses.Should().ContainSingle(r => r.StatusCode == HttpStatusCode.Created);
        responses.Should().ContainSingle(r => r.StatusCode == HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Create_InvalidColorHex_Returns400()
    {
        var http = await SetupTrainerAsync();

        var response = await http.PostAsJsonAsync("/trainer/client-tags",
            new { Name = "VIP", Description = (string?)null, ColorHex = "blue" },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_ColorHexWithTrailingNewline_Returns400()
    {
        // .NET regex `$` matches at end of input OR immediately before a trailing '\n', unlike
        // JavaScript's hard end-of-string anchor. An 8-character "#3b82f6\n" would have passed a
        // `$`-anchored pattern and then hit ColorHex's [MaxLength(7)] column at save time,
        // surfacing as an uncaught DbUpdateException (500) instead of a validation 400.
        var http = await SetupTrainerAsync();

        var response = await http.PostAsJsonAsync("/trainer/client-tags",
            new { Name = "VIP", Description = (string?)null, ColorHex = "#3b82f6\n" },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_EmptyName_Returns400()
    {
        var http = await SetupTrainerAsync();

        var response = await http.PostAsJsonAsync("/trainer/client-tags",
            new { Name = "", Description = (string?)null, ColorHex = "#3b82f6" },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private async Task<HttpClient> SetupTrainerAsync()
    {
        var http = factory.CreateClient();
        var email = UniqueEmail("trainer");

        await TestHelpers.RegisterAsync(http, email, "TestPass1!", "Test", "Trainer", "Trainer");
        var (token, _) = await TestHelpers.LoginAsync(http, email, "TestPass1!");
        TestHelpers.SetBearerToken(http, token);

        return http;
    }

    private sealed class ClientTagDto
    {
        public Guid TagId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string ColorHex { get; set; } = string.Empty;
    }
}
