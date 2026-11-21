using System.Net;
using FluentAssertions;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Services;

/// <summary>
/// The coach event recorder against the real database: it adds to the caller's context without saving, a repeated key writes
/// nothing, and a deleted coach's events stay without the link.
/// </summary>
[Collection(TestCollection.Shard2)]
public class CoachEventRecorderTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<int> CountByKeyAsync(string key)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.CoachEvents.CountAsync(e => e.IdempotencyKey == key, Ct);
    }

    [Fact]
    public async Task RecordAsync_AddsOneRowOnlyWhenTheCallerSaves()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var key = $"signup:{coach.UserId}";
        using var scope = factory.Services.CreateScope();
        var recorder = scope.ServiceProvider.GetRequiredService<ICoachEventRecorder>();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var added = await recorder.RecordAsync(CoachEventType.SignedUp, coach.UserId, key, Ct);

        added.Should().BeTrue();
        (await CountByKeyAsync(key)).Should().Be(0, "the recorder never saves on its own");

        await db.SaveChangesAsync(Ct);

        (await CountByKeyAsync(key)).Should().Be(1);
    }

    [Fact]
    public async Task RecordAsync_RepeatedKey_WritesNoSecondRow_PendingOrSaved()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var key = $"verify:{coach.UserId}";

        using (var scope = factory.Services.CreateScope())
        {
            var recorder = scope.ServiceProvider.GetRequiredService<ICoachEventRecorder>();

            (await recorder.RecordAsync(CoachEventType.EmailVerified, coach.UserId, key, Ct)).Should().BeTrue();
            (await recorder.RecordAsync(CoachEventType.EmailVerified, coach.UserId, key, Ct))
                .Should().BeFalse("the first one is still pending in the same context");
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().SaveChangesAsync(Ct);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var recorder = scope.ServiceProvider.GetRequiredService<ICoachEventRecorder>();

            (await recorder.RecordAsync(CoachEventType.EmailVerified, coach.UserId, key, Ct))
                .Should().BeFalse("the key is already saved");
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().SaveChangesAsync(Ct);
        }

        (await CountByKeyAsync(key)).Should().Be(1);
    }

    [Fact]
    public async Task DeleteAccount_CoachWithEvents_KeepsTheEventsWithANullCoachId()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var key = $"signup:{coach.UserId}";

        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ICoachEventRecorder>()
                .RecordAsync(CoachEventType.SignedUp, coach.UserId, key, Ct);
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().SaveChangesAsync(Ct);
        }

        var response = await coach.Http.DeleteAsync("/users/me", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));

        using var verifyScope = factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var kept = await db.CoachEvents.AsNoTracking().SingleAsync(e => e.IdempotencyKey == key, Ct);

        kept.CoachUserId.Should().BeNull("the link to the person goes, the funnel row stays");
        kept.EventType.Should().Be(CoachEventType.SignedUp);
    }
}
