using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Professionals;

/// <summary>
/// Integration tests for how GET /professionals/{publicId} reads the stored certificates JSON.
/// </summary>
[Collection(TestCollection.Shard3)]
public class GetPublicProfileCertificatesTests(FitnessApiFactory factory)
{
    private record PublicProfileResponse(List<string> Certificates);

    private async Task<List<string>> ReadCertificatesAsync(string? storedCertificates)
    {
        var trainer = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var profile = await db.ProfessionalProfiles.FirstAsync(
                p => p.UserId == trainer.UserId,
                TestContext.Current.CancellationToken);
            profile.Certificates = storedCertificates;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var response = await client.Http.GetAsync(
            $"/professionals/{trainer.PublicId}",
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PublicProfileResponse>(
            cancellationToken: TestContext.Current.CancellationToken);
        return body!.Certificates;
    }

    [Fact]
    public async Task GetPublicProfile_MixedStringAndObjectCertificates_ReturnsAllTitles()
    {
        var certificates = await ReadCertificatesAsync(
            """["Legacy cert",{"title":"NASM CPT","issuer":"NASM","year":"2021"},{"title":"Nutrition L2"}]""");

        certificates.Should().Equal("Legacy cert", "NASM CPT", "Nutrition L2");
    }

    [Fact]
    public async Task GetPublicProfile_LegacyStringCertificates_ReturnsStrings()
    {
        var certificates = await ReadCertificatesAsync("""["A","B"]""");

        certificates.Should().Equal("A", "B");
    }

    [Fact]
    public async Task GetPublicProfile_ObjectWithoutTitle_IsSkippedAndOthersKept()
    {
        var certificates = await ReadCertificatesAsync("""[{"issuer":"x"},"Kept"]""");

        certificates.Should().Equal("Kept");
    }

    [Fact]
    public async Task GetPublicProfile_MalformedCertificatesJson_ReturnsEmptyList()
    {
        var certificates = await ReadCertificatesAsync("not json");

        certificates.Should().BeEmpty();
    }
}
