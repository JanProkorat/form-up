namespace FormUp.Application.Seed.WalkSeed;

/// <summary>
/// The Stripe sandbox calls the walk seed needs. A seam so tests run without Stripe.
/// </summary>
internal interface IWalkStripeGateway
{
    /// <summary>
    /// Creates a fresh customer and a paid subscription to <paramref name="priceId"/>. When
    /// <paramref name="requireThreeDSecureOnLaterPayments"/> is true the card left on file needs 3D Secure.
    /// </summary>
    Task<WalkStripeSubscription> CreatePaidSubscriptionAsync(
        string walkKey, string email, string priceId, bool requireThreeDSecureOnLaterPayments, CancellationToken ct);

    /// <summary>
    /// Cancels a previous subscription at once, without a refund, and deletes its customer. Ids that no longer
    /// exist at Stripe are ignored.
    /// </summary>
    Task RetireAsync(string? customerId, string? subscriptionId, CancellationToken ct);
}
