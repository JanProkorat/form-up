namespace FormUp.Application.Seed.WalkSeed;

/// <summary>
/// What the walk seed keeps of a Stripe sandbox subscription it created.
/// </summary>
/// <param name="CustomerId">Stripe customer id.</param>
/// <param name="SubscriptionId">Stripe subscription id.</param>
/// <param name="CurrentPeriodEndsAt">End of the paid period.</param>
internal sealed record WalkStripeSubscription(string CustomerId, string SubscriptionId, DateTimeOffset CurrentPeriodEndsAt);
