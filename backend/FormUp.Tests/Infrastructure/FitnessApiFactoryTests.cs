using FluentAssertions;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Application.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FormUp.Tests.Infrastructure;

/// <summary>
/// Smoke tests for <see cref="FitnessApiFactory"/> configuration correctness.
/// </summary>
[Collection(TestCollection.Shard2)]
public class FitnessApiFactoryTests(FitnessApiFactory factory)
{
    /// <summary>
    /// Verifies that the <see cref="PhotoDiaryReminderScheduler"/> singleton is still
    /// resolvable from DI in the test host.
    /// Tests that drive the scheduler directly via TickAsync must be able to call
    /// factory.Services.GetRequiredService&lt;PhotoDiaryReminderScheduler&gt;().
    /// </summary>
    [Fact]
    public void PhotoDiaryReminderScheduler_IsStillResolvableAsSingleton()
    {
        var scheduler = factory.Services.GetRequiredService<PhotoDiaryReminderScheduler>();

        scheduler.Should().NotBeNull(
            "the singleton must remain resolvable so tests can drive TickAsync directly");
    }

    /// <summary>
    /// Root-cause regression test for #726: the three long-running, DB-touching
    /// background schedulers/reaper must never appear in the resolved
    /// <see cref="IHostedService"/> set for a Testcontainers-backed test host, or
    /// their zombie timers can tick against a disposed container after their own
    /// collection tears down (see <see cref="TestHostedServiceExtensions"/> for
    /// the full explanation).
    /// </summary>
    [Fact]
    public void BackgroundHostedServices_AreNotRegistered_InTestHost()
    {
        var hostedServiceTypes = factory.Services.GetServices<IHostedService>()
            .Select(s => s.GetType())
            .ToList();

        hostedServiceTypes.Should().NotContain(typeof(PhotoDiaryReminderScheduler),
            "the scheduler's BackgroundService loop must not auto-start in a test host");
        hostedServiceTypes.Should().NotContain(typeof(SocialLoginNonceReaperService),
            "the reaper's BackgroundService loop must not auto-start in a test host");
        hostedServiceTypes.Should().NotContain(typeof(AutomationScheduler),
            "the automation worker's loop must not auto-start in a test host");
    }

    /// <summary>
    /// The automation worker stays resolvable so tests can call its <c>RunDueNowAsync</c> seam.
    /// </summary>
    [Fact]
    public void AutomationScheduler_IsStillResolvableAsSingleton()
    {
        factory.Services.GetRequiredService<AutomationScheduler>().Should().NotBeNull(
            "tests run due jobs through RunDueNowAsync and never rely on ExecuteAsync");
    }

    /// <summary>
    /// <see cref="EmailDispatchWorker"/> must remain registered as an
    /// <see cref="IHostedService"/> in the test host. It is not part of the #726
    /// removal set (see <see cref="TestHostedServiceExtensions"/>): it never touches
    /// Postgres/Mongo, so it cannot hit the container-disposed cascade the removal
    /// set exists to prevent — and <c>AnonymousResendVerificationEndpointTests</c>
    /// depends on it actually running to drain the endpoint's fire-and-forget send.
    /// </summary>
    [Fact]
    public void EmailDispatchWorker_IsStillRegistered_InTestHost()
    {
        var hostedServiceTypes = factory.Services.GetServices<IHostedService>()
            .Select(s => s.GetType())
            .ToList();

        hostedServiceTypes.Should().Contain(typeof(EmailDispatchWorker),
            "the worker must keep draining its queue in the test host — AnonymousResendVerificationEndpointTests depends on it");
    }

    /// <summary>
    /// <see cref="MongoIndexInitializer"/> deliberately does NOT appear in the
    /// <see cref="IHostedService"/> set (post-#837 pass-2 review, fix for M1): it is
    /// registered as a plain <c>AddSingleton</c> in <c>Program.cs</c> and invoked
    /// explicitly, awaited, BEFORE <c>app.Run()</c> — never via <c>AddHostedService</c>.
    /// That wiring previously let Kestrel start accepting requests before (or
    /// concurrently with) the migration's completion, racing a
    /// <c>BsonSerializationException</c> on legacy documents. This test proves both
    /// halves of the fix: the plain-singleton registration exists (so the explicit
    /// call site in <c>Program.cs</c> can resolve it), and it is NOT also registered
    /// as an <see cref="IHostedService"/> (so there is exactly one code path that
    /// runs it, not two).
    /// </summary>
    [Fact]
    public void MongoIndexInitializer_IsPlainSingleton_NotHostedService()
    {
        var initializer = factory.Services.GetRequiredService<MongoIndexInitializer>();
        initializer.Should().NotBeNull(
            "it must be resolvable as a plain singleton — Program.cs's explicit pre-app.Run() call depends on this");

        var hostedServiceTypes = factory.Services.GetServices<IHostedService>()
            .Select(s => s.GetType())
            .ToList();

        hostedServiceTypes.Should().NotContain(typeof(MongoIndexInitializer),
            "it must run via the explicit pre-app.Run() call only, never additionally via the IHostedService pipeline");
    }
}
