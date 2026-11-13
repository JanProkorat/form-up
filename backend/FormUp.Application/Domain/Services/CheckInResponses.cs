using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Domain.Services;

/// <summary>
/// Reads check-ins as the questionnaire responses of kind check-in: which rows count, and how a row
/// maps onto the check-in status the clients still show.
/// </summary>
public static class CheckInResponses
{
    /// <summary>
    /// The check-in responses of a live link, left by the link's current collaboration, not cancelled.
    /// An unsubmitted response counts by when it was created, a submitted one by when it was submitted.
    /// </summary>
    /// <param name="responses">The responses to filter.</param>
    public static IQueryable<QuestionnaireResponse> CurrentCollaborationCheckIns(this IQueryable<QuestionnaireResponse> responses) =>
        responses.Where(r => r.Kind == QuestionnaireResponseKind.CheckIn
                             && r.Status != QuestionnaireResponseStatus.Cancelled
                             && r.Link.ConnectionState != ConnectionState.Ended
                             && (r.Link.ActivatedAtUtc == null
                                 || (r.Status == QuestionnaireResponseStatus.Submitted
                                     ? r.SubmittedAt >= r.Link.ActivatedAtUtc
                                     : r.DateCreated >= r.Link.ActivatedAtUtc)));

    /// <summary>
    /// The check-in responses of any collaboration, not cancelled.
    /// </summary>
    /// <param name="responses">The responses to filter.</param>
    public static IQueryable<QuestionnaireResponse> AllCheckIns(this IQueryable<QuestionnaireResponse> responses) =>
        responses.Where(r => r.Kind == QuestionnaireResponseKind.CheckIn
                             && r.Status != QuestionnaireResponseStatus.Cancelled);

    /// <summary>
    /// Maps a check-in response onto the lifecycle status: Reviewed once the coach opened the submission,
    /// Responded once submitted, Expired when unsubmitted past its due time, otherwise Pending.
    /// </summary>
    /// <param name="status">The response status.</param>
    /// <param name="reviewedByCoachAtUtc">When the coach first opened the submission, if they did.</param>
    /// <param name="dueAtUtc">When the check-in is due.</param>
    /// <param name="nowUtc">The current instant.</param>
    public static WeeklyCheckInStatus ResolveStatus(
        QuestionnaireResponseStatus status,
        DateTime? reviewedByCoachAtUtc,
        DateTime? dueAtUtc,
        DateTime nowUtc)
    {
        if (reviewedByCoachAtUtc is not null)
        {
            return WeeklyCheckInStatus.Reviewed;
        }

        if (status == QuestionnaireResponseStatus.Submitted)
        {
            return WeeklyCheckInStatus.Responded;
        }

        return dueAtUtc is not null && dueAtUtc <= nowUtc ? WeeklyCheckInStatus.Expired : WeeklyCheckInStatus.Pending;
    }

    /// <summary>The profession a link role stands for.</summary>
    /// <param name="role">The professional's role on the link.</param>
    public static Profession ProfessionOf(UserRole role) =>
        role == UserRole.Nutritionist ? Profession.Nutrition : Profession.Training;

    /// <summary>The week a check-in covers: its recorded Monday, or the Monday of the day it was sent.</summary>
    /// <param name="checkInWeekStart">The recorded week start, if any.</param>
    /// <param name="dateCreated">When the response was created.</param>
    public static DateOnly WeekStartOf(DateOnly? checkInWeekStart, DateTime dateCreated)
    {
        if (checkInWeekStart is not null)
        {
            return checkInWeekStart.Value;
        }

        var date = DateOnly.FromDateTime(dateCreated);
        var daysFromMonday = date.DayOfWeek == DayOfWeek.Sunday ? 6 : (int)date.DayOfWeek - 1;
        return date.AddDays(-daysFromMonday);
    }
}
