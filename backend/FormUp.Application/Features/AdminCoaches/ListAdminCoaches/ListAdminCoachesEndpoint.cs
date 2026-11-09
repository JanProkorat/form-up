using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.AdminCoaches.Shared;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.AdminCoaches.ListAdminCoaches;

/// <summary>
/// Lists the coaches for the admin Coaches page: one page of rows plus stats over all coaches. Filter, search and
/// paging run in SQL; client counts, roles and statuses of the page come from one query each, never per row.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="timeProvider">Clock.</param>
internal sealed class ListAdminCoachesEndpoint(IApplicationDbContext db, TimeProvider timeProvider)
    : Endpoint<ListAdminCoachesRequest, ListAdminCoachesResponse>
{
    private const int MonthsPerYear = 12;

    /// <inheritdoc />
    public override void Configure()
    {
        Get("/admin/coaches");
        Roles(AppRoles.Admin);
        Summary(s =>
        {
            s.Summary = "List coaches";
            s.Description = "Returns one page of coaches (filter by status, search by name or email) with their plan, status, " +
                            "client count against the cap and renewal or end date, plus stats over all coaches. Status buckets: " +
                            "Paying + Free + GivenFree make up All; PaymentFailed and Cancelling are subsets of Paying. Revenue is " +
                            "grouped by currency. The promotion column is always null until promotions exist. Client names and " +
                            "emails are never returned, only counts. X-Total-Count carries the number of coaches matching the filter.";
            s.Response<ListAdminCoachesResponse>(StatusCodes.Status200OK, "A page of coaches and the stats");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest, "Invalid paging, status filter or search");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or invalid credentials");
            s.Response(StatusCodes.Status403Forbidden, "Caller is not an Admin");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(ListAdminCoachesRequest req, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();

        var matching = db.ProfessionalProfiles
            .AsNoTracking()
            .Where(CoachSubscriptionStatusClassifier.Matches(req.Status, now));

        if (!string.IsNullOrWhiteSpace(req.Search))
        {
            var search = req.Search.Trim().ToLower();
            matching = matching.Where(profile =>
                (profile.User.FirstName + " " + profile.User.LastName).ToLower().Contains(search)
                || profile.User.Email!.ToLower().Contains(search));
        }

        var total = await matching.CountAsync(ct);
        HttpContext.Response.Headers["X-Total-Count"] = total.ToString();

        var coaches = await matching
            .OrderBy(profile => profile.User.LastName)
            .ThenBy(profile => profile.User.FirstName)
            .ThenBy(profile => profile.Id)
            .Skip((req.Page - 1) * req.PageSize)
            .Take(req.PageSize)
            .Select(AdminCoachQueries.Project)
            .ToListAsync(ct);

        await Send.OkAsync(new ListAdminCoachesResponse
        {
            Stats = await LoadStatsAsync(now, ct),
            Items = await BuildRowsAsync(coaches, now, ct),
        }, ct);
    }

    private async Task<List<AdminCoachRowDto>> BuildRowsAsync(
        List<AdminCoachRecord> coaches, DateTimeOffset now, CancellationToken ct)
    {
        if (coaches.Count == 0)
        {
            return [];
        }

        var profileIds = coaches.Select(coach => coach.ProfessionalProfileId).ToList();
        var userIds = coaches.Select(coach => coach.CoachId).ToList();

        var statuses = await db.ProfessionalProfiles
            .AsNoTracking()
            .Where(profile => profileIds.Contains(profile.Id))
            .Select(CoachSubscriptionStatusClassifier.ProjectStatus(now))
            .ToDictionaryAsync(row => row.ProfessionalProfileId, row => row.Status, ct);
        var clientCounts = await AdminCoachQueries.CountClientsAsync(db, profileIds, ct);
        var heldRoles = await AdminCoachQueries.LoadHeldRolesAsync(db, userIds, ct);
        var free = await AdminCoachQueries.LoadFreePlanAsync(db, ct);

        return coaches.Select(coach =>
        {
            var status = statuses[coach.ProfessionalProfileId];
            var (plan, maxActiveClients) = AdminCoachQueries.ResolveEffectivePlan(coach, free, now);

            return new AdminCoachRowDto
            {
                CoachId = coach.CoachId,
                Name = $"{coach.FirstName} {coach.LastName}".Trim(),
                Email = coach.Email,
                Roles = AdminCoachQueries.ActiveRoles(coach, heldRoles.GetValueOrDefault(coach.CoachId)),
                Plan = plan,
                Status = status,
                ClientsUsed = clientCounts.GetValueOrDefault(coach.ProfessionalProfileId),
                ClientsMax = maxActiveClients,
                Promotion = null,
                CurrentPeriodEndsAt = status is AdminCoachStatus.Paying or AdminCoachStatus.PaymentFailed or AdminCoachStatus.Cancelling
                    ? coach.CurrentPeriodEndsAt
                    : null,
                GivenFreeUntil = status == AdminCoachStatus.GivenFree ? coach.FreeUntil : null,
            };
        }).ToList();
    }

    private async Task<AdminCoachStatsDto> LoadStatsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var counts = await db.ProfessionalProfiles
            .AsNoTracking()
            .Select(CoachSubscriptionStatusClassifier.ProjectStatus(now))
            .GroupBy(row => row.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Status, row => row.Count, ct);

        var paying = db.ProfessionalProfiles
            .AsNoTracking()
            .Where(CoachSubscriptionStatusClassifier.Matches(AdminCoachStatusFilter.Paying, now));

        var payingByPlan = await paying
            .GroupBy(profile => new
            {
                profile.CoachSubscription!.SubscriptionPlan.Code,
                profile.CoachSubscription.SubscriptionPlan.NameCs,
                profile.CoachSubscription.SubscriptionPlan.NameEn,
                profile.CoachSubscription.SubscriptionPlan.NameDe,
            })
            .Select(group => new { group.Key.Code, group.Key.NameCs, group.Key.NameEn, group.Key.NameDe, Count = group.Count() })
            .ToListAsync(ct);

        // Each coach pays the price row they were sold, not the plan's current price.
        var revenueRows = await paying
            .Where(profile => profile.CoachSubscription!.Status != SubscriptionStatus.Trialing)
            .Select(profile => new
            {
                profile.CoachSubscription!.SubscriptionPlanPrice.Currency,
                Annual = profile.CoachSubscription.SubscriptionPlan.BillingInterval == BillingInterval.Annual,
                Amount = profile.CoachSubscription.SubscriptionPlanPrice.PriceMinorUnits
                         + (profile.CoachSubscription.PaysSecondRoleAddOn && !profile.CoachSubscription.SubscriptionPlan.BothRolesIncluded
                             ? profile.CoachSubscription.SubscriptionPlanPrice.SecondRoleAddOnPriceMinorUnits ?? 0
                             : 0),
            })
            .GroupBy(row => new { row.Currency, row.Annual })
            .Select(group => new { group.Key.Currency, group.Key.Annual, Total = group.Sum(row => row.Amount) })
            .ToListAsync(ct);

        int Count(AdminCoachStatus status) => counts.GetValueOrDefault(status);

        return new AdminCoachStatsDto
        {
            AllCount = counts.Values.Sum(),
            PayingCount = Count(AdminCoachStatus.Paying) + Count(AdminCoachStatus.PaymentFailed) + Count(AdminCoachStatus.Cancelling),
            PaymentFailedCount = Count(AdminCoachStatus.PaymentFailed),
            CancellingCount = Count(AdminCoachStatus.Cancelling),
            FreeCount = Count(AdminCoachStatus.Free),
            GivenFreeCount = Count(AdminCoachStatus.GivenFree),
            PayingByPlan = payingByPlan
                .OrderByDescending(row => row.Count)
                .ThenBy(row => row.Code, StringComparer.Ordinal)
                .Select(row => new AdminCoachPlanCountDto
                {
                    Plan = new AdminCoachPlanDto { Code = row.Code, NameCs = row.NameCs, NameEn = row.NameEn, NameDe = row.NameDe },
                    Count = row.Count,
                })
                .ToList(),
            MonthlyRevenue = revenueRows
                .GroupBy(row => row.Currency)
                .Select(group => new AdminCoachRevenueDto
                {
                    Currency = group.Key,
                    AmountMinorUnits = group.Where(row => !row.Annual).Sum(row => row.Total)
                                       + group.Where(row => row.Annual).Sum(row => row.Total) / MonthsPerYear,
                })
                .OrderBy(row => row.Currency, StringComparer.Ordinal)
                .ToList(),
        };
    }
}
