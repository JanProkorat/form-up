using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.PhotoDiaryRequests;

/// <summary>
/// Integration tests for POST /trainer/photo-diary-requests/{Id}/cancel.
/// </summary>
[Collection(TestCollection.Name)]
public class CancelPhotoDiaryRequestEndpointTests(FitnessApiFactory factory)
{
    private async Task<(Actor Professional, long LinkId)> SetupCoachWithLinkAsync(UserRole role = UserRole.Nutritionist)
    {
        var professional = await TestActors.Professional(factory, role).CreateAsync(TestContext.Current.CancellationToken);
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        var linkId = await TestActors.Link(factory, professional, client)
            .AsRole(role)
            .CreateAsync(TestContext.Current.CancellationToken);
        return (professional, linkId);
    }

    private async Task<Guid> InsertRequestAsync(
        Guid professionalId,
        long linkId,
        PhotoDiaryStatus status = PhotoDiaryStatus.Pending,
        DateTimeOffset? createdAt = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var created = createdAt ?? DateTimeOffset.UtcNow;
        var accepted = status is PhotoDiaryStatus.Accepted or PhotoDiaryStatus.InProgress or PhotoDiaryStatus.Completed;
        var request = new PhotoDiaryRequest
        {
            Id = Guid.NewGuid(),
            ProfessionalId = professionalId,
            LinkId = linkId,
            Mode = PhotoDiaryMode.Workflow,
            DurationDays = 7,
            Status = status,
            AcceptedAt = accepted ? created : null,
            CompletedAt = status == PhotoDiaryStatus.Completed ? created : null,
            DismissReason = status == PhotoDiaryStatus.Dismissed ? "No thanks" : null,
            CreatedAt = created,
            UpdatedAt = created,
        };
        db.PhotoDiaryRequests.Add(request);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return request.Id;
    }

    private async Task<PhotoDiaryRequest> LoadAsync(Guid requestId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.PhotoDiaryRequests.AsNoTracking()
            .FirstAsync(r => r.Id == requestId, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(PhotoDiaryStatus.Pending)]
    [InlineData(PhotoDiaryStatus.Accepted)]
    [InlineData(PhotoDiaryStatus.InProgress)]
    public async Task Cancel_OpenRequest_Returns200_AndSetsCancelled(PhotoDiaryStatus openStatus)
    {
        var (coach, linkId) = await SetupCoachWithLinkAsync();
        var requestId = await InsertRequestAsync(coach.UserId, linkId, openStatus);

        var response = await coach.Http.PostAsJsonAsync(
            $"/trainer/photo-diary-requests/{requestId}/cancel", new { }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await LoadAsync(requestId)).Status.Should().Be(PhotoDiaryStatus.Cancelled);
    }

    [Fact]
    public async Task Cancel_AnotherCoachsRequest_Returns404_AndLeavesItOpen()
    {
        var (owner, linkId) = await SetupCoachWithLinkAsync();
        var (otherCoach, _) = await SetupCoachWithLinkAsync();
        var requestId = await InsertRequestAsync(owner.UserId, linkId);

        var response = await otherCoach.Http.PostAsJsonAsync(
            $"/trainer/photo-diary-requests/{requestId}/cancel", new { }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await LoadAsync(requestId)).Status.Should().Be(PhotoDiaryStatus.Pending);
    }

    [Fact]
    public async Task Cancel_UnknownId_Returns404()
    {
        var (coach, _) = await SetupCoachWithLinkAsync();

        var response = await coach.Http.PostAsJsonAsync(
            $"/trainer/photo-diary-requests/{Guid.NewGuid()}/cancel", new { }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(PhotoDiaryStatus.Completed)]
    [InlineData(PhotoDiaryStatus.Dismissed)]
    [InlineData(PhotoDiaryStatus.Cancelled)]
    public async Task Cancel_ClosedRequest_Returns409_InvalidStatus(PhotoDiaryStatus closedStatus)
    {
        var (coach, linkId) = await SetupCoachWithLinkAsync();
        var requestId = await InsertRequestAsync(coach.UserId, linkId, closedStatus);

        var response = await coach.Http.PostAsJsonAsync(
            $"/trainer/photo-diary-requests/{requestId}/cancel", new { }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain("PHOTO_DIARY_REQUEST_INVALID_STATUS");
        (await LoadAsync(requestId)).Status.Should().Be(closedStatus);
    }

    [Fact]
    public async Task Cancel_PendingPastEndDate_Returns409_InvalidStatus()
    {
        var (coach, linkId) = await SetupCoachWithLinkAsync();
        var requestId = await InsertRequestAsync(
            coach.UserId, linkId, createdAt: DateTimeOffset.UtcNow.AddDays(-8));

        var response = await coach.Http.PostAsJsonAsync(
            $"/trainer/photo-diary-requests/{requestId}/cancel", new { }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Cancel_AfterTheLinkEnded_StillWorksForTheCreator()
    {
        var (coach, linkId) = await SetupCoachWithLinkAsync();
        var requestId = await InsertRequestAsync(coach.UserId, linkId);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var link = await db.ClientProfessionalLinks.FirstAsync(
                l => l.Id == linkId, TestContext.Current.CancellationToken);
            link.ConnectionState = ConnectionState.Ended;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var response = await coach.Http.PostAsJsonAsync(
            $"/trainer/photo-diary-requests/{requestId}/cancel", new { }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await LoadAsync(requestId)).Status.Should().Be(PhotoDiaryStatus.Cancelled);
    }

    [Fact]
    public async Task Cancel_ThenCreateAgain_Succeeds()
    {
        var professional = await TestActors.Nutritionist(factory).CreateAsync(TestContext.Current.CancellationToken);
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        await TestActors.Link(factory, professional, client).AsRole(UserRole.Nutritionist)
            .CreateAsync(TestContext.Current.CancellationToken);

        var first = await professional.Http.PostAsJsonAsync(
            "/trainer/photo-diary-requests",
            new { ClientId = client.PublicId, Mode = "Workflow" },
            TestContext.Current.CancellationToken);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstId = (await first.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(
            TestContext.Current.CancellationToken)).GetProperty("id").GetGuid();

        var blocked = await professional.Http.PostAsJsonAsync(
            "/trainer/photo-diary-requests",
            new { ClientId = client.PublicId, Mode = "Workflow" },
            TestContext.Current.CancellationToken);
        blocked.StatusCode.Should().Be(HttpStatusCode.Conflict);

        (await professional.Http.PostAsJsonAsync(
            $"/trainer/photo-diary-requests/{firstId}/cancel", new { }, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await professional.Http.PostAsJsonAsync(
            "/trainer/photo-diary-requests",
            new { ClientId = client.PublicId, Mode = "Bulk" },
            TestContext.Current.CancellationToken);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
