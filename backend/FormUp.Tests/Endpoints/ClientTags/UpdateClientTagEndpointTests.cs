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
/// Integration tests for <c>PUT /trainer/client-tags/{TagId}</c>.
/// </summary>
[Collection(TestCollection.Shard4)]
public class UpdateClientTagEndpointTests(FitnessApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static string UniqueEmail(string tag) => $"{Guid.NewGuid():N}@update-client-tag-{tag}.com";

    [Fact]
    public async Task Update_ValidRequest_Returns200AndUpdatesFields()
    {
        var http = await SetupTrainerAsync();
        var tagId = await CreateTagAsync(http, "VIP", "#3b82f6");

        var response = await http.PutAsJsonAsync($"/trainer/client-tags/{tagId}",
            new { Name = "Renamed", Description = "Updated", ColorHex = "#EF4444" },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ClientTagDto>(
            JsonOptions, TestContext.Current.CancellationToken);
        body!.Name.Should().Be("Renamed");
        body.Description.Should().Be("Updated");
        body.ColorHex.Should().Be("#ef4444");
    }

    [Fact]
    public async Task Update_OtherProfessionalsTag_Returns404()
    {
        var owner = await SetupTrainerAsync();
        var tagId = await CreateTagAsync(owner, "VIP", "#3b82f6");

        var otherTrainer = await SetupTrainerAsync();

        var response = await otherTrainer.PutAsJsonAsync($"/trainer/client-tags/{tagId}",
            new { Name = "Hijacked", Description = (string?)null, ColorHex = "#ef4444" },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Update_NonexistentTag_Returns404()
    {
        var http = await SetupTrainerAsync();

        var response = await http.PutAsJsonAsync($"/trainer/client-tags/{Guid.NewGuid()}",
            new { Name = "Ghost", Description = (string?)null, ColorHex = "#3b82f6" },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Update_DuplicateName_Returns409()
    {
        var http = await SetupTrainerAsync();
        await CreateTagAsync(http, "VIP", "#3b82f6");
        var otherTagId = await CreateTagAsync(http, "New", "#22c55e");

        var response = await http.PutAsJsonAsync($"/trainer/client-tags/{otherTagId}",
            new { Name = "VIP", Description = (string?)null, ColorHex = "#22c55e" },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Update_SameTagSameName_Returns200()
    {
        var http = await SetupTrainerAsync();
        var tagId = await CreateTagAsync(http, "VIP", "#3b82f6");

        var response = await http.PutAsJsonAsync($"/trainer/client-tags/{tagId}",
            new { Name = "VIP", Description = "New description", ColorHex = "#3b82f6" },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Update_InvalidColorHex_Returns400()
    {
        var http = await SetupTrainerAsync();
        var tagId = await CreateTagAsync(http, "VIP", "#3b82f6");

        var response = await http.PutAsJsonAsync($"/trainer/client-tags/{tagId}",
            new { Name = "VIP", Description = (string?)null, ColorHex = "not-a-color" },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_TagDeletedConcurrently_Returns404()
    {
        // Forces the exact race DeleteClientTagEndpoint already guards against, but on the
        // UPDATE side: an explicit, uncommitted transaction deletes the tag and holds the row
        // lock, so the update request's own SELECT still observes the pre-commit row (proceeds
        // past the owner-filtered load) while its UPDATE blocks on the delete's lock. Committing
        // the delete then lets the blocked UPDATE resume against a now-missing row, reproducing
        // the 0-rows-affected DbUpdateConcurrencyException.
        //
        // The Postgres half of that is deterministic, but *reaching* the interleaving is not: the
        // delay below races the HTTP pipeline's time-to-SELECT. If the pipeline is ever slower,
        // the delete commits first and the ordinary pre-load guard returns the same 404 without
        // exercising the catch at all — so this test can pass green with the catch removed. It
        // cannot flake red, which is the safe direction, but it is timing-tolerant coverage, not
        // a timing-independent proof. See ProfessionSlotRaceTests for the pattern that asserts on
        // the blocking itself rather than on a sleep.
        var http = await SetupTrainerAsync();
        var tagId = await CreateTagAsync(http, "VIP", "#3b82f6");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);

        var tag = await db.ClientTags.FirstAsync(
            t => t.PublicId == tagId, TestContext.Current.CancellationToken);
        db.ClientTags.Remove(tag);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var updateTask = http.PutAsJsonAsync($"/trainer/client-tags/{tagId}",
            new { Name = "Renamed", Description = (string?)null, ColorHex = "#ef4444" },
            TestContext.Current.CancellationToken);

        // Gives the update request's SELECT time to complete (and observe the still-visible,
        // uncommitted-delete row) before the delete commits and releases the row lock the
        // update's UPDATE statement is waiting on.
        await Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);
        await transaction.CommitAsync(TestContext.Current.CancellationToken);

        var response = await updateTask;

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
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

    private static async Task<Guid> CreateTagAsync(HttpClient http, string name, string colorHex)
    {
        var response = await http.PostAsJsonAsync("/trainer/client-tags",
            new { Name = name, Description = (string?)null, ColorHex = colorHex },
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<ClientTagDto>(
            JsonOptions, TestContext.Current.CancellationToken);
        return body!.TagId;
    }

    private sealed class ClientTagDto
    {
        public Guid TagId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string ColorHex { get; set; } = string.Empty;
    }
}
