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
using static FormUp.Tests.Endpoints.PaymentSettings.PaymentSettingsFixtures;

namespace FormUp.Tests.Endpoints.PaymentSettings;

/// <summary>
/// Integration tests for <c>GET /client/coaches/{CoachId}/bank-account</c>.
/// </summary>
[Collection(TestCollection.Name)]
public class GetCoachBankDetailsEndpointTests(FitnessApiFactory factory)
{
    private sealed record BankDetails(string Iban, string AccountHolder, int PaymentDueDays);

    private static Task<HttpResponseMessage> GetAsync(Actor client, Actor coach) =>
        client.Http.GetAsync($"/client/coaches/{coach.PublicId}/bank-account", TestContext.Current.CancellationToken);

    [Fact]
    public async Task Get_LinkGrantingNoPlanCapability_ReturnsTheBankDetails()
    {
        var coach = await TestActors.Trainer(factory).WithPayments().CreateAsync(TestContext.Current.CancellationToken);
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        await SetBankAccountAsync(factory, coach, ValidIban, "Martin Král");
        await TestActors.Link(factory, coach, client)
            .CanViewNutritionPlans(false)
            .CanViewTrainingPlans(false)
            .CreateAsync(TestContext.Current.CancellationToken);

        var response = await GetAsync(client, coach);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var details = await response.Content.ReadFromJsonAsync<BankDetails>(TestContext.Current.CancellationToken);
        details.Should().Be(new BankDetails(ValidIban, "Martin Král", 7));
    }

    [Theory]
    [InlineData("no link")]
    [InlineData("ended link")]
    [InlineData("no iban")]
    [InlineData("not entitled")]
    public async Task Get_NoLiveLinkNoIbanOrPaymentsLocked_Returns404(string scenario)
    {
        var coachBuilder = TestActors.Trainer(factory);
        var coach = await (scenario == "not entitled" ? coachBuilder : coachBuilder.WithPayments())
            .CreateAsync(TestContext.Current.CancellationToken);
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);

        if (scenario != "no iban")
        {
            await SetBankAccountAsync(factory, coach, ValidIban);
        }

        if (scenario == "ended link")
        {
            await TestActors.Link(factory, coach, client).Inactive().CreateAsync(TestContext.Current.CancellationToken);
        }
        else if (scenario != "no link")
        {
            await TestActors.Link(factory, coach, client).CreateAsync(TestContext.Current.CancellationToken);
        }

        if (scenario == "not entitled")
        {
            await SubscribeAsync(factory, coach, canUsePayments: false);
        }

        var response = await GetAsync(client, coach);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(ClientPaymentStatus.Requested)]
    [InlineData(ClientPaymentStatus.Upcoming)]
    public async Task Get_EndedLinkWithOpenPayment_ReturnsTheBankDetails(ClientPaymentStatus status)
    {
        var (coach, client) = await EndedLinkAsync();
        await SeedPaymentAsync(coach, client, status);

        var response = await GetAsync(client, coach);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(ClientPaymentStatus.Paid)]
    [InlineData(ClientPaymentStatus.Waived)]
    public async Task Get_EndedLinkWithOnlySettledPayments_Returns404(ClientPaymentStatus status)
    {
        var (coach, client) = await EndedLinkAsync();
        await SeedPaymentAsync(coach, client, status);

        var response = await GetAsync(client, coach);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_CoveredRoleRemovedWithOpenPayment_ReturnsTheBankDetails()
    {
        var (coach, client) = await RoleRemovedLinkAsync();
        await SeedPaymentAsync(coach, client, ClientPaymentStatus.Upcoming);

        var response = await GetAsync(client, coach);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Get_CoveredRoleRemovedWithOnlySettledPayments_Returns404()
    {
        var (coach, client) = await RoleRemovedLinkAsync();
        await SeedPaymentAsync(coach, client, ClientPaymentStatus.Paid);
        await SeedPaymentAsync(coach, client, ClientPaymentStatus.Waived);

        var response = await GetAsync(client, coach);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_OpenPaymentOwedToAnotherCoach_Returns404()
    {
        var (coach, client) = await EndedLinkAsync();
        var otherCoach = await TestActors.Trainer(factory).WithPayments().CreateAsync(TestContext.Current.CancellationToken);
        await SeedPaymentAsync(otherCoach, client, ClientPaymentStatus.Requested);

        var response = await GetAsync(client, coach);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_OpenPaymentOfAnotherClient_Returns404()
    {
        var (coach, client) = await EndedLinkAsync();
        var otherClient = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        await SeedPaymentAsync(coach, otherClient, ClientPaymentStatus.Requested);

        var response = await GetAsync(client, coach);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private async Task<(Actor Coach, Actor Client)> EndedLinkAsync()
    {
        var coach = await TestActors.Trainer(factory).WithPayments().CreateAsync(TestContext.Current.CancellationToken);
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        await SetBankAccountAsync(factory, coach, ValidIban);
        await TestActors.Link(factory, coach, client).Inactive().CreateAsync(TestContext.Current.CancellationToken);

        return (coach, client);
    }

    private async Task<(Actor Coach, Actor Client)> RoleRemovedLinkAsync()
    {
        var coach = await TestActors.Trainer(factory).WithPayments().CreateAsync(TestContext.Current.CancellationToken);
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        await SetBankAccountAsync(factory, coach, ValidIban);
        await TestActors.Link(factory, coach, client).CanViewNutritionPlans(false).CreateAsync(TestContext.Current.CancellationToken);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.ProfessionalProfiles.SingleAsync(p => p.Id == coach.ProfileId, TestContext.Current.CancellationToken))
            .TrainerRoleRemovedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return (coach, client);
    }

    private async Task SeedPaymentAsync(Actor coach, Actor client, ClientPaymentStatus status)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var assignment = await db.ClientPaymentAssignments.FirstOrDefaultAsync(
            a => a.ProfessionalProfileId == coach.ProfileId && a.ClientProfileId == client.ProfileId, ct);

        if (assignment is null)
        {
            assignment = new ClientPaymentAssignment
            {
                ProfessionalProfileId = coach.ProfileId,
                ClientProfileId = client.ProfileId,
                ClientNumber = 1
            };
            db.ClientPaymentAssignments.Add(assignment);
            await db.SaveChangesAsync(ct);
        }

        var dueDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-3);
        db.ClientPayments.Add(new ClientPayment
        {
            ClientPaymentAssignmentId = assignment.Id,
            ProfessionalProfileId = coach.ProfileId,
            Kind = ClientPaymentKind.OneOff,
            Status = status,
            DueDate = dueDate,
            Amount = 1000m,
            Title = "Seeded",
            VariableSymbol = $"{dueDate.Year}{dueDate.Month:D2}9{100 + (Interlocked.Increment(ref _seededSymbols) % 900)}",
            AccountIban = ValidIban,
            AccountHolder = "Martin Král",
            Message = "Seeded",
            RequestedAt = DateTime.UtcNow.AddDays(-10)
        });
        await db.SaveChangesAsync(ct);
    }

    private static int _seededSymbols;
}
