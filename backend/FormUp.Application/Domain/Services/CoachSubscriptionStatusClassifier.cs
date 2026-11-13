using System.Linq.Expressions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Domain.Services;

/// <summary>
/// The single definition of a coach's subscription standing, used by the admin coaches list rows, filters and
/// stats, by the detail view, and by the entitlement rule for a given-free plan. Every predicate is an expression
/// over <see cref="ProfessionalProfile"/> so SQL evaluates it; a coach with no subscription row is Free.
/// </summary>
/// <remarks>
/// Buckets that split every coach exactly once: GivenFree (a gift in force), Paying (a paid plan, not a gift, status
/// Trialing, Active or PastDue), Free (everyone else, including an expired gift, a Canceled or Incomplete
/// subscription and the Free plan). PaymentFailed (PastDue) and Cancelling (cancelled renewal, not PastDue) are
/// subsets of Paying.
/// </remarks>
public static class CoachSubscriptionStatusClassifier
{
    /// <summary>
    /// Whether the assigned plan's features apply right now. A gift applies until its end (always when it has none),
    /// whatever the status; otherwise the plan applies while the status is Trialing, Active or PastDue, or while the
    /// clock is before <paramref name="freeUntil"/>.
    /// </summary>
    /// <param name="status">The subscription status.</param>
    /// <param name="freeUntil">End of the free-features window, or of the gift when <paramref name="givenFreeAt"/> is set.</param>
    /// <param name="givenFreeAt">When the plan was given for free, or null.</param>
    /// <param name="now">Current time.</param>
    public static bool PlanApplies(
        SubscriptionStatus status, DateTimeOffset? freeUntil, DateTimeOffset? givenFreeAt, DateTimeOffset now)
    {
        if (givenFreeAt is not null)
        {
            return freeUntil is null || now < freeUntil;
        }

        return now < freeUntil || status is SubscriptionStatus.Trialing or SubscriptionStatus.Active or SubscriptionStatus.PastDue;
    }

    /// <summary>
    /// Whether the coach must choose a plan: an admin-given plan whose end has passed. Separate from
    /// <see cref="PlanApplies"/>, which still reads such a plan as no longer applying.
    /// </summary>
    /// <param name="givenFreeAt">When the plan was given for free, or null.</param>
    /// <param name="freeUntil">End of the gift, or null for none.</param>
    /// <param name="now">Current time.</param>
    public static bool IsPlanChoiceRequired(DateTimeOffset? givenFreeAt, DateTimeOffset? freeUntil, DateTimeOffset now) =>
        givenFreeAt is not null && freeUntil is not null && now >= freeUntil;

    /// <summary>
    /// The predicate selecting the coaches of a filter bucket.
    /// </summary>
    /// <param name="filter">The bucket.</param>
    /// <param name="now">Current time.</param>
    public static Expression<Func<ProfessionalProfile, bool>> Matches(AdminCoachStatusFilter filter, DateTimeOffset now) =>
        filter switch
        {
            AdminCoachStatusFilter.All => profile => true,
            AdminCoachStatusFilter.Paying => Paying(),
            AdminCoachStatusFilter.PaymentFailed => PaymentFailed(),
            AdminCoachStatusFilter.Cancelling => Cancelling(),
            AdminCoachStatusFilter.GivenFree => GivenFree(now),
            AdminCoachStatusFilter.Free => Not(Or(GivenFree(now), Paying())),
            _ => throw new ArgumentOutOfRangeException(nameof(filter), filter, "Unknown coach status filter."),
        };

