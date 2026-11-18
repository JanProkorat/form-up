namespace FormUp.Application.Infrastructure.Services.Billing;

/// <summary>
/// A call to the payment provider failed. Callers that can recover (admin plan and given-free changes) map it to a 502.
/// </summary>
public sealed class BillingProviderException : Exception
{
    /// <summary>Creates the exception with a message that never carries provider payloads or secrets.</summary>
    /// <param name="message">What failed.</param>
    /// <param name="innerException">The provider exception.</param>
    public BillingProviderException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
