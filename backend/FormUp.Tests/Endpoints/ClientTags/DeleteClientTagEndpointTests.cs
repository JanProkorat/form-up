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
/// Integration tests for <c>DELETE /trainer/client-tags/{TagId}</c>.
/// </summary>
[Collection(TestCollection.Shard4)]
public class DeleteClientTagEndpointTests(FitnessApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static string UniqueEmail(string tag) => $"{Guid.NewGuid():N}@delete-client-tag-{tag}.com";

    [Fact]
    public async Task Delete_ValidRequest_Returns204AndRemovesTag()
    {
        var http = await SetupTrainerAsync();
        var tagId = await CreateTagAsync(http, "VIP");

        var response = await http.DeleteAsync($"/trainer/client-tags/{tagId}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var persisted = await db.ClientTags.AsNoTracking()
            .FirstOrDefaultAsync(t => t.PublicId == tagId, TestContext.Current.CancellationToken);
        persisted.Should().BeNull();
    }

    [Fact]
    public async Task Delete_OtherProfessionalsTag_Returns404()
    {
        var owner = await SetupTrainerAsync();
        var tagId = await CreateTagAsync(owner, "VIP");

        var otherTrainer = await SetupTrainerAsync();

        var response = await otherTrainer.DeleteAsync(
            $"/trainer/client-tags/{tagId}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stillThere = await db.ClientTags.AsNoTracking()
            .FirstOrDefaultAsync(t => t.PublicId == tagId, TestContext.Current.CancellationToken);
        stillThere.Should().NotBeNull("a non-owning professional's delete must never affect the tag");
    }

    [Fact]
    public async Task Delete_NonexistentTag_Returns404()
    {
        var http = await SetupTrainerAsync();

        var response = await http.DeleteAsync(
            $"/trainer/client-tags/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_ConcurrentDuplicateDelete_NeverReturns500AndTagIsGone()
    {
        // Two timings are both valid: if both requests load before either commits, the loser's
        // DbUpdateConcurrencyException is caught and it also returns 204; if the second request
        // loads after the first commits, its owner-filtered query finds nothing and it returns 404.
        var http = await SetupTrainerAsync();
        var tagId = await CreateTagAsync(http, "VIP");

        var firstCall = http.DeleteAsync($"/trainer/client-tags/{tagId}", TestContext.Current.CancellationToken);
        var secondCall = http.DeleteAsync($"/trainer/client-tags/{tagId}", TestContext.Current.CancellationToken);

        var responses = await Task.WhenAll(firstCall, secondCall);

        responses.Should().OnlyContain(
            r => r.StatusCode == HttpStatusCode.NoContent || r.StatusCode == HttpStatusCode.NotFound);
        responses.Should().Contain(r => r.StatusCode == HttpStatusCode.NoContent);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var persisted = await db.ClientTags.AsNoTracking()
            .FirstOrDefaultAsync(t => t.PublicId == tagId, TestContext.Current.CancellationToken);
        persisted.Should().BeNull();
    }

    [Fact]
    public async Task Delete_AssignedTag_RemovesAssignmentToo()
    {
        var (http, professionalUserId) = await SetupTrainerInternalAsync();
        var tagId = await CreateTagAsync(http, "VIP");
        var clientPublicId = await SetupLinkedClientAsync(professionalUserId);

        var assignResponse = await http.PutAsJsonAsync($"/trainer/clients/{clientPublicId}/tags",
            new { TagIds = new[] { tagId } },
            TestContext.Current.CancellationToken);
        assignResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Resolve the link's own row id BEFORE deleting the tag — after deletion, a query that
        // joins assignments to ClientTags through the now-gone principal returns 0 whether the
        // cascade actually fired or the rows were simply orphaned. Filtering by
        // ClientProfessionalLinkId instead means the assertion can actually distinguish the two.
        long linkId;
        using (var setupScope = factory.Services.CreateScope())
        {
            var setupDb = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var clientProfile = await setupDb.ClientProfiles.AsNoTracking()
                .FirstAsync(cp => cp.PublicId == clientPublicId, TestContext.Current.CancellationToken);
            var link = await setupDb.ClientProfessionalLinks.AsNoTracking()
                .FirstAsync(l => l.ClientProfileId == clientProfile.Id, TestContext.Current.CancellationToken);
            linkId = link.Id;
        }

        var deleteResponse = await http.DeleteAsync(
            $"/trainer/client-tags/{tagId}", TestContext.Current.CancellationToken);
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var remainingAssignments = await db.ClientTagAssignments.AsNoTracking()
            .Where(a => a.ClientProfessionalLinkId == linkId)
            .CountAsync(TestContext.Current.CancellationToken);
        remainingAssignments.Should().Be(0, "ON DELETE CASCADE on client_tag_assignments.client_tag_id must remove the row");
    }

    private async Task<HttpClient> SetupTrainerAsync()
    {
        var (http, _) = await SetupTrainerInternalAsync();
        return http;
    }

    private async Task<(HttpClient Http, Guid UserId)> SetupTrainerInternalAsync()
    {
        var http = factory.CreateClient();
        var email = UniqueEmail("trainer");

        await TestHelpers.RegisterAsync(http, email, "TestPass1!", "Test", "Trainer", "Trainer");
        var (token, _) = await TestHelpers.LoginAsync(http, email, "TestPass1!");
        TestHelpers.SetBearerToken(http, token);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.AsNoTracking()
            .FirstAsync(u => u.Email == email, TestContext.Current.CancellationToken);

        return (http, user.Id);
    }

    private async Task<Guid> SetupLinkedClientAsync(Guid professionalUserId)
    {
        var clientUserId = await TestHelpers.RegisterLinkedClientAsync(
            factory, professionalUserId, TestContext.Current.CancellationToken);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var clientProfile = await db.ClientProfiles.AsNoTracking()
            .FirstAsync(cp => cp.UserId == clientUserId, TestContext.Current.CancellationToken);

        return clientProfile.PublicId;
    }

    private static async Task<Guid> CreateTagAsync(HttpClient http, string name)
    {
        var response = await http.PostAsJsonAsync("/trainer/client-tags",
            new { Name = name, Description = (string?)null, ColorHex = "#3b82f6" },
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
