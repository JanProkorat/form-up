namespace FormUp.Application.Domain.Extensions;

/// <summary>
/// Helpers for <see cref="DateTime"/> values bound from requests.
/// </summary>
public static class DateTimeExtensions
{
    /// <summary>
    /// Returns a UTC value Npgsql accepts for <c>timestamptz</c>: Local is converted,
    /// Unspecified is treated as already UTC, Utc is returned unchanged.
    /// </summary>
    public static DateTime ToUtcKind(this DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value,
    };
}
