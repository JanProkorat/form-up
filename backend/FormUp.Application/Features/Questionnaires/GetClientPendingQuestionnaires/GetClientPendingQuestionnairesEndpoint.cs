using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Features.Questionnaires.Shared;
using FormUp.Application.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.Questionnaires.GetClientPendingQuestionnaires;

/// <summary>
/// Returns pending questionnaires AND pending photo-diary requests so the mobile
/// Today screen can render its top banner stack from one query.
///
/// Ordering convention (intentional):
///   1. <see cref="GetClientPendingQuestionnairesResponse.PendingDiaryRequests"/> — diary first,
///      sorted by <c>CreatedAt DESC</c> within the list.
///   2. <see cref="GetClientPendingQuestionnairesResponse.Items"/> — questionnaires second,
///      one per open response or per active link without one, oldest first by
///      <see cref="PendingQuestionnaireItem.SentAt"/> across all coaches.
///
/// The mobile client renders the two arrays in order, producing: diary banner(s) → questionnaire banner(s).
/// </summary>
public class GetClientPendingQuestionnairesEndpoint(
    IApplicationDbContext db,
    UserManager<ApplicationUser> userManager,
    TimeProvider timeProvider)
    : EndpointWithoutRequest<GetClientPendingQuestionnairesResponse>
{
    public override void Configure()
    {
        Get("/client/questionnaires/pending");
        Roles(AppRoles.Client);
        Summary(s =>
        {
            s.Summary = "Get pending questionnaires and diary requests for the client";
            s.Description =
                "Returns pending photo-diary requests (diary first) and pending/in-progress questionnaires " +
                "across all active professional links. Questionnaires are ordered oldest first by their sent time. " +
                "Single query — no additional round-trip required.";
        });
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);
        var emailClaim = User.FindFirstValue(AppClaims.Email);
        if (userId is null) { await Send.UnauthorizedAsync(ct); return; }
        var userGuid = Guid.Parse(userId);

        var clientProfile = await db.ClientProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(cp => cp.UserId == userGuid, ct);

        if (clientProfile is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // ── 1. Pending diary requests ─────────────────────────────────────────

        // Collect link IDs and invite IDs for this client (same dual-source pattern as ListClientRequests)
        var clientLinkIds = await db.ClientProfessionalLinks
            .AsNoTracking()
            .Where(l => l.ClientProfileId == clientProfile.Id && l.ConnectionState != ConnectionState.Ended)
            .Select(l => (long?)l.Id)
            .ToListAsync(ct);

        // Invites addressed to a coaching account are silent rows — a coach-role caller sees none.
        var isCoachCaller = User.IsInRole(AppRoles.Trainer) || User.IsInRole(AppRoles.Nutritionist);

        var clientInviteIds = emailClaim is not null && !isCoachCaller
            ? await db.PendingInvites
                .AsNoTracking()
                .Where(i => i.Email == emailClaim)
                .Select(i => (long?)i.Id)
                .ToListAsync(ct)
            : [];

        var pendingDiaryEntities = await db.PhotoDiaryRequests
            .AsNoTracking()
            .Include(r => r.Professional)
            .Include(r => r.Link)
            .Where(r =>
                r.Status == PhotoDiaryStatus.Pending &&
                (
                    (r.LinkId != null && clientLinkIds.Contains(r.LinkId)) ||
                    (r.PendingInviteId != null && clientInviteIds.Contains(r.PendingInviteId))
                ))
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(ct);

        var pendingDiaryItems = new List<PendingDiaryRequestItem>(pendingDiaryEntities.Count);
        var now = timeProvider.GetUtcNow();

        foreach (var req in pendingDiaryEntities)
        {
            // A Pending request past its end date can no longer be accepted, so it is not a banner.
            if (now >= req.EndsAt)
            {
                continue;
            }

            // Resolve professional role:
            //   - Link-based: use the link's stored ProfessionalRole (no extra DB hit).
            //   - Invite-based: look up Identity roles (UserManager is already injected).
            string? professionalRole = null;

            if (req.Link is not null)
            {
                professionalRole = req.Link.ProfessionalRole.ToString();
            }
            else
            {
                var roles = await userManager.GetRolesAsync(req.Professional);
                professionalRole = roles.Contains(AppRoles.Nutritionist)
                    ? AppRoles.Nutritionist
                    : roles.Contains(AppRoles.Trainer) ? AppRoles.Trainer : null;
            }

            pendingDiaryItems.Add(new PendingDiaryRequestItem
            {
                RequestPublicId = req.Id,
                ProfessionalName = $"{req.Professional.FirstName} {req.Professional.LastName}",
                ProfessionalRole = professionalRole,
                DurationDays = req.DurationDays,
                Mode = req.Mode,
                Message = req.Message,
                Status = "Pending",
                PlanId = req.PlanId,
                CreatedAt = req.CreatedAt,
            });
        }

        // ── 2. Pending questionnaires (existing logic, unchanged) ─────────────

        // Get all active professional links (with profile/user navigation)
        var links = await db.ClientProfessionalLinks
            .Include(l => l.ProfessionalProfile).ThenInclude(pp => pp.User)
            .Where(l => l.ClientProfileId == clientProfile.Id && l.ConnectionState != ConnectionState.Ended)
            .ToListAsync(ct);

        var questionnaireItems = new List<PendingQuestionnaireItem>();

        foreach (var link in links)
        {
            var activatedAtUtc = link.ActivatedAtUtc;

            // Responses of the current collaboration; ones left over from an earlier one are ignored.
            // A submission counts by when it was submitted, an open response by when it was created.
            var linkResponses = await db.QuestionnaireResponses
                .AsNoTracking()
                .Include(r => r.Questionnaire)
                .Where(r => r.ClientId == userGuid
                            && r.LinkId == link.Id
                            && (activatedAtUtc == null
                                || (r.Status == QuestionnaireResponseStatus.Submitted
                                    ? r.SubmittedAt >= activatedAtUtc
                                    : r.DateCreated >= activatedAtUtc)))
                .OrderBy(r => r.Id)
                .ToListAsync(ct);

            // One item per open response, so a link widened to a second role lists both questionnaires.
            // The submitted check is per response: answering one never hides the other.
            if (linkResponses.Count > 0)
            {
                // A check-in past its answer window is no longer offered.
                foreach (var response in linkResponses.Where(r =>
                             r.Status is QuestionnaireResponseStatus.Pending or QuestionnaireResponseStatus.InProgress
                             && !r.IsAnswerWindowClosed(now.UtcDateTime)))
                {
                    questionnaireItems.Add(new PendingQuestionnaireItem
                    {
                        LinkPublicId = link.PublicId,
                        ProfessionalName = $"{link.ProfessionalProfile.User.FirstName} {link.ProfessionalProfile.User.LastName}",
                        ProfessionalRole = link.ProfessionalRole.ToString(),
                        QuestionnairePublicId = response.Questionnaire.PublicId,
                        QuestionnaireTitle = response.Questionnaire.Title,
                        QuestionCount = await CountAnswerableQuestionsAsync(response.QuestionnaireId, ct),
                        SentAt = response.DateCreated,
                        ResponsePublicId = response.PublicId,
                        ResponseStatus = response.Status.ToString(),
                        Scope = response.Scope,
                        Kind = response.Kind,
                        CheckInWeekStart = response.CheckInWeekStart,
                        CheckInDueAtUtc = response.CheckInDueAtUtc,
                    });
                }

                continue;
            }

            // No response rows: fall back to the link's questionnaire or the coach's default (scope unknown).
            // Skip links where the client already submitted a questionnaire response.
            // A link with an activation time only counts a submission made in the current collaboration.
            var hasSubmitted = await db.QuestionnaireResponses
                .AsNoTracking()
                .AnyAsync(r =>
                    r.ClientId == userGuid
                    && r.Status == QuestionnaireResponseStatus.Submitted
                    && (activatedAtUtc == null
                        ? (r.LinkId == link.Id || r.ProfessionalId == link.ProfessionalProfile.UserId)
                        : (r.LinkId == link.Id && r.SubmittedAt >= activatedAtUtc)), ct);

            if (hasSubmitted) continue;

            // Resolve which questionnaire applies for this link
            Domain.Entities.Questionnaire? questionnaire = null;

            if (link.QuestionnaireId.HasValue)
            {
                questionnaire = await db.Questionnaires
                    .AsNoTracking()
                    .FirstOrDefaultAsync(q => q.Id == link.QuestionnaireId.Value && q.IsActive, ct);
            }

            // A link whose coach chose "no onboarding form" never falls back to the default form.
            if (questionnaire is null && !link.SkipOnboardingForm)
            {
                questionnaire = await db.Questionnaires
                    .AsNoTracking()
                    .FirstOrDefaultAsync(q => q.ProfessionalId == link.ProfessionalProfile.UserId && q.IsDefault && q.IsActive && q.Type == QuestionnaireType.Onboarding, ct);
            }

            if (questionnaire is null) continue;

            questionnaireItems.Add(new PendingQuestionnaireItem
            {
                LinkPublicId = link.PublicId,
                ProfessionalName = $"{link.ProfessionalProfile.User.FirstName} {link.ProfessionalProfile.User.LastName}",
                ProfessionalRole = link.ProfessionalRole.ToString(),
                QuestionnairePublicId = questionnaire.PublicId,
                QuestionnaireTitle = questionnaire.Title,
                QuestionCount = await CountAnswerableQuestionsAsync(questionnaire.Id, ct),
                SentAt = link.ActivatedAtUtc ?? link.DateCreated,
            });
        }

        // ── 3. Return: diary first, questionnaires second (oldest first) ──────

        await Send.OkAsync(new GetClientPendingQuestionnairesResponse
        {
            PendingDiaryRequests = pendingDiaryItems,
            Items = questionnaireItems
                .OrderBy(item => item.SentAt)
                .ThenBy(item => item.LinkPublicId)
                .ThenBy(item => item.ResponsePublicId)
                .ToList(),
        }, ct);
    }

    private Task<int> CountAnswerableQuestionsAsync(long questionnaireId, CancellationToken ct) =>
        db.QuestionnaireQuestions
            .AsNoTracking()
            .Where(q => q.QuestionnaireId == questionnaireId)
            .Where(ClientAnswerableQuestion.Predicate)
            .CountAsync(ct);
}
