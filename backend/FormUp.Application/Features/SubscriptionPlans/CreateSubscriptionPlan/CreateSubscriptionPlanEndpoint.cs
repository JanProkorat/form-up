using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Features.SubscriptionPlans.Shared;
using FormUp.Application.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FormUp.Application.Features.SubscriptionPlans.CreateSubscriptionPlan;

/// <summary>
/// Creates a new subscription tier. Admin-only — tier definitions are data managed here,
/// not hardcoded (#595).
/// </summary>
/// <param name="db">Application database context.</param>
internal sealed class CreateSubscriptionPlanEndpoint(IApplicationDbContext db)
    : Endpoint<CreateSubscriptionPlanRequest, SubscriptionPlanDto>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/admin/subscription-plans");
        Roles(AppRoles.Admin);
        Summary(s =>
        {
            s.Summary = "Create subscription plan";
            s.Description = "Creates a new subscription tier. Code is the immutable entitlement/Stripe mapping key.";
            s.Response<SubscriptionPlanDto>(StatusCodes.Status201Created, "Subscription plan created");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest, "Invalid request body");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or invalid credentials");
            s.Response(StatusCodes.Status403Forbidden, "Caller is not an Admin");
            s.Response(StatusCodes.Status409Conflict, "A plan with this Code already exists");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(CreateSubscriptionPlanRequest req, CancellationToken ct)
    {
        var codeExists = await db.SubscriptionPlans
            .AsNoTracking()
            .AnyAsync(p => p.Code == req.Code, ct);

        if (codeExists)
        {
            await this.SendProblemAsync(
                StatusCodes.Status409Conflict,
                ErrorCodes.SubscriptionPlanCodeAlreadyExists,
                "A subscription plan with this Code already exists.",
                ct);
            return;
        }

        var plan = new SubscriptionPlan
        {
            Code = req.Code,
            NameCs = req.NameCs,
            NameEn = req.NameEn,
            NameDe = req.NameDe,
            BothRolesIncluded = req.BothRolesIncluded,
            CanCreatePlans = req.CanCreatePlans,
            CanMessage = req.CanMessage,
            CanSendQuestionnaires = req.CanSendQuestionnaires,
            CanUseWeeklyCheckIns = req.CanUseWeeklyCheckIns,
            CanUsePerClientCheckInConfig = req.CanUsePerClientCheckInConfig,
            CanUsePayments = req.CanUsePayments,
            MaxActiveClients = req.MaxActiveClients,
            PriceMinorUnits = req.PriceMinorUnits,
            SecondRoleAddOnPriceMinorUnits = req.SecondRoleAddOnPriceMinorUnits,
            Currency = req.Currency,
            BillingInterval = req.BillingInterval,
            IsActive = req.IsActive,
        };

        plan.Prices.Add(new SubscriptionPlanPrice
        {
            SubscriptionPlan = plan,
            SubscriptionPlanId = 0,
            PriceMinorUnits = req.PriceMinorUnits,
            SecondRoleAddOnPriceMinorUnits = req.SecondRoleAddOnPriceMinorUnits,
            Currency = req.Currency,
            ExternalPriceId = req.ExternalPriceId,
            ValidFrom = DateTime.UtcNow,
        });

        db.SubscriptionPlans.Add(plan);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // A concurrent request (double-click Save, retried POST) won the race on the
            // Code unique index between the AnyAsync pre-check above and this insert.
            // Map it to the same 409 the pre-check returns instead of letting it surface
            // as an unhandled 500 through the global exception handler.
            await this.SendProblemAsync(
                StatusCodes.Status409Conflict,
                ErrorCodes.SubscriptionPlanCodeAlreadyExists,
                "A subscription plan with this Code already exists.",
                ct);
            return;
        }

        await Send.ResponseAsync(SubscriptionPlanDto.FromEntity(plan, req.ExternalPriceId, 0), StatusCodes.Status201Created, ct);
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException pgEx && pgEx.SqlState == "23505";
}
