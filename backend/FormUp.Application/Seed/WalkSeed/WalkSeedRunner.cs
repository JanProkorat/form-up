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
    private const string CheckInsOnlyPlanCode = "walk-checkins-only";
    private const string CheckInsOnlyPlanName = "Walk check-ins only";
    private const string CheckInsFullPlanCode = "walk-checkins-full";
    private const string CheckInsFullPlanName = "Walk check-ins full";

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

        var checkInsOnlyPrice = await EnsureSeedPlanAsync(
            db, CheckInsOnlyPlanCode, CheckInsOnlyPlanName, questionnaires: false, perClientCheckIns: false, ct);
        var checkInsFullPrice = await EnsureSeedPlanAsync(
            db, CheckInsFullPlanCode, CheckInsFullPlanName, questionnaires: true, perClientCheckIns: true, ct);

        var gateway = gatewayFactory();
        var prices = new PlanPrices(freePrice.Value, proPrice.Value, checkInsOnlyPrice, checkInsFullPrice);
        var credentials = new List<WalkAccountCredentials>();
        var clientCredentials = new List<WalkClientCredentials>();
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

                if (account.HasCheckInData)
                {
                    var (clients, passwords) = await PrepareCheckInClientsAsync(db, userManager, account, profile, ct);
                    await WalkCheckInData.SeedAsync(
                        db, user.Id, profile, clients, timeProvider.GetUtcNow().UtcDateTime, ct);
                    clientCredentials.AddRange(passwords);
                }
            }

            var refreshToken = await MintRefreshTokenAsync(db, configuration, user.Id, ct);
            credentials.Add(new WalkAccountCredentials(account, password, refreshToken));
            lines.Add($"{account.Key,-12} {account.Email,-40} {account.Note}");
        }

        foreach (var client in clientCredentials)
        {
            lines.Add($"{client.Client.Key,-12} {client.Client.Email,-40} client of {client.Client.CoachKey}: {client.Client.Note}");
        }

        await WalkSeedOutputWriter.WriteAsync(options, credentials, clientCredentials, ct);

        return new WalkSeedResult(true, null, lines);
    }

    private static async Task<(long PlanId, long PriceId, string? ExternalPriceId)?> FindOpenPriceAsync(
        ApplicationDbContext db, string planCode, bool requireExternalId, CancellationToken ct)
    {
        var price = await db.SubscriptionPlanPrices
            .AsNoTracking()
            .Where(p => p.SubscriptionPlan.Code == planCode
                        && p.ValidTo == null
                        && p.BillingInterval == BillingInterval.Monthly
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
        var target = account.Kind switch
        {
            WalkAccountKind.Free => prices.Free,
            WalkAccountKind.CheckInsOnly => prices.CheckInsOnly,
            WalkAccountKind.CheckIns => prices.CheckInsFull,
            _ => prices.Pro,
        };

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

        if (account.IsGivenByFormUp)
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
    /// Creates or resets a seed-owned plan of a check-in walk coach: weekly check-ins and automations on, forms and
    /// per-client check-in settings as asked. It is inactive, so it never shows up as a plan to buy, and has no Stripe price.
    /// </summary>
    private async Task<PlanPrice> EnsureSeedPlanAsync(
        ApplicationDbContext db, string code, string name, bool questionnaires, bool perClientCheckIns, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var plan = await db.SubscriptionPlans
            .Include(p => p.Prices)
            .FirstOrDefaultAsync(p => p.Code == code, ct);

        if (plan is null)
        {
            plan = new SubscriptionPlan
            {
                Code = code,
                NameCs = name,
                NameEn = name,
                NameDe = name,
                Currency = SupportedCurrencies.Czk,
            };
            db.SubscriptionPlans.Add(plan);
        }

        plan.BothRolesIncluded = true;
        plan.CanCreatePlans = true;
        plan.CanMessage = true;
        plan.CanSendQuestionnaires = questionnaires;
        plan.CanUseWeeklyCheckIns = true;
        plan.CanUsePerClientCheckInConfig = perClientCheckIns;
        plan.CanUsePayments = false;
        plan.CanUsePlanTemplates = true;
        plan.CanUseRecipes = true;
        plan.CanUseIngredients = true;
        plan.CanUseProgressPhotos = true;
        plan.CanUseWeightAndMeasurements = true;
        plan.CanUseAutomations = true;
        plan.CanUsePackages = false;
        plan.MaxActiveClients = null;
        plan.NameCs = name;
        plan.NameEn = name;
        plan.NameDe = name;
        plan.PriceMinorUnits = 0;
        plan.SecondRoleAddOnPriceMinorUnits = null;
        plan.IsActive = false;

        var price = plan.Prices.FirstOrDefault(p => p.ValidTo == null && p.BillingInterval == BillingInterval.Monthly);

        if (price is null)
        {
            price = new SubscriptionPlanPrice
            {
                SubscriptionPlanId = plan.Id,
                SubscriptionPlan = plan,
                BillingInterval = BillingInterval.Monthly,
                Currency = SupportedCurrencies.Czk,
                ValidFrom = now,
            };
            plan.Prices.Add(price);
        }

        price.PriceMinorUnits = 0;
        price.SecondRoleAddOnPriceMinorUnits = null;
        price.ExternalPriceId = null;

        await db.SaveChangesAsync(ct);

        return new PlanPrice(plan.Id, price.Id, null);
    }

    /// <summary>
    /// Creates or resets the coach's check-in clients: verified, Europe/Prague, a fresh password, linked for training and
    /// connected since long enough that the whole check-in history belongs to the collaboration.
    /// </summary>
    private async Task<(List<WalkCheckInClient> Clients, List<WalkClientCredentials> Passwords)> PrepareCheckInClientsAsync(
        ApplicationDbContext db,
        UserManager<ApplicationUser> userManager,
        WalkAccount coach,
        ProfessionalProfile coachProfile,
        CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var clients = new List<WalkCheckInClient>();
        var passwords = new List<WalkClientCredentials>();

        foreach (var account in WalkAccountCatalog.CheckInClients.Where(client => client.CoachKey == coach.Key))
        {
            var password = WalkSeedPasswordGenerator.Generate();
            var clientUser = await EnsureUserAsync(userManager, account.Email, account.Key, password);
            await SetRolesAsync(userManager, clientUser, UserRole.Client);

            clientUser.TimeZone = WalkCheckInData.ZoneId;
            ThrowIfFailed(await userManager.UpdateAsync(clientUser), $"update walk client {account.Email}");

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
            link.ActivatedAtUtc = now.AddDays(-7 * WalkCheckInData.LinkedWeeks);
            await db.SaveChangesAsync(ct);

            clients.Add(new WalkCheckInClient(account.Key, clientUser, clientProfile, link));
            passwords.Add(new WalkClientCredentials(account, password));
        }

        return (clients, passwords);
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

    private readonly record struct PlanPrices(PlanPrice Free, PlanPrice Pro, PlanPrice CheckInsOnly, PlanPrice CheckInsFull);
}
