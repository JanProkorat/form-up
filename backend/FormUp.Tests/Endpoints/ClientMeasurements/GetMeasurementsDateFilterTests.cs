using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.ClientMeasurements;

/// <summary>
/// Integration tests (real PostgreSQL) for the from/to query-string filters on the measurement list endpoints.
/// Query binding yields DateTime values that Npgsql rejects against timestamptz unless normalised to UTC.
/// </summary>
[Collection(TestCollection.Name)]
public class GetMeasurementsDateFilterTests(FitnessApiFactory factory)
{
    private sealed record MeasurementBody(DateTime MeasuredAt);

    private sealed record MeasurementsBody(List<MeasurementBody>? Items);

    private async Task SeedMeasurementAsync(Actor client, DateTime measuredAtUtc)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.BodyMeasurements.Add(new BodyMeasurement
        {
            ClientProfileId = client.ProfileId,
            MeasuredAt = measuredAtUtc,
            WeightKg = 80m,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static string Query(DateTime fromUtc, DateTime toUtc) =>
        $"from={Uri.EscapeDataString(fromUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"))}" +
        $"&to={Uri.EscapeDataString(toUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"))}";

    private static async Task AssertOnlyInsideWindowAsync(HttpResponseMessage response, DateTime insideUtc)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<MeasurementsBody>(
            cancellationToken: TestContext.Current.CancellationToken);
        body!.Items!.Should().ContainSingle()
            .Which.MeasuredAt.ToUniversalTime().Should().BeCloseTo(insideUtc, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task GetClientMeasurements_FromAndToAsIsoUtc_Returns200AndFiltersByWindow()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        await TestActors.Link(factory, coach, client).CreateAsync(TestContext.Current.CancellationToken);
        var inside = new DateTime(2026, 10, 12, 23, 0, 0, DateTimeKind.Utc);
        await SeedMeasurementAsync(client, inside);
        await SeedMeasurementAsync(client, inside.AddDays(-10));

        var response = await coach.Http.GetAsync(
            $"/trainer/clients/{client.PublicId}/measurements?{Query(inside.AddHours(-2), inside.AddHours(2))}",
            TestContext.Current.CancellationToken);

        await AssertOnlyInsideWindowAsync(response, inside);
    }

    [Fact]
    public async Task GetMeasurements_FromAndToAsIsoUtc_Returns200AndFiltersByWindow()
    {
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        var inside = new DateTime(2026, 10, 12, 23, 0, 0, DateTimeKind.Utc);
        await SeedMeasurementAsync(client, inside);
        await SeedMeasurementAsync(client, inside.AddDays(-10));

        var response = await client.Http.GetAsync(
            $"/client/measurements?{Query(inside.AddHours(-2), inside.AddHours(2))}",
            TestContext.Current.CancellationToken);

        await AssertOnlyInsideWindowAsync(response, inside);
    }
}
