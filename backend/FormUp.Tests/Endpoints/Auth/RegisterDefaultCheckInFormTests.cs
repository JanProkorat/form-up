using System.Net;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Auth;

/// <summary>
/// A newly registered coach starts with the default "Weekly check-in" form, in their language.
/// </summary>
[Collection(TestCollection.Shard4)]
public class RegisterDefaultCheckInFormTests(FitnessApiFactory factory)
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Register_Trainer_GetsTheDefaultCheckInFormOnceInTheirLanguage()
    {
        var email = $"{Guid.NewGuid():N}@default-form.test";
        using var http = factory.CreateClient();
        http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("cs-CZ");

        var response = await TestHelpers.RegisterAsync(http, email, "TestPass1!", "Jana", "Trenerova", "Trainer");

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userId = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync(Ct);

        var form = await db.Questionnaires.Include(q => q.Questions)
            .SingleAsync(q => q.ProfessionalId == userId && q.Type == QuestionnaireType.CheckIn, Ct);
        form.Title.Should().Be("Týdenní check-in");
        form.IsDefault.Should().BeFalse();
        form.Questions.OrderBy(q => q.OrderIndex).Select(q => q.Type).Should().Equal("smiley_rating", "short_text");

        await DefaultCheckInForm.EnsureAsync(db, userId, "en", Ct);

        (await db.Questionnaires.CountAsync(q => q.ProfessionalId == userId && q.Type == QuestionnaireType.CheckIn, Ct))
            .Should().Be(1, "seeding again changes nothing");
    }

    [Fact]
    public async Task Register_Client_GetsNoCheckInForm()
    {
        var email = $"{Guid.NewGuid():N}@default-form.test";
        using var http = factory.CreateClient();

        await TestHelpers.RegisterAsync(http, email, "TestPass1!", "Karel", "Klient", "Client");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userId = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync(Ct);
        (await db.Questionnaires.AnyAsync(q => q.ProfessionalId == userId, Ct)).Should().BeFalse();
    }
}
