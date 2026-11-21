using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Auth;

/// <summary>
/// Integration tests for the anonymous, email-keyed resend-verification endpoint (#679).
/// Covers the AC's non-rate-limit cases: unregistered email, already-verified account,
/// a real unverified send, the lifetime-cap decoupling, and the rolling-window/cooldown
/// throttle (security follow-up) — all of which must return an IDENTICAL generic 200
/// response (no-enumeration contract).
///
/// The per-IP rate-limit case needs the full rate-limiter middleware active, which the
/// shared <see cref="FitnessApiFactory"/> disables for test isolation — that case lives
/// in <see cref="AnonymousResendVerificationRateLimitTests"/> below, backed by
/// <see cref="RateLimitEnabledFactory"/> instead.
/// </summary>
[Collection(TestCollection.Shard3)]
public class AnonymousResendVerificationEndpointTests(FitnessApiFactory factory)
{
    // Per-host singleton (#726 refinement) — resolved from this factory's own DI
    // container so assertions never see another factory's zombie worker traffic.
    private FakeEmailService EmailService => factory.Services.GetRequiredService<FakeEmailService>();

    private const string Route = "/auth/resend-verification/anonymous";
    private const string GenericMessage = "If an unverified account exists for this email, a verification email has been sent.";

    private static string UniqueEmail() => $"{Guid.NewGuid():N}@anon-resend-test.com";

