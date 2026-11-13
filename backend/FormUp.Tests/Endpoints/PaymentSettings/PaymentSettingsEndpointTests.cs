using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.PaymentSettings.Shared;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using static FormUp.Tests.Endpoints.PaymentSettings.PaymentSettingsFixtures;

namespace FormUp.Tests.Endpoints.PaymentSettings;

/// <summary>
/// Integration tests for <c>GET</c> and <c>PUT /payments/settings</c>.
/// </summary>
[Collection(TestCollection.Name)]
public class PaymentSettingsEndpointTests(FitnessApiFactory factory)
{
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task GetThenPut_NewCoach_StartsFromDefaultsAndStoresTheSettingsWithoutTouchingTheBankAccount()
    {
        var coach = await TestActors.Trainer(factory).WithPayments().CreateAsync(TestContext.Current.CancellationToken);

        var get = await coach.Http.GetAsync("/payments/settings", TestContext.Current.CancellationToken);

        get.StatusCode.Should().Be(HttpStatusCode.OK);
        var defaults = await get.Content.ReadFromJsonAsync<PaymentSettingsDto>(WireOptions, TestContext.Current.CancellationToken);
        defaults!.Iban.Should().BeNull();
        defaults.PaymentDueDays.Should().Be(7);
        defaults.MessageFormat.Should().Be(PaymentMessageFormat.PackageNameAndMonth);
        defaults.VariableSymbolFormat.Should().Be(VariableSymbolFormat.YearMonthClientNumber);

        await SetBankAccountAsync(factory, coach, ValidIban);

        var put = await coach.Http.PutAsJsonAsync(
            "/payments/settings",
            new { PaymentDueDays = 14, MessageFormat = "ClientNameAndMonth", VariableSymbolFormat = "YearMonthSequence" },
            TestContext.Current.CancellationToken);

        put.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = await ReadSettingsAsync(factory, coach);
        stored!.PaymentDueDays.Should().Be(14);
        stored.MessageFormat.Should().Be(PaymentMessageFormat.ClientNameAndMonth);
        stored.VariableSymbolFormat.Should().Be(VariableSymbolFormat.YearMonthSequence);
        stored.Iban.Should().Be(ValidIban);
    }

    [Fact]
    public async Task Put_ProfileCurrency_IsStoredAndKeptWhenOmitted()
    {
        var coach = await TestActors.Trainer(factory).WithPayments().CreateAsync(TestContext.Current.CancellationToken);
        var body = new { PaymentDueDays = 7, MessageFormat = "PackageName", VariableSymbolFormat = "YearMonthClientNumber" };

        (await coach.Http.PutAsJsonAsync(
            "/payments/settings", new { body.PaymentDueDays, body.MessageFormat, body.VariableSymbolFormat, ProfileCurrency = "EUR" },
            TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await coach.Http.PutAsJsonAsync("/payments/settings", body, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await ReadSettingsAsync(factory, coach))!.ProfileCurrency.Should().Be(PaymentCurrency.EUR);
    }

    [Fact]
    public async Task Put_DueDaysOutOfRange_Returns400AndStoresNothing()
    {
        var coach = await TestActors.Trainer(factory).WithPayments().CreateAsync(TestContext.Current.CancellationToken);

        var response = await coach.Http.PutAsJsonAsync(
            "/payments/settings",
            new { PaymentDueDays = 0, MessageFormat = "PackageName", VariableSymbolFormat = "YearMonthClientNumber" },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadSettingsAsync(factory, coach)).Should().BeNull();
    }
}
