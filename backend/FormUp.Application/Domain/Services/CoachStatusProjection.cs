using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Domain.Services;

/// <summary>
/// A professional profile's id and its <see cref="AdminCoachStatus"/>, computed in SQL by
/// <see cref="CoachSubscriptionStatusClassifier.ProjectStatus"/>.
/// </summary>
public class CoachStatusProjection
{
    /// <summary>The professional's <c>ProfessionalProfile.Id</c>.</summary>
    public long ProfessionalProfileId { get; set; }

    /// <summary>The coach's status.</summary>
    public AdminCoachStatus Status { get; set; }
}
