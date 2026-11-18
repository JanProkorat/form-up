namespace FormUp.Application.Domain.Enums;

/// <summary>
/// How coaches get a <see cref="Entities.Promotion"/>.
/// </summary>
public enum PromotionMode
{
    /// <summary>Applied at checkout to every coach who qualifies.</summary>
    Automatic,

    /// <summary>Claimed by typing its code.</summary>
    Code
}
