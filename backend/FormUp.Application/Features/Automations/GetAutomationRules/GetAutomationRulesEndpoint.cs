using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.Automations.Shared;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.Automations.GetAutomationRules;

/// <summary>
/// Lists the caller's global automation rules.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="mongo">MongoDB context, read for plan-event send dates.</param>
/// <param name="timeProvider">Clock.</param>
public class GetAutomationRulesEndpoint(IApplicationDbContext db, IMongoContext mongo, TimeProvider timeProvider)
    : EndpointWithoutRequest<GetAutomationRulesResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/trainer/automations/rules");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "List automation rules";
            s.Description =
                "Returns the caller's global rules, each with a coverage summary, and the weekly check-in per profession as a linked entry; client-only rules are listed on the client.";
            s.Responses[StatusCodes.Status200OK] = "The caller's global rules";
            s.Responses[StatusCodes.Status401Unauthorized] = "Missing or invalid credentials";
            s.Responses[StatusCodes.Status404NotFound] = "Caller has no professional profile";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var profile = await this.LoadCallerProfileOrRespondAsync(db, Guid.Parse(userId), ct);

        if (profile is null)
        {
            return;
        }

        var rules = await db.AutomationRules
            .AsNoTracking()
            .Where(r => r.OwnerProfessionalProfileId == profile.Id && r.ClientProfileId == null)
            .OrderBy(r => r.DateCreated)
            .ThenBy(r => r.Id)
            .WithDetails()
            .ToListAsync(ct);

        var covered = await AutomationCoverage.LoadCoveredClientsAsync(db, profile.Id, ct);
        var summaries = await AutomationRuleSummaryBuilder.BuildSummariesAsync(
            db, mongo, profile.UserId, rules, covered, timeProvider.GetUtcNow().UtcDateTime, ct);

        var items = rules
            .Select(rule =>
            {
                var dto = AutomationRuleDto.FromEntity(rule);
                dto.Summary = summaries[rule.Id];
                return dto;
            })
            .ToList();

        await Send.OkAsync(new GetAutomationRulesResponse
        {
            Rules = items,
        }, ct);
    }
}
