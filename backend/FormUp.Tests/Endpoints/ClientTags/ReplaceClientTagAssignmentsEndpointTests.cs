using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FormUp.Application.Domain.Enums;

namespace FormUp.Tests.Endpoints.ClientTags;

/// <summary>
/// Integration tests for <c>PUT /trainer/clients/{ClientId}/tags</c>.
/// </summary>
[Collection(TestCollection.Name)]
public class ReplaceClientTagAssignmentsEndpointTests(FitnessApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static string UniqueEmail(string tag) => $"{Guid.NewGuid():N}@replace-client-tags-{tag}.com";

    [Fact]
    public async Task Replace_ValidTagIds_Returns200AndAssignsTags()
    {
        var (http, professionalUserId) = await SetupTrainerAsync();
        var tagId = await CreateTagAsync(http, "VIP");
        var clientPublicId = await SetupLinkedClientAsync(professionalUserId, true, true);

        var response = await http.PutAsJsonAsync($"/trainer/clients/{clientPublicId}/tags",
            new { TagIds = new[] { tagId } },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ReplaceResponseDto>(
            JsonOptions, TestContext.Current.CancellationToken);
        body!.Tags.Should().ContainSingle(t => t.TagId == tagId);
    }

    [Fact]
    public async Task Replace_ConcurrentIdenticalReplace_NeverReturns500AndNeverReportsRolledBackRemovals()
    {
        // Starts from a NON-empty assignment set ({Y}) so the removal half of the replace is
        // actually exercised by the race, not just the insert half — a set that starts empty
        // (as an earlier version of this test did) can never observe a removal that a failed
        // insert rolled back, because there is nothing to roll back. Both concurrent calls
        // replace {Y} with {X}: each removes Y and tries to insert X, and exactly one of the two
        // inserts collides on the unique index. The regression this guards is a response that
        // reports staged intent ("200 {tags:[X]}") while the actually-persisted row set still
        // contains Y because the insert's failure rolled back the same transaction's removal of
        // Y. Every response must report — and the database must actually hold — exactly {X}.
        var (http, professionalUserId) = await SetupTrainerAsync();
        var tagX = await CreateTagAsync(http, "X");
        var tagY = await CreateTagAsync(http, "Y");
        var clientPublicId = await SetupLinkedClientAsync(professionalUserId, true, true);

        var seed = await http.PutAsJsonAsync($"/trainer/clients/{clientPublicId}/tags",
            new { TagIds = new[] { tagY } },
            TestContext.Current.CancellationToken);
        seed.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = new { TagIds = new[] { tagX } };

        var firstCall = http.PutAsJsonAsync(
            $"/trainer/clients/{clientPublicId}/tags", payload, TestContext.Current.CancellationToken);
        var secondCall = http.PutAsJsonAsync(
            $"/trainer/clients/{clientPublicId}/tags", payload, TestContext.Current.CancellationToken);

        var responses = await Task.WhenAll(firstCall, secondCall);

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);

        foreach (var response in responses)
        {
            var body = await response.Content.ReadFromJsonAsync<ReplaceResponseDto>(
                JsonOptions, TestContext.Current.CancellationToken);
            body!.Tags.Select(t => t.TagId).Should().BeEquivalentTo([tagX],
                "the response must reflect persisted state, never a pre-collision snapshot");
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var clientProfile = await db.ClientProfiles.AsNoTracking()
            .FirstAsync(cp => cp.PublicId == clientPublicId, TestContext.Current.CancellationToken);
        var link = await db.ClientProfessionalLinks.AsNoTracking()
            .FirstAsync(l => l.ClientProfileId == clientProfile.Id, TestContext.Current.CancellationToken);
        var persistedTagPublicIds = await db.ClientTagAssignments.AsNoTracking()
            .Where(a => a.ClientProfessionalLinkId == link.Id)
            .Join(db.ClientTags, a => a.ClientTagId, t => t.Id, (a, t) => t.PublicId)
            .ToListAsync(TestContext.Current.CancellationToken);

        persistedTagPublicIds.Should().BeEquivalentTo([tagX],
            "Y's removal must not be rolled back by a losing concurrent insert of X");
    }

    [Fact]
    public async Task Replace_EmptyTagIds_ClearsAssignments()
    {
        var (http, professionalUserId) = await SetupTrainerAsync();
        var tagId = await CreateTagAsync(http, "VIP");
        var clientPublicId = await SetupLinkedClientAsync(professionalUserId, true, true);

        var assign = await http.PutAsJsonAsync($"/trainer/clients/{clientPublicId}/tags",
            new { TagIds = new[] { tagId } },
            TestContext.Current.CancellationToken);
        assign.StatusCode.Should().Be(HttpStatusCode.OK);

        var clear = await http.PutAsJsonAsync($"/trainer/clients/{clientPublicId}/tags",
            new { TagIds = Array.Empty<Guid>() },
            TestContext.Current.CancellationToken);

        clear.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await clear.Content.ReadFromJsonAsync<ReplaceResponseDto>(
            JsonOptions, TestContext.Current.CancellationToken);
        body!.Tags.Should().BeEmpty();
    }

    [Fact]
    public async Task Replace_UnknownTagId_Returns404NoPartialAssignment()
    {
        var (http, professionalUserId) = await SetupTrainerAsync();
        var realTagId = await CreateTagAsync(http, "VIP");
        var clientPublicId = await SetupLinkedClientAsync(professionalUserId, true, true);

        var response = await http.PutAsJsonAsync($"/trainer/clients/{clientPublicId}/tags",
            new { TagIds = new[] { realTagId, Guid.NewGuid() } },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var clientProfile = await db.ClientProfiles.AsNoTracking()
            .FirstAsync(cp => cp.PublicId == clientPublicId, TestContext.Current.CancellationToken);
        var link = await db.ClientProfessionalLinks.AsNoTracking()
            .FirstAsync(l => l.ClientProfileId == clientProfile.Id, TestContext.Current.CancellationToken);
        var assignmentCount = await db.ClientTagAssignments.AsNoTracking()
            .CountAsync(a => a.ClientProfessionalLinkId == link.Id, TestContext.Current.CancellationToken);
        assignmentCount.Should().Be(0, "a rejected request must not partially assign the valid tag");
    }

    [Fact]
    public async Task Replace_OtherProfessionalsTag_Returns404()
    {
        var (owner, _) = await SetupTrainerAsync();
        var tagId = await CreateTagAsync(owner, "VIP");

        var (otherTrainer, otherUserId) = await SetupTrainerAsync();
        var clientPublicId = await SetupLinkedClientAsync(otherUserId, true, true);

        var response = await otherTrainer.PutAsJsonAsync($"/trainer/clients/{clientPublicId}/tags",
            new { TagIds = new[] { tagId } },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Replace_ClientNotFound_Returns404()
    {
        var (http, _) = await SetupTrainerAsync();

        var response = await http.PutAsJsonAsync($"/trainer/clients/{Guid.NewGuid()}/tags",
            new { TagIds = Array.Empty<Guid>() },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Replace_NoActiveLink_Returns404()
    {
        var (http, _) = await SetupTrainerAsync();
        var (_, unrelatedUserId) = await SetupTrainerAsync();
        var clientPublicId = await SetupLinkedClientAsync(unrelatedUserId, true, true);

        var response = await http.PutAsJsonAsync($"/trainer/clients/{clientPublicId}/tags",
            new { TagIds = Array.Empty<Guid>() },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Replace_ArchivedLink_Returns404()
    {
        var (http, professionalUserId) = await SetupTrainerAsync();
        var clientPublicId = await SetupLinkedClientAsync(professionalUserId, true, true);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var clientProfile = await db.ClientProfiles
                .FirstAsync(cp => cp.PublicId == clientPublicId, TestContext.Current.CancellationToken);
            var link = await db.ClientProfessionalLinks
                .FirstAsync(l => l.ClientProfileId == clientProfile.Id, TestContext.Current.CancellationToken);
            link.ConnectionState = ConnectionState.Ended;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var response = await http.PutAsJsonAsync($"/trainer/clients/{clientPublicId}/tags",
            new { TagIds = Array.Empty<Guid>() },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Replace_LinkGrantsNoCapabilityFlags_StillSucceeds()
    {
        // Tagging is coach-private relationship metadata, not plan data — a live link is
        // enough, neither CanViewNutritionPlans nor CanViewTrainingPlans is required.
        var (http, professionalUserId) = await SetupTrainerAsync();
        var tagId = await CreateTagAsync(http, "VIP");
        var clientPublicId = await SetupLinkedClientAsync(
            professionalUserId, canViewNutritionPlans: false, canViewTrainingPlans: false);

        var response = await http.PutAsJsonAsync($"/trainer/clients/{clientPublicId}/tags",
            new { TagIds = new[] { tagId } },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<(HttpClient Http, Guid UserId)> SetupTrainerAsync()
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

    private async Task<Guid> SetupLinkedClientAsync(
        Guid professionalUserId, bool canViewNutritionPlans, bool canViewTrainingPlans)
    {
        var clientUserId = await TestHelpers.RegisterLinkedClientAsync(
            factory, professionalUserId, TestContext.Current.CancellationToken,
            canViewNutritionPlans, canViewTrainingPlans);

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

    private sealed class ReplaceResponseDto
    {
        public Guid ClientId { get; set; }
        public List<ClientTagDto> Tags { get; set; } = [];
    }
}
