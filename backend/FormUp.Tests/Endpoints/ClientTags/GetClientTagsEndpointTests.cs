using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FormUp.Tests.Infrastructure;
using FluentAssertions;

namespace FormUp.Tests.Endpoints.ClientTags;

/// <summary>
/// Integration tests for <c>GET /trainer/client-tags</c>.
/// </summary>
[Collection(TestCollection.Shard4)]
public class GetClientTagsEndpointTests(FitnessApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static string UniqueEmail(string tag) => $"{Guid.NewGuid():N}@client-tags-{tag}.com";

    [Fact]
    public async Task List_HappyPath_ReturnsOnlyOwnedTags()
    {
        var http = await SetupTrainerAsync();

        await http.PostAsJsonAsync("/trainer/client-tags",
            new { Name = "VIP", Description = (string?)null, ColorHex = "#3b82f6" },
            TestContext.Current.CancellationToken);
        await http.PostAsJsonAsync("/trainer/client-tags",
            new { Name = "New", Description = (string?)null, ColorHex = "#22c55e" },
            TestContext.Current.CancellationToken);

        var otherTrainerHttp = await SetupTrainerAsync();
        await otherTrainerHttp.PostAsJsonAsync("/trainer/client-tags",
            new { Name = "NotMine", Description = (string?)null, ColorHex = "#ef4444" },
            TestContext.Current.CancellationToken);

        var response = await http.GetAsync("/trainer/client-tags", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<GetClientTagsResponseDto>(
            JsonOptions, TestContext.Current.CancellationToken);
        body!.Tags.Select(t => t.Name).Should().BeEquivalentTo(["New", "VIP"]);
    }

    [Fact]
    public async Task List_NoTagsYet_ReturnsEmptyList()
    {
        var http = await SetupTrainerAsync();

        var response = await http.GetAsync("/trainer/client-tags", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<GetClientTagsResponseDto>(
            JsonOptions, TestContext.Current.CancellationToken);
        body!.Tags.Should().BeEmpty();
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

    private sealed class GetClientTagsResponseDto
    {
        public List<ClientTagDto> Tags { get; set; } = [];
    }

    private sealed class ClientTagDto
    {
        public Guid TagId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string ColorHex { get; set; } = string.Empty;
    }
}
