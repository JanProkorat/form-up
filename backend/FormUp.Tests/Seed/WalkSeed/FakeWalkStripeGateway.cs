using FormUp.Application.Seed.WalkSeed;

namespace FormUp.Tests.Seed.WalkSeed;

/// <summary>
/// <see cref="IWalkStripeGateway"/> that records its calls and hands out deterministic ids. No Stripe involved.
/// </summary>
internal sealed class FakeWalkStripeGateway : IWalkStripeGateway
{
    private int counter;

    /// <summary>Every created subscription, in call order: walk key, price id, 3D Secure flag.</summary>
    public List<(string WalkKey, string PriceId, bool ThreeDSecure)> Created { get; } = [];

    /// <summary>Every retired customer/subscription pair, in call order.</summary>
    public List<(string? CustomerId, string? SubscriptionId)> Retired { get; } = [];

    /// <summary>Runs before a retire is recorded; lets a test look at the database at that moment.</summary>
    public Func<string?, string?, Task>? BeforeRetire { get; set; }

    /// <summary>Number of Stripe calls so far.</summary>
    public int CallCount => Created.Count + Retired.Count;

    /// <inheritdoc />
    public Task<WalkStripeSubscription> CreatePaidSubscriptionAsync(
        string walkKey, string email, string priceId, bool requireThreeDSecureOnLaterPayments, CancellationToken ct)
    {
        Created.Add((walkKey, priceId, requireThreeDSecureOnLaterPayments));
        var number = Interlocked.Increment(ref counter);

        return Task.FromResult(new WalkStripeSubscription(
            $"cus_walk_{number}", $"sub_walk_{number}", DateTimeOffset.UtcNow.AddDays(30)));
    }

    /// <inheritdoc />
    public async Task RetireAsync(string? customerId, string? subscriptionId, CancellationToken ct)
    {
        if (BeforeRetire is not null)
        {
            await BeforeRetire(customerId, subscriptionId);
        }

        Retired.Add((customerId, subscriptionId));
    }
}
