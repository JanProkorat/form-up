using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Constants;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;

namespace FormUp.Tests.Endpoints.ClientPhotos;

/// <summary>
/// A coach whose plan lacks progress photos cannot list a client's photos or work with photo requests; the client's
/// own photo endpoints are not touched by the coach's plan.
/// </summary>
[Collection(TestCollection.Name)]
public class ClientPhotosEntitlementTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(Actor Coach, Actor Client)> LinkedPairAsync(bool canUseProgressPhotos)
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).CreateAsync(Ct);
        await CoachSubscriptionBuilder.MoveToPlanAsync(
            factory, coach, plan => plan.CanUseProgressPhotos = canUseProgressPhotos);

        return (coach, client);
    }

    [Fact]
    public async Task CoachPhotoEndpoints_PlanWithoutProgressPhotos_Return403SubscriptionRequired()
    {
        var (coach, client) = await LinkedPairAsync(canUseProgressPhotos: false);

        var responses = new Dictionary<string, HttpResponseMessage>
        {
            ["GET /trainer/clients/{id}/photos"] = await coach.Http.GetAsync($"/trainer/clients/{client.PublicId}/photos", Ct),
            ["GET /trainer/photo-diary-requests"] = await coach.Http.GetAsync("/trainer/photo-diary-requests", Ct),
            ["POST /trainer/photo-diary-requests"] = await coach.Http.PostAsJsonAsync(
                "/trainer/photo-diary-requests", new { ClientId = client.PublicId, Mode = "Bulk" }, Ct),
            ["POST /trainer/photo-diary-requests/{id}/cancel"] = await coach.Http.PostAsJsonAsync(
                $"/trainer/photo-diary-requests/{Guid.NewGuid()}/cancel", new { }, Ct),
        };

        foreach (var (call, response) in responses)
        {
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, call);
            (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.SubscriptionRequired, call);
        }
    }

    [Fact]
    public async Task CoachPhotoEndpoints_PlanWithProgressPhotos_ReachTheEndpoint()
    {
        var (coach, client) = await LinkedPairAsync(canUseProgressPhotos: true);

        var photos = await coach.Http.GetAsync($"/trainer/clients/{client.PublicId}/photos", Ct);
        var requests = await coach.Http.GetAsync("/trainer/photo-diary-requests", Ct);

        photos.StatusCode.Should().Be(HttpStatusCode.OK);
        requests.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ClientPhotoEndpoints_CoachPlanWithoutProgressPhotos_StayOpen()
    {
        var (_, client) = await LinkedPairAsync(canUseProgressPhotos: false);

        var photos = await client.Http.GetAsync("/client/me/photos", Ct);
        var uploadUrl = await client.Http.PostAsJsonAsync(
            "/client/photos/upload-url", new { ContentType = "image/png", SizeBytes = 1000, Category = "Front" }, Ct);
        var requests = await client.Http.GetAsync("/client/photo-diary-requests", Ct);

        photos.StatusCode.Should().Be(HttpStatusCode.OK);
        uploadUrl.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
        requests.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
