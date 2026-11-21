using FluentAssertions;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Services;

/// <summary>
/// The save the checkout, keep-free and apply-code endpoints share: a second identical request that loses the race on the
/// unique idempotency key must be answered normally, not with a 500.
/// </summary>
[Collection(TestCollection.Shard2)]
public class CoachEventSavesTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CoachEvent NewEvent(Guid coachUserId, string key) => new()
    {
        CoachUserId = coachUserId,
        EventType = CoachEventType.ChosePlan,
        OccurredAt = DateTimeOffset.UtcNow,
        IdempotencyKey = key,
    };

    [Fact]
    public async Task SaveIgnoringDuplicateAsync_KeyAlreadyStored_DoesNotThrow_AndKeepsOneRow()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var key = $"signup-free:{Guid.NewGuid()}";

        using (var winner = factory.Services.CreateScope())
        {
            var winnerDb = winner.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            winnerDb.CoachEvents.Add(NewEvent(coach.UserId, key));
            await winnerDb.SaveChangesAsync(Ct);
        }

        using var loser = factory.Services.CreateScope();
        var loserDb = loser.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        loserDb.CoachEvents.Add(NewEvent(coach.UserId, key));

        var save = () => CoachEventSaves.SaveIgnoringDuplicateAsync(loserDb, Ct);

        await save.Should().NotThrowAsync();
        using var check = factory.Services.CreateScope();
        var checkDb = check.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await checkDb.CoachEvents.CountAsync(e => e.IdempotencyKey == key, Ct)).Should().Be(1);
    }
}
