using System.Net;
using System.Net.Http.Json;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;

namespace FormUp.Tests.Endpoints.Client.Collaborations;

/// <summary>
/// Integration tests for <c>GET /client/collaborations</c>.
/// </summary>
[Collection(TestCollection.Shard2)]
public class GetCollaborationsEndpointTests(FitnessApiFactory factory)
{
    private record CollaborationsResult(List<CollaborationItem> Collaborations);

    private record CollaborationItem(Guid ProfessionalPublicId, string Role, string? CoveredType);

    [Fact]
    public async Task GetAll_TrainingOnlyLink_ReturnsTrainingOnlyCoveredType()
    {
        var ct = TestContext.Current.CancellationToken;
        var trainer = await TestActors.Trainer(factory).CreateAsync(ct);
        var client = await TestActors.Client(factory).CreateAsync(ct);
        await TestActors.Link(factory, trainer, client)
            .CanViewNutritionPlans(false)
            .CanViewTrainingPlans()
            .CreateAsync(ct);

        var response = await client.Http.GetAsync("/client/collaborations", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<CollaborationsResult>(cancellationToken: ct);
        body!.Collaborations.Single(c => c.ProfessionalPublicId == trainer.PublicId)
            .CoveredType.Should().Be("TrainingOnly");
    }
}
