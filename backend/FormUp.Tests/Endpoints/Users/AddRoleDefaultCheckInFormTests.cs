using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Users;

/// <summary>
/// A coach whose professional profile is created by adding a role gets the default "Weekly check-in" form.
/// </summary>
[Collection(TestCollection.Shard3)]
public class AddRoleDefaultCheckInFormTests(FitnessApiFactory factory)
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task AddRole_ProfileCreatedByTheRole_GetsTheDefaultCheckInForm()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.ProfessionalProfiles.Where(p => p.UserId == coach.UserId).ExecuteDeleteAsync(Ct);
        }

        var response = await coach.Http.PostAsJsonAsync("/users/me/roles", new { Role = "Nutritionist" }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var forms = await verifyDb.Questionnaires
            .Where(q => q.ProfessionalId == coach.UserId && q.Type == QuestionnaireType.CheckIn)
            .ToListAsync(Ct);
        forms.Should().ContainSingle().Which.IsActive.Should().BeTrue();
    }
}
