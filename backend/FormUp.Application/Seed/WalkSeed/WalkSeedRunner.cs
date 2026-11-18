using System.Security.Cryptography;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Application.Seed.WalkSeed;

/// <summary>
/// Creates or resets the walk accounts on the local dev stack and writes the accounts file and the Playwright session
/// files, so a browser walk never registers, verifies or logs in through the UI. Re-running puts every account back in
/// its state.
/// </summary>
/// <param name="services">Root service provider; the runner opens its own scope.</param>
/// <param name="gatewayFactory">Builds the Stripe gateway once the guards have passed.</param>
/// <param name="environmentName">Host environment name.</param>
/// <param name="stripeSecretKey">Configured Stripe secret key; only its prefix is ever checked.</param>
/// <param name="options">Output locations and web origin.</param>
/// <param name="timeProvider">Clock.</param>
internal sealed class WalkSeedRunner(
    IServiceProvider services,
    Func<IWalkStripeGateway> gatewayFactory,
    string environmentName,
    string? stripeSecretKey,
    WalkSeedOptions options,
    TimeProvider timeProvider)
{
    private const string FreePlanCode = SubscriptionPlanCodes.Free;
    private const string ProPlanCode = "pro";
    private const string UserNamePrefix = "Walk";

    /// <summary>
    /// Runs the seed. Refuses, with nothing written, when a guard fails.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<WalkSeedResult> RunAsync(CancellationToken ct)
    {
        var refusal = WalkSeedGuard.Check(environmentName, stripeSecretKey);

        if (refusal is not null)
        {
            return WalkSeedResult.Refused(refusal);
        }

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        var freePrice = await FindOpenPriceAsync(db, FreePlanCode, requireExternalId: false, ct);
        var proPrice = await FindOpenPriceAsync(db, ProPlanCode, requireExternalId: true, ct);

        if (proPrice is null || freePrice is null)
        {
            return WalkSeedResult.Refused(
                "Refusing to run: the Pro plan has no open price with a Stripe price id (or the Free plan is missing). Price Pro in admin first.");
        }

        var gateway = gatewayFactory();
        var prices = new PlanPrices(freePrice.Value, proPrice.Value);
        var credentials = new List<WalkAccountCredentials>();
        var lines = new List<string>();

        foreach (var account in WalkAccountCatalog.All)
        {
            var password = WalkSeedPasswordGenerator.Generate();
            var user = await EnsureUserAsync(userManager, account.Email, account.Key, password);
            await SetRolesAsync(userManager, user, account.IsCoach ? UserRole.Trainer : UserRole.Admin);

            if (account.IsCoach)
            {
                var profile = await EnsureProfessionalProfileAsync(db, user.Id, ct);
                await PrepareSubscriptionAsync(db, account, profile, prices, gateway, ct);

                if (account.Kind == WalkAccountKind.ManyClients)
                {
                    await PrepareClientsAsync(db, userManager, profile, ct);
                }
            }

            var refreshToken = await MintRefreshTokenAsync(db, configuration, user.Id, ct);
            credentials.Add(new WalkAccountCredentials(account, password, refreshToken));
            lines.Add($"{account.Key,-12} {account.Email,-40} {account.Note}");
        }

        await WalkSeedOutputWriter.WriteAsync(options, credentials, ct);

        return new WalkSeedResult(true, null, lines);
    }

    private static async Task<(long PlanId, long PriceId, string? ExternalPriceId)?> FindOpenPriceAsync(
        ApplicationDbContext db, string planCode, bool requireExternalId, CancellationToken ct)
    {
        var price = await db.SubscriptionPlanPrices
            .AsNoTracking()
            .Where(p => p.SubscriptionPlan.Code == planCode
                        && p.ValidTo == null
                        && (!requireExternalId || p.ExternalPriceId != null))
            .OrderByDescending(p => p.Id)
            .Select(p => new { p.SubscriptionPlanId, p.Id, p.ExternalPriceId })
            .FirstOrDefaultAsync(ct);

        return price is null ? null : (price.SubscriptionPlanId, price.Id, price.ExternalPriceId);
    }

    private async Task<ApplicationUser> EnsureUserAsync(
        UserManager<ApplicationUser> userManager, string email, string lastName, string? password)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var user = await userManager.FindByEmailAsync(email);

        if (user is null)
        {
            user = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                IsActive = true,
                FirstName = UserNamePrefix,
                LastName = lastName,
                GdprConsent = true,
                GdprConsentDate = now,
            };

            var created = password is null
                ? await userManager.CreateAsync(user)
                : await userManager.CreateAsync(user, password);
            ThrowIfFailed(created, $"create walk user {email}");

            return user;
        }

        user.EmailConfirmed = true;
        user.IsActive = true;
        user.GdprConsent = true;
        user.FirstName = UserNamePrefix;
        user.LastName = lastName;
        ThrowIfFailed(await userManager.UpdateAsync(user), $"update walk user {email}");

        if (password is not null)
        {
            if (await userManager.HasPasswordAsync(user))
            {
                ThrowIfFailed(await userManager.RemovePasswordAsync(user), $"remove the old password of {email}");
            }

            ThrowIfFailed(await userManager.AddPasswordAsync(user, password), $"set the password of {email}");
        }

        return user;
    }

    private static async Task SetRolesAsync(UserManager<ApplicationUser> userManager, ApplicationUser user, UserRole role)
    {
        var wanted = role.ToString();
        var current = await userManager.GetRolesAsync(user);

        var surplus = current.Where(name => name != wanted).ToList();

        if (surplus.Count > 0)
        {
            ThrowIfFailed(await userManager.RemoveFromRolesAsync(user, surplus), $"remove roles from {user.Email}");
        }

        if (!current.Contains(wanted))
        {
            ThrowIfFailed(await userManager.AddToRoleAsync(user, wanted), $"add role {wanted} to {user.Email}");
        }
    }

    private async Task<ProfessionalProfile> EnsureProfessionalProfileAsync(
        ApplicationDbContext db, Guid userId, CancellationToken ct)
    {
        var profile = await db.ProfessionalProfiles.FirstOrDefaultAsync(p => p.UserId == userId, ct);

        if (profile is null)
        {
            profile = new ProfessionalProfile
            {
                UserId = userId,
                PublicId = Guid.NewGuid(),
                ShowInSearch = false,
                DateCreated = timeProvider.GetUtcNow().UtcDateTime,
            };
            db.ProfessionalProfiles.Add(profile);
        }

        // Undo what an Add role or Remove role step of a walk leaves behind.
        profile.AcceptNewClients = true;
        profile.TrainerRoleRemovedAt = null;
        profile.NutritionistRoleRemovedAt = null;
        profile.CoachAccountActiveUntil = null;

        await db.SaveChangesAsync(ct);
        await DefaultCheckInForm.EnsureAsync(db, userId, "en", ct);

        return profile;
    }

    /// <summary>
    /// Puts the coach's subscription row in the account's state. The order mirrors the admin give-plan-free flow: the row
    /// loses its provider ids and takes a fresh event time first, so a late deleted event of the previous subscription
    /// finds no row to rewrite; only then is the previous subscription retired and the new one created.
    /// </summary>
    private async Task PrepareSubscriptionAsync(
        ApplicationDbContext db,
        WalkAccount account,
        ProfessionalProfile profile,
        PlanPrices prices,
        IWalkStripeGateway gateway,
        CancellationToken ct)
    {
        var target = account.Kind == WalkAccountKind.Free ? prices.Free : prices.Pro;

        var row = await db.CoachSubscriptions.FirstOrDefaultAsync(cs => cs.ProfessionalProfileId == profile.Id, ct);

        if (row is null)
        {
            row = new CoachSubscription
            {
                ProfessionalProfileId = profile.Id,
                SubscriptionPlanId = target.PlanId,
                SubscriptionPlanPriceId = target.PriceId,
            };
            db.CoachSubscriptions.Add(row);
        }

        var previousCustomerId = row.ExternalCustomerId;
        var previousSubscriptionId = row.ExternalSubscriptionId;

        ResetSubscriptionFields(row, target);
        row.Status = SubscriptionStatus.Incomplete;
        row.LastExternalEventAt = TruncateToSecond(timeProvider.GetUtcNow());
        await db.SaveChangesAsync(ct);

        if (previousCustomerId is not null || previousSubscriptionId is not null)
        {
            await gateway.RetireAsync(previousCustomerId, previousSubscriptionId, ct);
        }

        var stripeSubscription = account.UsesStripe
            ? await gateway.CreatePaidSubscriptionAsync(
                account.Key,
                account.Email,
                target.ExternalPriceId!,
                requireThreeDSecureOnLaterPayments: account.Kind == WalkAccountKind.ProThreeDSecure,
                ct)
            : null;

        // A webhook may have touched the row while Stripe was called; take what is stored now.
        await db.Entry(row).ReloadAsync(ct);

        var now = timeProvider.GetUtcNow();
        ResetSubscriptionFields(row, target);
        row.Status = SubscriptionStatus.Active;

        if (account.Kind == WalkAccountKind.Given)
        {
            row.GivenFreeAt = now;
        }

        if (stripeSubscription is not null)
        {
            row.ExternalCustomerId = stripeSubscription.CustomerId;
            row.ExternalSubscriptionId = stripeSubscription.SubscriptionId;
            row.CurrentPeriodEndsAt = stripeSubscription.CurrentPeriodEndsAt;
            row.FirstPaidAt = now;
        }

        var eventAt = TruncateToSecond(now);
        row.LastExternalEventAt = row.LastExternalEventAt is { } stored && stored > eventAt ? stored : eventAt;

        await CoachLapseLinkReset.ResetAsync(db, profile.Id, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Overwrites every field a walk can change, so nothing of an earlier run or walk survives.
    /// </summary>
    private static void ResetSubscriptionFields(CoachSubscription row, PlanPrice target)
    {
        row.SubscriptionPlanId = target.PlanId;
        row.SubscriptionPlanPriceId = target.PriceId;
        row.PendingSubscriptionPlanPriceId = null;
        row.PaysSecondRoleAddOn = false;
        row.SecondRoleAddOnEndsAt = null;
        row.TrialEndsAt = null;
        row.FreeUntil = null;
        row.GivenFreeAt = null;
        row.CurrentPeriodEndsAt = null;
        row.CancelAtPeriodEnd = false;
        row.RenewalCancelledByAdminAt = null;
        row.ExternalCustomerId = null;
        row.ExternalSubscriptionId = null;
        row.NextPaymentAttemptAt = null;
        row.CurrentPromotionId = null;
        row.CurrentPromotionEndsAt = null;
        row.FirstPaidAt = null;
    }

    private async Task PrepareClientsAsync(
        ApplicationDbContext db, UserManager<ApplicationUser> userManager, ProfessionalProfile coachProfile, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var clientProfileIds = new List<long>();

        foreach (var number in Enumerable.Range(1, WalkAccountCatalog.ManyClientsClientCount))
        {
            var email = WalkAccountCatalog.ManyClientsClientEmail(number);
            var clientUser = await EnsureUserAsync(userManager, email, $"Client {number}", password: null);
            await SetRolesAsync(userManager, clientUser, UserRole.Client);

            var clientProfile = await db.ClientProfiles.FirstOrDefaultAsync(p => p.UserId == clientUser.Id, ct);

            if (clientProfile is null)
            {
                clientProfile = new ClientProfile
                {
                    UserId = clientUser.Id,
                    PublicId = Guid.NewGuid(),
                    DateCreated = now,
                };
                db.ClientProfiles.Add(clientProfile);
            }

            clientProfile.IsOnboardingComplete = true;
            await db.SaveChangesAsync(ct);
            clientProfileIds.Add(clientProfile.Id);

            var link = await db.ClientProfessionalLinks.FirstOrDefaultAsync(
                l => l.ProfessionalProfileId == coachProfile.Id && l.ClientProfileId == clientProfile.Id, ct);

            if (link is null)
            {
                link = new ClientProfessionalLink
                {
                    ProfessionalProfileId = coachProfile.Id,
                    ClientProfileId = clientProfile.Id,
                    PublicId = Guid.NewGuid(),
                    DateCreated = now,
                };
                db.ClientProfessionalLinks.Add(link);
            }

            link.ProfessionalRole = UserRole.Trainer;
            link.ConnectionState = ConnectionState.Connected;
            link.CanViewTrainingPlans = true;
            link.CanViewNutritionPlans = false;
            link.ActivatedAtUtc ??= now;
        }

        // Clients a walk linked on top of the seeded ones would break "exactly 3".
        var extraLinks = await db.ClientProfessionalLinks
            .Where(l => l.ProfessionalProfileId == coachProfile.Id
                        && !clientProfileIds.Contains(l.ClientProfileId)
                        && l.ConnectionState != ConnectionState.Ended)
            .ToListAsync(ct);

        foreach (var extraLink in extraLinks)
        {
            extraLink.ConnectionState = ConnectionState.Ended;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Revokes the user's earlier refresh tokens and inserts a fresh one, shaped like the one the login endpoint issues.
    /// </summary>
    private async Task<string> MintRefreshTokenAsync(
        ApplicationDbContext db, IConfiguration configuration, Guid userId, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        await db.RefreshTokens
            .Where(token => token.UserId == userId && token.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(token => token.RevokedAt, now), ct);

        var tokenValue = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = userId,
            Token = tokenValue,
            ExpiresAt = now.AddDays(configuration.GetValue(ConfigKeys.JwtRefreshTokenExpirationDays, 7)),
        });
        await db.SaveChangesAsync(ct);

        return tokenValue;
    }

    private static DateTimeOffset TruncateToSecond(DateTimeOffset instant) =>
        new(instant.Ticks - (instant.Ticks % TimeSpan.TicksPerSecond), instant.Offset);

    private static void ThrowIfFailed(IdentityResult result, string action)
    {
        if (!result.Succeeded)
        {
            var errors = string.Join("; ", result.Errors.Select(error => error.Code));
            throw new InvalidOperationException($"Failed to {action}: {errors}");
        }
    }

    private readonly record struct PlanPrice(long PlanId, long PriceId, string? ExternalPriceId)
    {
        public static implicit operator PlanPrice((long PlanId, long PriceId, string? ExternalPriceId) value) =>
            new(value.PlanId, value.PriceId, value.ExternalPriceId);
    }

    private readonly record struct PlanPrices(PlanPrice Free, PlanPrice Pro);
}