    [Fact]
    public async Task UnregisteredEmail_ReturnsGenericSuccess_NoEmailSent()
    {
        var client = factory.CreateClient();
        var email = UniqueEmail();

        var response = await client.PostAsJsonAsync(Route, new { Email = email }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<GenericResult>(cancellationToken: TestContext.Current.CancellationToken);
        body!.Message.Should().Be(GenericMessage);

        EmailService.SentVerifications.Should().NotContain(v => v.Email == email,
            "an unregistered email must never trigger a send");
    }

    [Fact]
    public async Task AlreadyVerifiedEmail_ReturnsGenericSuccess_NoEmailSent()
    {
        var client = factory.CreateClient();
        var email = UniqueEmail();

        await TestHelpers.RegisterAsync(client, email, "TestPass1!", "Anon", "Verified", "Client");

        // Mark the freshly-registered account as verified directly in Postgres — no need
        // to drive the real token-verification flow just to reach this state.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.FirstAsync(u => u.Email == email, TestContext.Current.CancellationToken);
            user.EmailConfirmed = true;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var response = await client.PostAsJsonAsync(Route, new { Email = email }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<GenericResult>(cancellationToken: TestContext.Current.CancellationToken);
        body!.Message.Should().Be(GenericMessage);

        // Only the registration send exists for this email — the resend call itself
        // must not have added a second one.
        EmailService.SentVerifications.Where(v => v.Email == email).Should().ContainSingle(
            "registration sends the original email, but the resend call on an already-verified account must not send another");
    }

    [Fact]
    public async Task UnverifiedEmail_ReturnsGenericSuccess_AndSendsEmailWithValidToken()
    {
        var client = factory.CreateClient();
        var email = UniqueEmail();

        await TestHelpers.RegisterAsync(client, email, "TestPass1!", "Anon", "Unverified", "Client");

        var response = await client.PostAsJsonAsync(Route, new { Email = email }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<GenericResult>(cancellationToken: TestContext.Current.CancellationToken);
        body!.Message.Should().Be(GenericMessage);

        // The resend's SMTP send is now fire-and-forget (#702) -- it may not have landed
        // in SentVerifications yet by the time the HTTP response returns. Drain
        // deterministically (bounded poll, not a fixed sleep) before asserting on it.
        await FakeEmailService.WaitForAsync(() =>
            EmailService.SentVerifications.Count(v => v.Email == email) >= 2);

        // Registration sends one verification email; the resend call sends a second,
        // distinct one (prior unused tokens are invalidated by the shared token service).
        var sentForEmail = EmailService.SentVerifications.Where(v => v.Email == email).ToList();
        sentForEmail.Should().HaveCount(2, "registration sends one email, the resend call sends a second");

        var resendToken = sentForEmail[1].Token;

        // Prove the token is real and valid by actually verifying with it.
        var verifyResponse = await client.PostAsJsonAsync(
            "/auth/verify-email", new { Token = resendToken }, TestContext.Current.CancellationToken);
        verifyResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task LifetimeCapReached_DoesNotBlockAnonymousSend()
    {
        // Was ResendCapReached_ReturnsGenericSuccess_NoAdditionalEmailSent, asserting the
        // OPPOSITE of what this test now asserts: pre-fix, this endpoint gated on the
        // lifetime VerificationEmailsSent counter, so maxing it out suppressed the send.
        // That gate was the root cause of the anonymous quota-burn DoS (#679 security
        // follow-up): because the endpoint is anonymous, any caller could drive an
        // arbitrary user's counter to the cap and permanently lock them out of the
        // AUTHENTICATED resend path too. The fix replaces the lifetime-cap gate with the
        // rolling-window throttle (see WindowLimitReached... below) and decouples
        // anonymous sends from the counter entirely — so reaching the old cap must NOT
        // block an anonymous send anymore.
        var client = factory.CreateClient();
        var email = UniqueEmail();

        await TestHelpers.RegisterAsync(client, email, "TestPass1!", "Anon", "Capped", "Client");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.FirstAsync(u => u.Email == email, TestContext.Current.CancellationToken);
            user.VerificationEmailsSent = 4;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var sentBeforeResend = EmailService.SentVerifications.Count(v => v.Email == email);

        var response = await client.PostAsJsonAsync(Route, new { Email = email }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<GenericResult>(cancellationToken: TestContext.Current.CancellationToken);
        body!.Message.Should().Be(GenericMessage);

        // Fire-and-forget send (#702) -- drain deterministically before asserting.
        await FakeEmailService.WaitForAsync(() =>
            EmailService.SentVerifications.Count(v => v.Email == email) > sentBeforeResend);

        EmailService.SentVerifications.Count(v => v.Email == email).Should().Be(sentBeforeResend + 1,
            "the anonymous endpoint must not gate on the lifetime counter — only the rolling-window throttle applies here");
    }

    [Fact]
    public async Task WindowLimitReached_ReturnsGenericSuccess_NoAdditionalEmailSent()
    {
        var client = factory.CreateClient();
        var email = UniqueEmail();

        await TestHelpers.RegisterAsync(client, email, "TestPass1!", "Anon", "Windowed", "Client");

        Guid userId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.FirstAsync(u => u.Email == email, TestContext.Current.CancellationToken);
            userId = user.Id;

            // Seed 2 additional tokens so this user already has 3 EmailVerificationToken
            // rows within the last 24h (the registration token + these 2) — simulating 3
            // prior sends already inside the rolling window, regardless of which endpoint
            // originally issued them (the throttle counts all rows for the user, see
            // AnonymousResendVerificationEndpoint remarks).
            db.EmailVerificationTokens.AddRange(
                new EmailVerificationToken { UserId = userId, Token = Guid.NewGuid().ToString("N"), ExpiresAt = DateTime.UtcNow.AddHours(23) },
                new EmailVerificationToken { UserId = userId, Token = Guid.NewGuid().ToString("N"), ExpiresAt = DateTime.UtcNow.AddHours(23) });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var sentBeforeResend = EmailService.SentVerifications.Count(v => v.Email == email);

        // This would be the 4th send within the window — must be throttled.
        var response = await client.PostAsJsonAsync(Route, new { Email = email }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<GenericResult>(cancellationToken: TestContext.Current.CancellationToken);
        body!.Message.Should().Be(GenericMessage);

        EmailService.SentVerifications.Count(v => v.Email == email).Should().Be(sentBeforeResend,
            "the rolling-24h window cap (3 sends) must suppress the 4th send without changing the response");
    }

    [Fact]
    public async Task AnonymousSends_DoNotAdvanceLifetimeCap_AuthenticatedResendStillWorks()
    {
        var client = factory.CreateClient();
        var email = UniqueEmail();
        const string password = "TestPass1!";

        await TestHelpers.RegisterAsync(client, email, password, "Anon", "Decoupled", "Client");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.FirstAsync(u => u.Email == email, TestContext.Current.CancellationToken);
            user.VerificationEmailsSent.Should().Be(1, "registration itself counts toward the lifetime cap");
        }

        // Drive 2 anonymous sends (registration already holds 1 of the 3 window slots).
        for (var i = 0; i < 2; i++)
        {
            var response = await client.PostAsJsonAsync(Route, new { Email = email }, TestContext.Current.CancellationToken);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // Both anonymous sends are fire-and-forget (#702) -- drain deterministically
        // before asserting on the total count.
        await FakeEmailService.WaitForAsync(() =>
            EmailService.SentVerifications.Count(v => v.Email == email) >= 3);

        // Registration + 2 anonymous sends = 3 emails total, but the lifetime counter the
        // AUTHENTICATED endpoint gates on must still read 1 — proving the anonymous sends
        // never advanced it.
        EmailService.SentVerifications.Count(v => v.Email == email).Should().Be(3);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.FirstAsync(u => u.Email == email, TestContext.Current.CancellationToken);
            user.VerificationEmailsSent.Should().Be(1,
                "anonymous sends must never advance the lifetime counter the authenticated endpoint gates on");
        }

        // The authenticated path must still work: login (unverified accounts can still
        // log in) and call the AUTHENTICATED resend endpoint — it must succeed, proving
        // the anonymous traffic above did not lock it out.
        var (accessToken, _) = await TestHelpers.LoginAsync(client, email, password);
        TestHelpers.SetBearerToken(client, accessToken);

        var authResendResponse = await client.PostAsync("/auth/resend-verification", null, TestContext.Current.CancellationToken);
        authResendResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            "the authenticated resend must still be available after anonymous-triggered sends");
    }

    [Fact]
    public async Task FireAndForget_UnverifiedBranchEnqueuesOne_NoOpBranchesEnqueueNone_ResponseIdenticalAcrossAllFour()
    {
        // #702 coverage: the unverified branch must enqueue exactly one background send;
        // the three no-op branches (unregistered / already-verified / throttled) must
        // enqueue none; and the response body must be byte-identical across all four --
        // proving the enumeration defense survives the fire-and-forget refactor.
        var client = factory.CreateClient();

        // -- Unregistered (no-op): nothing is ever enqueued for this email, so absence
        // right after the response is already conclusive -- no wait needed.
        var unregisteredEmail = UniqueEmail();
        var unregisteredResponse = await client.PostAsJsonAsync(Route, new { Email = unregisteredEmail }, TestContext.Current.CancellationToken);
        EmailService.SentVerifications.Should().NotContain(v => v.Email == unregisteredEmail,
            "an unregistered email must never enqueue a background send");

        // -- Already-verified (no-op): only the registration send exists.
        var verifiedEmail = UniqueEmail();
        await TestHelpers.RegisterAsync(client, verifiedEmail, "TestPass1!", "Anon", "AllFourVerified", "Client");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.FirstAsync(u => u.Email == verifiedEmail, TestContext.Current.CancellationToken);
            user.EmailConfirmed = true;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var verifiedResponse = await client.PostAsJsonAsync(Route, new { Email = verifiedEmail }, TestContext.Current.CancellationToken);
        EmailService.SentVerifications.Where(v => v.Email == verifiedEmail).Should().ContainSingle(
            "an already-verified account must never enqueue a background send beyond the original registration email");

        // -- Throttled (no-op): window cap already exhausted.
        var throttledEmail = UniqueEmail();
        await TestHelpers.RegisterAsync(client, throttledEmail, "TestPass1!", "Anon", "AllFourThrottled", "Client");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.FirstAsync(u => u.Email == throttledEmail, TestContext.Current.CancellationToken);
            db.EmailVerificationTokens.AddRange(
                new EmailVerificationToken { UserId = user.Id, Token = Guid.NewGuid().ToString("N"), ExpiresAt = DateTime.UtcNow.AddHours(23) },
                new EmailVerificationToken { UserId = user.Id, Token = Guid.NewGuid().ToString("N"), ExpiresAt = DateTime.UtcNow.AddHours(23) });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var sentBeforeThrottled = EmailService.SentVerifications.Count(v => v.Email == throttledEmail);
        var throttledResponse = await client.PostAsJsonAsync(Route, new { Email = throttledEmail }, TestContext.Current.CancellationToken);
        EmailService.SentVerifications.Count(v => v.Email == throttledEmail).Should().Be(sentBeforeThrottled,
            "a throttled (window-cap) request must never enqueue a background send");

        // -- Real send (registered + unverified + under-throttle): exactly one enqueued
        // send, observed once the background worker drains it.
        var sendEmail = UniqueEmail();
        await TestHelpers.RegisterAsync(client, sendEmail, "TestPass1!", "Anon", "AllFourRealSend", "Client");
        var sentBeforeSend = EmailService.SentVerifications.Count(v => v.Email == sendEmail);
        var sendResponse = await client.PostAsJsonAsync(Route, new { Email = sendEmail }, TestContext.Current.CancellationToken);

        await FakeEmailService.WaitForAsync(() =>
            EmailService.SentVerifications.Count(v => v.Email == sendEmail) > sentBeforeSend);

        EmailService.SentVerifications.Count(v => v.Email == sendEmail).Should().Be(sentBeforeSend + 1,
            "the unverified branch must enqueue exactly one background send");

        // -- Response identity: all four requests get the SAME 200 with the SAME body.
        unregisteredResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        verifiedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        throttledResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        sendResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var unregisteredBody = await unregisteredResponse.Content.ReadFromJsonAsync<GenericResult>(cancellationToken: TestContext.Current.CancellationToken);
        var verifiedBody = await verifiedResponse.Content.ReadFromJsonAsync<GenericResult>(cancellationToken: TestContext.Current.CancellationToken);
        var throttledBody = await throttledResponse.Content.ReadFromJsonAsync<GenericResult>(cancellationToken: TestContext.Current.CancellationToken);
        var sendBody = await sendResponse.Content.ReadFromJsonAsync<GenericResult>(cancellationToken: TestContext.Current.CancellationToken);

        unregisteredBody!.Message.Should().Be(GenericMessage);
        verifiedBody!.Message.Should().Be(GenericMessage);
        throttledBody!.Message.Should().Be(GenericMessage);
        sendBody!.Message.Should().Be(GenericMessage);
    }

    [Fact]
    public async Task MalformedEmail_Returns400()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(Route, new { Email = "not-an-email" }, TestContext.Current.CancellationToken);

        // Well-formedness validation is allowed to differ from the no-enumeration
        // contract — it never distinguishes registered vs. unregistered emails.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private record GenericResult(string Message);
}

/// <summary>
/// Rate-limit AC for the anonymous resend-verification endpoint (#679). The endpoint runs
/// under <see cref="Application.Domain.Constants.AppPolicies.AuthRateLimit"/> — 10
/// requests / 15 min per IP, the same budget shared with /auth/register, /auth/login, etc.
/// Needs the full rate-limiter middleware active, so this uses
/// <see cref="RateLimitEnabledFactory"/> (defined in RateLimitPolicyTests.cs) rather than
/// the shared <see cref="FitnessApiFactory"/>, which disables rate limiting for isolation.
/// </summary>
public class AnonymousResendVerificationRateLimitTests(SharedTestContainers sharedContainers) : IAsyncLifetime
{
    private readonly RateLimitEnabledFactory _factory = new(sharedContainers);

    // Per-host singleton (#726 refinement) — resolved from this factory's own DI
    // container so assertions never see another factory's zombie worker traffic.
    private FakeEmailService EmailService => _factory.Services.GetRequiredService<FakeEmailService>();

    private const string Route = "/auth/resend-verification/anonymous";

    // A private IP not used by RateLimitPolicyTests' own buckets, in the 10.0.0.0/8
    // trusted range so partition-key injection via X-Test-Client-IP behaves the same way.
    private const string Ip = "10.0.4.1";

    public async ValueTask InitializeAsync() => await _factory.InitializeAsync();

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private HttpClient CreateClientWithIp(string ip)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Add(TestClientIpStartupFilter.Header, ip);
        return client;
    }

    [Fact]
    public async Task ExceedingPerIpBudget_ReturnsThrottledResponse_WithoutAnAdditionalEmail()
    {
        using var client = CreateClientWithIp(Ip);

        // SentVerifications is per-host (#726 refinement) -- this factory's instance is
        // isolated from every other factory's, but still scope every email to a
        // run-unique prefix so assertions below never see this HOST's own other traffic.
        var runId = Guid.NewGuid().ToString("N");
        string BudgetEmail(int i) => $"budget-{runId}-{i}@ratelimit-anon-test.com";
        var finalEmail = $"final-{runId}@ratelimit-anon-test.com";

        // Exhaust the shared 'auth' per-IP budget (PermitLimit = 10 per 15 min).
        for (var i = 0; i < 10; i++)
        {
            var response = await client.PostAsJsonAsync(
                Route, new { Email = BudgetEmail(i) }, TestContext.Current.CancellationToken);
            response.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests,
                $"attempt #{i + 1} should not be rate-limited yet (budget = 10)");
        }

        // The 11th call from the same IP must be blocked. The AC accepts a documented
        // 429 as the throttled response for this branch — the limiter runs before the
        // handler, so it never reaches the no-enumeration generic-200 code path at all.
        var rateLimitedResponse = await client.PostAsJsonAsync(
            Route, new { Email = finalEmail }, TestContext.Current.CancellationToken);

        rateLimitedResponse.StatusCode.Should().Be(HttpStatusCode.TooManyRequests,
            "the 11th call from the same IP must be rate-limited (budget = 10 per 15 min)");

        EmailService.SentVerifications.Should().NotContain(v => v.Email == finalEmail,
            "no email should be sent for the request that got rate-limited");
    }
}