    /// <summary>
    /// The projection giving each profile its single <see cref="AdminCoachStatus"/>, built from the same predicates
    /// as <see cref="Matches"/>.
    /// </summary>
    /// <param name="now">Current time.</param>
    public static Expression<Func<ProfessionalProfile, CoachStatusProjection>> ProjectStatus(DateTimeOffset now)
    {
        var profile = Expression.Parameter(typeof(ProfessionalProfile), "profile");

        var status = Expression.Condition(
            Body(GivenFree(now), profile),
            Expression.Constant(AdminCoachStatus.GivenFree),
            Expression.Condition(
                Body(Paying(), profile),
                Expression.Condition(
                    Body(PaymentFailed(), profile),
                    Expression.Constant(AdminCoachStatus.PaymentFailed),
                    Expression.Condition(
                        Body(Cancelling(), profile),
                        Expression.Constant(AdminCoachStatus.Cancelling),
                        Expression.Constant(AdminCoachStatus.Paying))),
                Expression.Constant(AdminCoachStatus.Free)));

        var projection = Expression.MemberInit(
            Expression.New(typeof(CoachStatusProjection)),
            Expression.Bind(
                typeof(CoachStatusProjection).GetProperty(nameof(CoachStatusProjection.ProfessionalProfileId))!,
                Expression.Property(profile, nameof(ProfessionalProfile.Id))),
            Expression.Bind(typeof(CoachStatusProjection).GetProperty(nameof(CoachStatusProjection.Status))!, status));

        return Expression.Lambda<Func<ProfessionalProfile, CoachStatusProjection>>(projection, profile);
    }

    private static Expression<Func<ProfessionalProfile, bool>> GivenFree(DateTimeOffset now) =>
        profile => profile.CoachSubscription != null
                   && profile.CoachSubscription.GivenFreeAt != null
                   && (profile.CoachSubscription.FreeUntil == null || profile.CoachSubscription.FreeUntil > now);

    private static Expression<Func<ProfessionalProfile, bool>> Paying() =>
        profile => profile.CoachSubscription != null
                   && profile.CoachSubscription.GivenFreeAt == null
                   && profile.CoachSubscription.SubscriptionPlan.Code != SubscriptionPlanCodes.Free
                   && (profile.CoachSubscription.Status == SubscriptionStatus.Trialing
                       || profile.CoachSubscription.Status == SubscriptionStatus.Active
                       || profile.CoachSubscription.Status == SubscriptionStatus.PastDue);

    private static Expression<Func<ProfessionalProfile, bool>> PaymentFailed() =>
        And(Paying(), profile => profile.CoachSubscription!.Status == SubscriptionStatus.PastDue);

    private static Expression<Func<ProfessionalProfile, bool>> Cancelling() =>
        And(
            Paying(),
            profile => profile.CoachSubscription!.CancelAtPeriodEnd
                       && profile.CoachSubscription.Status != SubscriptionStatus.PastDue);

    private static Expression<Func<ProfessionalProfile, bool>> And(
        Expression<Func<ProfessionalProfile, bool>> left, Expression<Func<ProfessionalProfile, bool>> right) =>
        Combine(left, right, Expression.AndAlso);

    private static Expression<Func<ProfessionalProfile, bool>> Or(
        Expression<Func<ProfessionalProfile, bool>> left, Expression<Func<ProfessionalProfile, bool>> right) =>
        Combine(left, right, Expression.OrElse);

    private static Expression<Func<ProfessionalProfile, bool>> Not(Expression<Func<ProfessionalProfile, bool>> operand) =>
        Expression.Lambda<Func<ProfessionalProfile, bool>>(Expression.Not(operand.Body), operand.Parameters);

    private static Expression<Func<ProfessionalProfile, bool>> Combine(
        Expression<Func<ProfessionalProfile, bool>> left,
        Expression<Func<ProfessionalProfile, bool>> right,
        Func<Expression, Expression, BinaryExpression> join)
    {
        var parameter = left.Parameters[0];

        return Expression.Lambda<Func<ProfessionalProfile, bool>>(
            join(left.Body, Body(right, parameter)), parameter);
    }

    private static Expression Body(Expression<Func<ProfessionalProfile, bool>> predicate, ParameterExpression parameter) =>
        new ParameterReplacer(predicate.Parameters[0], parameter).Visit(predicate.Body);

    private sealed class ParameterReplacer(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
