using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Trainers;

/// <summary>
/// Integration tests for GET and PUT /trainer/profile against a real PostgreSQL instance.
/// </summary>
[Collection(TestCollection.Shard2)]
public class TrainerProfileIntegrationTests(FitnessApiFactory factory)
{
    private const string ObjectCertificates =
        """[{"title":"NASM CPT","issuer":"NASM","year":"2021"},"Legacy cert"]""";

    private record ProfileResponse(string? Bio, string? Certificates, string? AvatarBlobUrl);

    private record UploadUrlResponse(string UploadUrl, string BlobUrl);

    [Fact]
    public async Task GetProfile_WithConfirmedProfessionalAvatar_ReturnsAvatarBlobUrl()
    {
        var trainer = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);

        var uploadResp = await trainer.Http.PostAsJsonAsync(
            "/professionals/me/avatar/upload-url",
            new { ContentType = "image/jpeg", SizeBytes = 1024 },
            TestContext.Current.CancellationToken);
        uploadResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var upload = await uploadResp.Content.ReadFromJsonAsync<UploadUrlResponse>(
            cancellationToken: TestContext.Current.CancellationToken);

        var confirmResp = await trainer.Http.PutAsJsonAsync(
            "/professionals/me/avatar",
            new { BlobUrl = upload!.BlobUrl },
            TestContext.Current.CancellationToken);
        confirmResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var response = await trainer.Http.GetAsync("/trainer/profile", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ProfileResponse>(
            cancellationToken: TestContext.Current.CancellationToken);
        body!.AvatarBlobUrl.Should().Be(upload.BlobUrl);
    }

    [Fact]
    public async Task GetProfile_OnlyUserAvatarSet_ReturnsNullAvatarBlobUrl()
    {
        var trainer = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.FirstAsync(u => u.Id == trainer.UserId, TestContext.Current.CancellationToken);
            user.AvatarBlobUrl = "users/personal-avatar.jpg";
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var response = await trainer.Http.GetAsync("/trainer/profile", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ProfileResponse>(
            cancellationToken: TestContext.Current.CancellationToken);
        body!.AvatarBlobUrl.Should().BeNull();
    }

    [Fact]
    public async Task PutProfile_WithObjectCertificates_RoundTrips()
    {
        var trainer = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);

        var putResp = await trainer.Http.PutAsJsonAsync(
            "/trainer/profile",
            new { Bio = "Hello", Certificates = ObjectCertificates, ShowInSearch = true, AcceptNewClients = true },
            TestContext.Current.CancellationToken);
        putResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var getResp = await trainer.Http.GetAsync("/trainer/profile", TestContext.Current.CancellationToken);
        var body = await getResp.Content.ReadFromJsonAsync<ProfileResponse>(
            cancellationToken: TestContext.Current.CancellationToken);

        body!.Certificates.Should().Be(ObjectCertificates);
        body.Bio.Should().Be("Hello");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"title":"x"}""")]
    [InlineData("""[{"issuer":"no title"}]""")]
    [InlineData("""[{"title":""}]""")]
    [InlineData("[1,2]")]
    public async Task PutProfile_WithMalformedCertificates_Returns400(string certificates)
    {
        var trainer = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);

        var putResp = await trainer.Http.PutAsJsonAsync(
            "/trainer/profile",
            new { Certificates = certificates },
            TestContext.Current.CancellationToken);

        putResp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PutProfile_BioOverLimit_Returns400()
    {
        var trainer = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);

        var putResp = await trainer.Http.PutAsJsonAsync(
            "/trainer/profile",
            new { Bio = new string('a', 1001) },
            TestContext.Current.CancellationToken);

        putResp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PutProfile_InvalidCollaborationType_Returns400()
    {
        var trainer = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);

        var putResp = await trainer.Http.PutAsJsonAsync(
            "/trainer/profile",
            new { CollaborationType = "sometimes" },
            TestContext.Current.CancellationToken);

        putResp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetProfile_NoProfessionalProfile_Returns404()
    {
        var trainer = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.ProfessionalProfiles
                .Where(p => p.UserId == trainer.UserId)
                .ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        }

        var response = await trainer.Http.GetAsync("/trainer/profile", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetProfile_AsClientOnly_Returns403()
    {
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);

        var response = await client.Http.GetAsync("/trainer/profile", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
