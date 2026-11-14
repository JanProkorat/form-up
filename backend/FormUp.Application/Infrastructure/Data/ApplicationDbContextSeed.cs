using System.Security.Cryptography;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Application.Infrastructure.Data;

/// <summary>
/// Seeds the database with initial data (roles, system admin user).
/// </summary>
public static class ApplicationDbContextSeed
{
    /// <summary>
    /// Applies pending migrations and seeds the default roles + system admin user.
    /// </summary>
    /// <param name="serviceProvider">The application service provider.</param>
    public static async Task SeedAsync(IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        await context.Database.MigrateAsync();

        foreach (var role in Enum.GetValues<UserRole>())
        {
            var roleName = role.ToString();
            if (!await roleManager.RoleExistsAsync(roleName))
            {
                await roleManager.CreateAsync(new ApplicationRole
                {
                    Name = roleName,
                    Description = $"{roleName} role"
                });
            }
        }

        await EnsureSystemAdminAsync(userManager);
        await EnsureSubscriptionPlansAsync(context);
    }

    /// <summary>
    /// Ensures the non-loginable system admin account (<see cref="SystemUsers.AdminId"/>) exists
    /// and is a member of the <see cref="UserRole.Admin"/> role. This account owns seeded catalog
    /// content (recipes, workout templates) where an owner is structurally required — it is never
    /// meant to authenticate: the password is cryptographically random and is never logged or
    /// otherwise revealed. Idempotent — safe to call on every startup.
    /// </summary>
    private static async Task EnsureSystemAdminAsync(UserManager<ApplicationUser> userManager)
    {
        var admin = await userManager.FindByIdAsync(SystemUsers.AdminId.ToString());

        if (admin is null)
        {
            admin = new ApplicationUser
            {
                Id = SystemUsers.AdminId,
                UserName = SystemUsers.AdminEmail,
                Email = SystemUsers.AdminEmail,
                EmailConfirmed = true,
                IsActive = true,
                FirstName = "Form Up",
                LastName = "System",
                GdprConsent = true,
            };

            var createResult = await userManager.CreateAsync(admin, GenerateRandomPassword());
            if (!createResult.Succeeded)
            {
                var errors = string.Join("; ", createResult.Errors.Select(e => $"{e.Code}: {e.Description}"));
                throw new InvalidOperationException($"Failed to create system admin user: {errors}");
            }
        }

        if (!await userManager.IsInRoleAsync(admin, UserRole.Admin.ToString()))
        {
            var roleResult = await userManager.AddToRoleAsync(admin, UserRole.Admin.ToString());
            if (!roleResult.Succeeded)
            {
                var errors = string.Join("; ", roleResult.Errors.Select(e => $"{e.Code}: {e.Description}"));
                throw new InvalidOperationException($"Failed to assign Admin role to system user: {errors}");
            }
        }
    }

    /// <summary>
    /// Seeds the placeholder subscription tiers (Free/Start/Pro/Studio), each with its one open
    /// price row, so the Admin management UI has rows to display on first boot. Insert-if-missing
    /// by <c>Code</c> — a plan that already exists is left untouched, so Admin edits made through
    /// the CRUD endpoints are never clobbered by a later boot re-running this seeder. Prices and
    /// caps are placeholder values, editable through the Admin UI.
    /// </summary>
    private static async Task EnsureSubscriptionPlansAsync(ApplicationDbContext context)
    {
        var existingCodes = await context.SubscriptionPlans
            .Select(p => p.Code)
            .ToListAsync();

        var placeholderPlans = new[]
        {
            NewSeedPlan(
                SubscriptionPlanCodes.Free, "Zdarma", "Free", "Kostenlos",
                price: 0, addOnPrice: null, maxActiveClients: 2,
                questionnairesAndCheckIns: false, perClientCheckInConfig: false),
            NewSeedPlan(
                "start", "Start", "Start", "Start",
                price: 29900, addOnPrice: 14900, maxActiveClients: 10,
                questionnairesAndCheckIns: false, perClientCheckInConfig: false),
            NewSeedPlan(
                "pro", "Pro", "Pro", "Pro",
                price: 59900, addOnPrice: 29900, maxActiveClients: 30,
                questionnairesAndCheckIns: true, perClientCheckInConfig: false),
            NewSeedPlan(
                "studio", "Studio", "Studio", "Studio",
                price: 99900, addOnPrice: null, maxActiveClients: null,
                questionnairesAndCheckIns: true, perClientCheckInConfig: true),
        };

        foreach (var plan in placeholderPlans)
        {
            if (!existingCodes.Contains(plan.Code))
            {
                context.SubscriptionPlans.Add(plan);
            }
        }

        await context.SaveChangesAsync();
    }

    private static SubscriptionPlan NewSeedPlan(
        string code,
        string nameCs,
        string nameEn,
        string nameDe,
        long price,
        long? addOnPrice,
        int? maxActiveClients,
        bool questionnairesAndCheckIns,
        bool perClientCheckInConfig)
    {
        var plan = new SubscriptionPlan
        {
            Code = code,
            NameCs = nameCs,
            NameEn = nameEn,
            NameDe = nameDe,
            BothRolesIncluded = addOnPrice is null,
            CanCreatePlans = true,
            CanMessage = true,
            CanSendQuestionnaires = questionnairesAndCheckIns,
            CanUseWeeklyCheckIns = questionnairesAndCheckIns,
            CanUsePerClientCheckInConfig = perClientCheckInConfig,
            // Client payments (packages) come with Pro, like forms and check-ins.
            CanUsePayments = questionnairesAndCheckIns,
            CanUsePlanTemplates = true,
            CanUseRecipes = true,
            CanUseIngredients = true,
            CanUseProgressPhotos = true,
            CanUseWeightAndMeasurements = true,
            CanUseAutomations = true,
            // Packages come with Pro, like client payments.
            CanUsePackages = questionnairesAndCheckIns,
            MaxActiveClients = maxActiveClients,
            PriceMinorUnits = price,
            SecondRoleAddOnPriceMinorUnits = addOnPrice,
            Currency = SupportedCurrencies.Czk,
            BillingInterval = BillingInterval.Monthly,
            IsActive = true,
        };

        plan.Prices.Add(new SubscriptionPlanPrice
        {
            SubscriptionPlanId = 0,
            SubscriptionPlan = plan,
            PriceMinorUnits = price,
            SecondRoleAddOnPriceMinorUnits = addOnPrice,
            Currency = SupportedCurrencies.Czk,
            ValidFrom = DateTime.UtcNow,
        });

        return plan;
    }

    /// <summary>
    /// Generates a cryptographically random password satisfying the configured Identity password
    /// policy (min length 8, requires digit + lowercase + uppercase). Never logged or persisted
    /// anywhere but the Identity password hash — the system admin account is not meant to log in.
    /// </summary>
    private static string GenerateRandomPassword()
    {
        const string lower = "abcdefghijkmnopqrstuvwxyz";
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string digits = "23456789";
        const string all = lower + upper + digits;

        var buffer = RandomNumberGenerator.GetBytes(32);
        var chars = new char[32];

        chars[0] = lower[buffer[0] % lower.Length];
        chars[1] = upper[buffer[1] % upper.Length];
        chars[2] = digits[buffer[2] % digits.Length];

        for (var i = 3; i < chars.Length; i++)
        {
            chars[i] = all[buffer[i] % all.Length];
        }

        return new string(chars);
    }
}
