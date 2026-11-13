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
using static FormUp.Tests.Endpoints.ClientPayments.ClientPaymentFixtures;
using static FormUp.Tests.Endpoints.PaymentSettings.PaymentSettingsFixtures;

namespace FormUp.Tests.Endpoints.Trainers.ClientRequests;

/// <summary>
/// Accepting a request that carries a package gives the client its terms, with the coach's optional discount.
/// </summary>
[Collection(TestCollection.Name)]
public class AcceptClientRequestPackageTermsTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Actor> CoachWithPaymentsAsync()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await SubscribeAsync(factory, coach, canUsePayments: true);

        return coach;
    }

    private static async Task<Guid> SendRequestAsync(Actor coach, Actor client, Guid? packageId)
    {
        var response = await client.Http.PostAsJsonAsync(
            "/client/requests", new { ProfessionalPublicId = coach.PublicId, PackagePublicId = packageId }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        return (await response.Content.ReadFromJsonAsync<RequestCreated>(Ct))!.PublicId;
    }

    private static Task<HttpResponseMessage> AcceptAsync(Actor coach, Guid requestId, Guid? discountId) =>
        coach.Http.PostAsJsonAsync($"/trainer/client-requests/{requestId}/accept", new { DiscountPublicId = discountId }, Ct);

    private async Task<List<ClientPaymentTerms>> TermsOfAsync(Actor coach, Actor client)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.ClientPaymentTerms
            .AsNoTracking()
            .Include(t => t.CoachPackage)
            .Where(t => t.ClientPaymentAssignment.ProfessionalProfileId == coach.ProfileId
                && t.ClientPaymentAssignment.ClientProfileId == client.ProfileId)
            .ToListAsync(Ct);
    }

    private async Task<ClientRequest> StoredRequestAsync(Guid requestId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.ClientRequests.AsNoTracking().SingleAsync(r => r.PublicId == requestId, Ct);
    }

    private async Task<bool> LinkActiveAsync(Actor coach, Actor client)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.ClientProfessionalLinks.AnyAsync(
            l => l.ClientProfileId == client.ProfileId && l.ProfessionalProfileId == coach.ProfileId && l.ConnectionState != ConnectionState.Ended, Ct);
    }

    private async Task<bool> SkipAuditedAsync(Guid requestId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.AuditLogs.AnyAsync(
            a => a.EntityId == requestId && a.EntityType == nameof(ClientRequest) && a.Action == "InvitePackageTermsSkipped", Ct);
    }

    private async Task AssertAcceptedWithoutTermsAsync(Actor coach, Actor client, Guid requestId, bool skipAudited)
    {
        (await LinkActiveAsync(coach, client)).Should().BeTrue();
        (await TermsOfAsync(coach, client)).Should().BeEmpty();
        (await StoredRequestAsync(requestId)).CoachDiscountId.Should().BeNull();
        (await SkipAuditedAsync(requestId)).Should().Be(skipAudited);
    }

    [Fact]
    public async Task Accept_WithUsablePackageAndCoveringDiscount_StoresTermsAndTheDiscount()
    {
        var coach = await CoachWithPaymentsAsync();
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var packageId = await CreatePackageAsync(coach, 2500m);
        var discountId = await CreateDiscountAsync(coach, 30m);
        var requestId = await SendRequestAsync(coach, client, packageId);

        var response = await AcceptAsync(coach, requestId, discountId);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var terms = (await TermsOfAsync(coach, client)).Should().ContainSingle().Subject;
        terms.CoachPackage.PublicId.Should().Be(packageId);
        terms.CoachDiscountId.Should().NotBeNull();
        (await StoredRequestAsync(requestId)).CoachDiscountId.Should().Be(terms.CoachDiscountId);
    }

    [Fact]
    public async Task Accept_WithUsablePackageAndNoDiscount_StoresTermsWithoutDiscount()
    {
        var coach = await CoachWithPaymentsAsync();
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var packageId = await CreatePackageAsync(coach);
        var requestId = await SendRequestAsync(coach, client, packageId);

        var response = await AcceptAsync(coach, requestId, null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await TermsOfAsync(coach, client)).Should().ContainSingle().Which.CoachDiscountId.Should().BeNull();
        (await StoredRequestAsync(requestId)).CoachDiscountId.Should().BeNull();
    }

    [Fact]
    public async Task Accept_PackageArchivedAfterTheRequest_AcceptsWithoutTermsAndAudits()
    {
        var coach = await CoachWithPaymentsAsync();
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var packageId = await CreatePackageAsync(coach);
        var discountId = await CreateDiscountAsync(coach);
        var requestId = await SendRequestAsync(coach, client, packageId);
        (await coach.Http.PostAsync($"/payments/packages/{packageId}/archive", Empty(), Ct))
            .IsSuccessStatusCode.Should().BeTrue();

        var response = await AcceptAsync(coach, requestId, discountId);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await AssertAcceptedWithoutTermsAsync(coach, client, requestId, skipAudited: true);
    }

    [Fact]
    public async Task Accept_WithDiscountOfAnotherCoach_AcceptsWithoutTermsAndAudits()
    {
        var coach = await CoachWithPaymentsAsync();
        var otherCoach = await CoachWithPaymentsAsync();
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var packageId = await CreatePackageAsync(coach);
        var foreignDiscountId = await CreateDiscountAsync(otherCoach);
        var requestId = await SendRequestAsync(coach, client, packageId);

        var response = await AcceptAsync(coach, requestId, foreignDiscountId);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await AssertAcceptedWithoutTermsAsync(coach, client, requestId, skipAudited: true);
    }

    [Fact]
    public async Task Accept_WithUnknownDiscount_AcceptsWithoutTermsAndAudits()
    {
        var coach = await CoachWithPaymentsAsync();
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var packageId = await CreatePackageAsync(coach);
        var requestId = await SendRequestAsync(coach, client, packageId);

        var response = await AcceptAsync(coach, requestId, Guid.NewGuid());

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await AssertAcceptedWithoutTermsAsync(coach, client, requestId, skipAudited: true);
    }

    [Fact]
    public async Task Accept_WithDiscountNotCoveringThePackage_AcceptsWithoutTermsAndAudits()
    {
        var coach = await CoachWithPaymentsAsync();
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var packageId = await CreatePackageAsync(coach);
        var otherPackageId = await CreatePackageAsync(coach, name: "Other");
        var discountId = await CreateDiscountAsync(coach, packageIds: [otherPackageId]);
        var requestId = await SendRequestAsync(coach, client, packageId);

        var response = await AcceptAsync(coach, requestId, discountId);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await AssertAcceptedWithoutTermsAsync(coach, client, requestId, skipAudited: true);
    }

    [Fact]
    public async Task Accept_WithDiscountOnRequestWithoutPackage_IgnoresItAndAcceptsWithoutTerms()
    {
        var coach = await CoachWithPaymentsAsync();
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var discountId = await CreateDiscountAsync(coach);
        var requestId = await SendRequestAsync(coach, client, null);

        var response = await AcceptAsync(coach, requestId, discountId);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await AssertAcceptedWithoutTermsAsync(coach, client, requestId, skipAudited: false);
    }

    private record RequestCreated(Guid PublicId);
}
