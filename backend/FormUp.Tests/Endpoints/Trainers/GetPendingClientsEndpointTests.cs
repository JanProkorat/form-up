using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Trainers;

/// <summary>
/// Integration tests for <c>GET /trainer/clients/pending</c>.
/// </summary>
[Collection(TestCollection.Shard2)]
public class GetPendingClientsEndpointTests(FitnessApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static string UniqueEmail(string tag) => $"{Guid.NewGuid():N}@get-pending-clients-{tag}.com";

    [Fact]
    public async Task Get_MergesInvitesAndRequests_OrderedBySentAtDescending()
    {
        var (http, trainerId) = await SetupTrainerAsync();

        var invitePublicId = await SeedInviteAsync(trainerId, "invited@test.com", sentAt: DateTime.UtcNow.AddDays(-2));
        var requestPublicId = await SeedRequestAsync(trainerId, sentAt: DateTime.UtcNow.AddDays(-1));

        var response = await http.GetAsync("/trainer/clients/pending", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Deserialize(response);

        body!.Rows.Should().HaveCount(2);
        body.Rows[0].PublicId.Should().Be(requestPublicId, "the more recent row (the request) sorts first");
        body.Rows[0].Kind.Should().Be("Request");
        body.Rows[1].PublicId.Should().Be(invitePublicId);
        body.Rows[1].Kind.Should().Be("Invite");
    }

    [Fact]
    public async Task Get_AcceptedInviteAndNonPendingRequest_AreExcluded()
    {
        var (http, trainerId) = await SetupTrainerAsync();

        await SeedInviteAsync(trainerId, "accepted@test.com", sentAt: DateTime.UtcNow, isAccepted: true);
        await SeedRequestAsync(trainerId, sentAt: DateTime.UtcNow, status: ClientRequestStatus.Accepted);

        var response = await http.GetAsync("/trainer/clients/pending", TestContext.Current.CancellationToken);
        var body = await Deserialize(response);

        body!.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Get_ReinvitedAfterExpiry_ListsOnlyTheLiveInviteOnce()
    {
        var (http, trainerId) = await SetupTrainerAsync();
        var email = UniqueEmail("reinvited");

        await SeedInviteAsync(trainerId, email, sentAt: DateTime.UtcNow.AddDays(-10));
        var livePublicId = await SeedInviteAsync(trainerId, email, sentAt: DateTime.UtcNow.AddDays(-1));

        var response = await http.GetAsync("/trainer/clients/pending", TestContext.Current.CancellationToken);
        var body = await Deserialize(response);

        var row = body!.Rows.Should().ContainSingle().Subject;
        row.PublicId.Should().Be(livePublicId);
        row.Status.Should().Be("Pending");
    }

    [Fact]
    public async Task Get_LoneExpiredInvite_ListedOnceAsExpired_SoItStaysCancellable()
    {
        var (http, trainerId) = await SetupTrainerAsync();

        var expiredPublicId = await SeedInviteAsync(trainerId, UniqueEmail("lone"), sentAt: DateTime.UtcNow.AddDays(-10));

        var response = await http.GetAsync("/trainer/clients/pending", TestContext.Current.CancellationToken);
        var body = await Deserialize(response);

        var row = body!.Rows.Should().ContainSingle().Subject;
        row.PublicId.Should().Be(expiredPublicId);
        row.Status.Should().Be("Expired");
    }

    [Fact]
    public async Task Get_InviteWithStoredPackage_ReturnsThePackageOnTheRow()
    {
        var (http, trainerId) = await SetupTrainerAsync();
        var withPackage = await SeedInviteAsync(
            trainerId, UniqueEmail("with-package"), sentAt: DateTime.UtcNow.AddHours(-2), withPackage: true);
        var withoutPackage = await SeedInviteAsync(trainerId, UniqueEmail("no-package"), sentAt: DateTime.UtcNow.AddHours(-1));

        var response = await http.GetAsync("/trainer/clients/pending", TestContext.Current.CancellationToken);
        var body = await Deserialize(response);

        var packaged = body!.Rows.Single(r => r.PublicId == withPackage);
        packaged.Package.Should().NotBeNull();
        packaged.Package!.Name.Should().Be("Invite package");
        packaged.Package.Billing.Should().Be("Monthly");
        packaged.Package.Price.Should().Be(900m);
        packaged.Package.Currency.Should().Be("CZK");
        packaged.Package.PublicId.Should().NotBeEmpty();
        body.Rows.Single(r => r.PublicId == withoutPackage).Package.Should().BeNull();
    }

    [Fact]
    public async Task Get_NoProfessionalProfile_Returns404NotEmptyList()
    {
        // Deliberately NOT the 200-empty shape GetIncomingRequestsEndpoint uses for this case —
        // both tabs of one page must behave alike.
        var http = factory.CreateClient();
        var email = UniqueEmail("no-profile");
        await TestHelpers.RegisterAsync(http, email, "TestPass1!", "Test", "Trainer", "Trainer");
        var (token, _) = await TestHelpers.LoginAsync(http, email, "TestPass1!");
        TestHelpers.SetBearerToken(http, token);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.FirstAsync(u => u.Email == email, TestContext.Current.CancellationToken);
            var profile = await db.ProfessionalProfiles
                .FirstAsync(pp => pp.UserId == user.Id, TestContext.Current.CancellationToken);
            db.ProfessionalProfiles.Remove(profile);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var response = await http.GetAsync("/trainer/clients/pending", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PendingRoute_ResolvesToPendingEndpoint_NotToClientIdRouteParam()
    {
        // Literal segments outrank route parameters in FastEndpoints, so /trainer/clients/pending
        // must reach THIS endpoint rather than GET /trainer/clients/{clientId} (which would try
        // to parse "pending" as a client id and 404/400 in an unrelated way). Asserting the
        // response carries this endpoint's own "rows" shape (not a client-dashboard shape) proves
        // the route resolved correctly.
        var (http, _) = await SetupTrainerAsync();

        var response = await http.GetAsync("/trainer/clients/pending", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        json.Should().Contain("rows", "the pending endpoint's own response shape must be reached, not the client dashboard's");
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

    private async Task<Guid> SeedInviteAsync(
        Guid professionalUserId, string email, DateTime sentAt, bool isAccepted = false, bool withPackage = false)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var professionalProfile = await db.ProfessionalProfiles
            .FirstAsync(pp => pp.UserId == professionalUserId, TestContext.Current.CancellationToken);

        CoachPackage? package = null;

        if (withPackage)
        {
            package = new CoachPackage
            {
                PublicId = Guid.NewGuid(),
                ProfessionalProfileId = professionalProfile.Id,
                Name = "Invite package",
                Price = 900m,
                Currency = PaymentCurrency.CZK,
                Billing = PackageBillingInterval.Monthly,
            };
            db.CoachPackages.Add(package);
        }

        var invite = new PendingInvite
        {
            CoachPackage = package,
            PublicId = Guid.NewGuid(),
            ProfessionalProfileId = professionalProfile.Id,
            Email = email,
            SentAt = sentAt,
            Status = isAccepted ? PendingInviteStatus.Accepted : PendingInviteStatus.Pending
        };
        db.PendingInvites.Add(invite);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return invite.PublicId;
    }

    private async Task<Guid> SeedRequestAsync(
        Guid professionalUserId, DateTime sentAt, ClientRequestStatus status = ClientRequestStatus.Pending)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var professionalProfile = await db.ProfessionalProfiles
            .FirstAsync(pp => pp.UserId == professionalUserId, TestContext.Current.CancellationToken);

        var requesterEmail = UniqueEmail("requester");
        var requesterHttp = factory.CreateClient();
        await TestHelpers.RegisterAsync(requesterHttp, requesterEmail, "TestPass1!", "Requester", "Client", "Client");

        var requesterUser = await db.Users.FirstAsync(
            u => u.Email == requesterEmail, TestContext.Current.CancellationToken);
        var requesterProfile = await db.ClientProfiles.FirstAsync(
            cp => cp.UserId == requesterUser.Id, TestContext.Current.CancellationToken);

        var request = new ClientRequest
        {
            PublicId = Guid.NewGuid(),
            ClientProfileId = requesterProfile.Id,
            ProfessionalProfileId = professionalProfile.Id,
            Status = status,
            SentAt = sentAt
        };
        db.ClientRequests.Add(request);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return request.PublicId;
    }

    private static async Task<GetPendingClientsResponseDto?> Deserialize(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<GetPendingClientsResponseDto>(
            JsonOptions, TestContext.Current.CancellationToken);

    private sealed class GetPendingClientsResponseDto
    {
        public List<PendingClientRowDto> Rows { get; set; } = [];
    }

    private sealed class PendingClientRowDto
    {
        public string Kind { get; set; } = string.Empty;
        public Guid PublicId { get; set; }
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string? Message { get; set; }
        public DateTime SentAt { get; set; }
        public string Status { get; set; } = string.Empty;
        public PackageDto? Package { get; set; }
    }

    private sealed class PackageDto
    {
        public Guid PublicId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Billing { get; set; } = string.Empty;
        public decimal Price { get; set; }
        public string Currency { get; set; } = string.Empty;
    }
}
