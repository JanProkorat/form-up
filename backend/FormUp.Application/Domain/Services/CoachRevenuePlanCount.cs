namespace FormUp.Application.Domain.Services;

/// <summary>
/// Paying coaches on one plan.
/// </summary>
/// <param name="Code">Plan code.</param>
/// <param name="NameCs">Czech plan name.</param>
/// <param name="NameEn">English plan name.</param>
/// <param name="NameDe">German plan name.</param>
/// <param name="Count">Number of coaches.</param>
public sealed record CoachRevenuePlanCount(string Code, string NameCs, string NameEn, string NameDe, int Count);
