using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;

namespace FormUp.Tests.Endpoints.CoachRoleRemoval;

/// <summary>
/// Real-pipeline tests for the read-only gate on endpoints gated to both coach roles: a coach with no active
/// coach role is refused, a coach with one role left is not.
/// </summary>
[Collection(TestCollection.Shard3)]
public class NoActiveCoachRoleGateIntegrationTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<Actor> CoachWithRemovedRolesAsync(params UserRole[] removed) =>
        TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist)
            .WithRemovedCoachRoles(removed)
            .CreateAsync(Ct);

    private static async Task<HttpResponseMessage> SendAsync(Actor actor, string method, string path, object body)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(body) };
        return await actor.Http.SendAsync(request, Ct);
    }

    private static async Task AssertCoachRoleRemovedAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, raw);
        using var document = JsonDocument.Parse(raw);
        document.RootElement.GetProperty("errors")[0].GetProperty("code").GetString()
            .Should().Be(ErrorCodes.CoachRoleRemoved);
    }

    [Theory]
    [InlineData("POST", "/trainer/questionnaires")]
    [InlineData("POST", "/trainer/pending-invites")]
    public async Task NoActiveCoachRole_DualRoleWrite_Returns403CoachRoleRemoved(string method, string path)
    {
        var coach = await CoachWithRemovedRolesAsync(UserRole.Trainer, UserRole.Nutritionist);

        var response = await SendAsync(coach, method, path, new { Title = "Intake" });

        await AssertCoachRoleRemovedAsync(response);
    }

    [Theory]
    [InlineData("PUT", "/trainer/profile")]
    [InlineData("POST", "/users/me/roles")]
    public async Task NoActiveCoachRole_ExemptWrite_IsNotRefusedByTheGate(string method, string path)
    {
        var coach = await CoachWithRemovedRolesAsync(UserRole.Trainer, UserRole.Nutritionist);

        var response = await SendAsync(coach, method, path, new { Role = "Trainer" });

        (await response.Content.ReadAsStringAsync(Ct)).Should().NotContain(ErrorCodes.CoachRoleRemoved);
    }

    [Fact]
    public async Task NoActiveCoachRole_Read_StillReturns200()
    {
        var coach = await CoachWithRemovedRolesAsync(UserRole.Trainer, UserRole.Nutritionist);

        var response = await coach.Http.GetAsync("/trainer/questionnaires", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AddRoleBack_RestoresTheDualRoleWrite()
    {
        var coach = await CoachWithRemovedRolesAsync(UserRole.Trainer, UserRole.Nutritionist);
        await AssertCoachRoleRemovedAsync(await SendAsync(coach, "POST", "/trainer/questionnaires", new { Title = "Intake" }));

        var addBack = await SendAsync(coach, "POST", "/users/me/roles", new { Role = "Trainer" });
        addBack.IsSuccessStatusCode.Should().BeTrue(await addBack.Content.ReadAsStringAsync(Ct));

        var response = await SendAsync(coach, "POST", "/trainer/questionnaires", new { Title = "Intake" });

        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task OneCoachRoleRemoved_DualRoleWrite_StillSucceeds()
    {
        var coach = await CoachWithRemovedRolesAsync(UserRole.Trainer);

        var response = await SendAsync(coach, "POST", "/trainer/questionnaires", new { Title = "Intake" });

        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync(Ct));
    }
}
