using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.NutritionPlanTemplates.Shared;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using Microsoft.EntityFrameworkCore;
using MongoDB.Driver;

namespace FormUp.Application.Features.NutritionPlanTemplates.InstantiateTemplate;

/// <summary>
/// Instantiates a nutrition plan template into a new Draft client plan — a verbatim copy plus
/// <c>clientId</c>/<c>name</c>/<c>startDate</c>, with every week Draft. Replicates the existing
/// plan-creation coach↔client link check, start-date rules, and overlap check rather than
/// bypassing them (mirrors <c>CreatePlanEndpoint</c>).
/// </summary>
/// <param name="mongo">MongoDB context.</param>
/// <param name="linkAuthorizationService">Resolves the nutritionist-client link's CanViewNutritionPlans permission.</param>
/// <param name="db">PostgreSQL context for cross-DB validation.</param>
/// <param name="timeProvider">Injected time source for audit timestamps.</param>
[RemovedCoachRole(AppRoles.Nutritionist, RemovedCoachRoleMode.Refuse)]
public class InstantiateTemplateEndpoint(
    IMongoContext mongo,
    IClientLinkAuthorizationService linkAuthorizationService,
    IApplicationDbContext db,
    TimeProvider timeProvider)
    : Endpoint<InstantiateNutritionPlanTemplateRequest, InstantiateNutritionPlanTemplateResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/nutrition/plan-templates/{TemplateId}/instantiate");
        Roles(AppRoles.Nutritionist);
        Description(b => b.ClearDefaultProduces(StatusCodes.Status200OK)
            .Produces<InstantiateNutritionPlanTemplateResponse>(StatusCodes.Status201Created));
        Summary(s =>
        {
            s.Summary = "Instantiate a nutrition plan template";
            s.Description = "Creates a new Draft client plan from a template, every week Draft.";
            s.Responses[StatusCodes.Status403Forbidden] =
                "The subscription does not include plans (SUBSCRIPTION_REQUIRED), or the client is over the Free plan's client limit (CLIENT_OVER_PLAN_LIMIT)";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(InstantiateNutritionPlanTemplateRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var nutritionistId = Guid.Parse(userId);

        // Read-guarded, not write-guarded — another nutritionist's Public template must stay
        // instantiable even though this endpoint writes a new NutritionPlan.
        var template = await this.LoadLibraryEntryForReadOrRespondAsync(
            mongo.NutritionPlanTemplates, req.TemplateId, nutritionistId, NutritionPlanTemplateLibrary.Denial, ct);

        if (template is null)
        {
            return;
        }

        // req.ClientId is the nutritionist-facing ClientProfile.PublicId — the PublicId-addressed
        // overload. Instantiating a nutrition plan requires CanViewNutritionPlans specifically.
        var capabilities = await linkAuthorizationService.GetCapabilitiesByClientPublicIdAsync(
            nutritionistId, req.ClientId, ct);

        if (capabilities is not { CanViewNutritionPlans: true })
        {
            // 404 via the shared library helper, never a bare 404 and never 403 — a 403 would
            // confirm the client exists to an unlinked coach. Matches the TrainingPlanTemplates
            // sibling (#939).
            await this.SendLibraryNotFoundAsync(NutritionPlanTemplateLibrary.Denial, ct);
            return;
        }

        // After the template and client-link checks, so a missing template or unlinked client still answers 404.
        if (await this.RespondIfFeatureNotEntitledAsync(
                db, nutritionistId, entitlements => entitlements.CanCreatePlans,
                "The current subscription does not include creating plans.", ct))
        {
            return;
        }

        var clientProfile = await db.ClientProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(cp => cp.PublicId == req.ClientId, ct);

        if (clientProfile is null)
        {
            await this.SendLibraryNotFoundAsync(NutritionPlanTemplateLibrary.Denial, ct);
            return;
        }

        var clientUserId = clientProfile.UserId;

        if (await this.RespondIfClientOverPlanLimitAsync(db, nutritionistId, clientUserId, ct))
        {
            return;
        }

        if (req.StartDate.HasValue)
        {
            var candidateStart = DateTime.SpecifyKind(req.StartDate.Value.Date, DateTimeKind.Utc);

            var existingFilter = Builders<NutritionPlan>.Filter.Eq(p => p.ClientId, clientUserId)
                                & Builders<NutritionPlan>.Filter.Ne(p => p.Status, NutritionPlanStatus.Archived)
                                & Builders<NutritionPlan>.Filter.Ne(p => p.Status, NutritionPlanStatus.Completed)
                                & Builders<NutritionPlan>.Filter.Ne(p => p.StartDate, null);

            using var existingCursor = await mongo.NutritionPlans.FindAsync(existingFilter, cancellationToken: ct);
            var existingPlans = await existingCursor.ToListAsync(ct);

            var overlaps = existingPlans.Any(p =>
                PlanWindowResolver.WindowsOverlap(candidateStart, template.WeekCount, p.StartDate!.Value, p.Weeks.Count));

            if (overlaps)
            {
                await this.SendProblemAsync(409, ErrorCodes.PlanOverlap,
                    "The selected date range overlaps an existing plan for this client.", ct);
                return;
            }
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        var plan = new NutritionPlan
        {
            ExternalId = Guid.NewGuid(),
            ClientId = clientUserId,
            NutritionistId = nutritionistId,
            Name = req.Name,
            SourceTemplateId = template.ExternalId,
            Status = NutritionPlanStatus.Draft,
            GlobalSettings = template.GlobalSettings,
            Goal = template.Goal,
            Supplements = NutritionPlanContentCloner.CloneSupplements(template.Supplements, mintFreshExternalIds: true),
            Weeks = NutritionPlanContentCloner.CloneWeeksAsPlan(template.Weeks),
            Version = 1,
            DateCreated = now,
            StartDate = req.StartDate.HasValue ? DateTime.SpecifyKind(req.StartDate.Value.Date, DateTimeKind.Utc) : null
        };

        await mongo.NutritionPlans.InsertOneAsync(plan, cancellationToken: ct);

        await HttpContext.Response.SendAsync(new InstantiateNutritionPlanTemplateResponse
        {
            PlanId = plan.ExternalId,
            ClientId = req.ClientId,
            Name = plan.Name,
            Status = plan.Status.ToString(),
            DateCreated = plan.DateCreated
        }, 201, cancellation: ct);
    }
}
