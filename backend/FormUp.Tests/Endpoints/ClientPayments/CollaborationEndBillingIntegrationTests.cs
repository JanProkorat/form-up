using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.ClientPayments.GetClientPaymentSummary;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static FormUp.Tests.Endpoints.ClientPayments.ClientPaymentFixtures;

namespace FormUp.Tests.Endpoints.ClientPayments;

/// <summary>
/// Ending a collaboration ends the client's package and payments; a new or reactivated link bills only from a
/// package chosen in the invite, or one the coach sets later. The old terms never resume.
/// </summary>
[Collection(TestCollection.Shard4)]
public class CollaborationEndBillingIntegrationTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Moves the ended terms and their periods back in time, as if the collaboration ended months ago.</summary>
    private async Task AgeAsync(PaymentWorld world, int months)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var assignmentId = await db.ClientPaymentAssignments
            .Where(a => a.ClientProfileId == world.Client.ProfileId && a.ProfessionalProfileId == world.Coach.ProfileId)
            .Select(a => a.Id).SingleAsync(Ct);

        foreach (var terms in await db.ClientPaymentTerms.Where(t => t.ClientPaymentAssignmentId == assignmentId).ToListAsync(Ct))
        {
            terms.EffectiveFromUtc = PaymentTermsAging.ShiftMonths(terms.EffectiveFromUtc, -months);
            terms.EndsAtUtc = terms.EndsAtUtc is { } endsAt ? PaymentTermsAging.ShiftMonths(endsAt, -months) : null;
        }

        foreach (var period in await db.ClientPayments.Where(p => p.ClientPaymentAssignmentId == assignmentId).ToListAsync(Ct))
        {
            period.PeriodStart = period.PeriodStart!.Value.AddMonths(-months);
            period.PeriodEnd = period.PeriodEnd!.Value.AddMonths(-months);
            period.DueDate = period.DueDate.AddMonths(-months);
            period.VariableSymbol = null;
        }

        await db.SaveChangesAsync(Ct);
    }

    private async Task<List<DateOnly>> PeriodStartsAsync(PaymentWorld world)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.ClientPayments.AsNoTracking()
            .Where(p => p.ClientPaymentAssignment.ProfessionalProfileId == world.Coach.ProfileId
                && p.ClientPaymentAssignment.ClientProfileId == world.Client.ProfileId
                && p.PeriodStart != null)
            .OrderBy(p => p.PeriodStart)
            .Select(p => p.PeriodStart!.Value)
            .ToListAsync(Ct);
    }

    private async Task<List<ClientPaymentTerms>> TermsAsync(PaymentWorld world)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.ClientPaymentTerms.AsNoTracking()
            .Where(t => t.ClientPaymentAssignment.ProfessionalProfileId == world.Coach.ProfileId
                && t.ClientPaymentAssignment.ClientProfileId == world.Client.ProfileId)
            .OrderBy(t => t.EffectiveFromUtc)
            .ToListAsync(Ct);
    }

    private async Task<GetClientPaymentSummaryResponse> SummaryAsync(PaymentWorld world)
    {
        var response = await world.Coach.Http.GetAsync($"{world.Route}/summary", Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return await ReadAsync<GetClientPaymentSummaryResponse>(response);
    }

    [Fact]
    public async Task EndCollaboration_EndsTheTermsDropsUpcomingPeriodsAndKeepsHistory()
    {
        var world = await CreateWorldAsync(factory);
        await SetTermsOkAsync(world, await CreatePackageAsync(world.Coach));
        var requested = await SeedPastPaymentAsync(world, Today.AddDays(-5));

        await EndCollaborationAsync(world);

        (await TermsAsync(world)).Should().ContainSingle().Which.EndsAtUtc.Should().NotBeNull();
        (await PeriodStartsAsync(world)).Should().Equal(Today);
        (await ReadStoredAsync(factory, requested)).Status.Should().Be(ClientPaymentStatus.Requested);
        (await ReadAuditAsync(world)).Should().Contain(entry => entry.Action == "EndClientPaymentTerms");
    }

    [Fact]
    public async Task EndCollaboration_WhenTheLinkAlreadyEnded_Returns404AndChangesNothing()
    {
        var world = await CreateWorldAsync(factory);
        await SetTermsOkAsync(world, await CreatePackageAsync(world.Coach));
        var linkId = await LinkPublicIdAsync(world);
        await EndCollaborationAsync(world);
        var endedAt = (await TermsAsync(world)).Single().EndsAtUtc;

        var again = await world.Client.Http.DeleteAsync($"/client/collaborations/{linkId}", Ct);

        again.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await TermsAsync(world)).Single().EndsAtUtc.Should().Be(endedAt);
    }

    [Fact]
    public async Task Reinvite_WithTheSamePackageOnTheDayItEnded_ContinuesWithoutAClashingPeriod()
    {
        var world = await CreateWorldAsync(factory);
        var package = await CreatePackageAsync(world.Coach);
        await SetTermsOkAsync(world, package);
        var oldRow = (await ListAsync(world)).Single(row => row.PeriodStart == Today).PaymentId;
        await EndCollaborationAsync(world);

        await AcceptInviteAsync(world, package);

        var terms = await TermsAsync(world);
        terms.Should().HaveCount(2);
        terms[1].EndsAtUtc.Should().BeNull();
        terms[1].EffectiveFromUtc.Should().BeOnOrAfter(terms[0].EndsAtUtc!.Value);
        (await PeriodStartsAsync(world)).Should().Equal(Today, Today.AddMonths(1));
        var rows = await ListAsync(world);
        rows.Should().HaveCount(2);
        rows.Select(row => row.PaymentId).Should().NotContain(oldRow, "the ended collaboration's never-requested row for the day is replaced");
        rows.Should().OnlyContain(row => !row.IsEarlierCollaboration);
    }

    [Fact]
    public async Task Reinvite_WithoutAPackageAfterAGap_BillsNothingAndShowsNoPackage()
    {
        var world = await CreateWorldAsync(factory);
        await SetTermsOkAsync(world, await CreatePackageAsync(world.Coach));
        await EndCollaborationAsync(world);
        await AgeAsync(world, 3);

        await AcceptInviteAsync(world, null);

        (await SummaryAsync(world)).Terms.Should().BeNull();
        (await PeriodStartsAsync(world)).Should().Equal(Today.AddMonths(-3));
    }

    [Fact]
    public async Task AcceptClientRequest_ForAFormerClient_BillsNothingUntilTheCoachSetsAPackage()
    {
        var world = await CreateWorldAsync(factory);
        var package = await CreatePackageAsync(world.Coach);
        await SetTermsOkAsync(world, package);
        await EndCollaborationAsync(world);
        await AgeAsync(world, 3);
        Guid requestId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var request = new ClientRequest { ClientProfileId = world.Client.ProfileId, ProfessionalProfileId = world.Coach.ProfileId };
            db.ClientRequests.Add(request);
            await db.SaveChangesAsync(Ct);
            requestId = request.PublicId;
        }

        var accept = await world.Coach.Http.PostAsJsonAsync($"/trainer/client-requests/{requestId}/accept", new { }, Ct);
        accept.IsSuccessStatusCode.Should().BeTrue(await accept.Content.ReadAsStringAsync(Ct));

        (await ListAsync(world)).Should().ContainSingle();
        (await SummaryAsync(world)).Terms.Should().BeNull();

        await SetTermsOkAsync(world, package);

        (await ListAsync(world)).Should().HaveCount(3);
        (await PeriodStartsAsync(world)).Should().Equal(Today.AddMonths(-3), Today, Today.AddMonths(1));
    }
}
