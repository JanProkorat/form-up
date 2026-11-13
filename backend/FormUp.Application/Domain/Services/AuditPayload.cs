using System.Text.Encodings.Web;
using System.Text.Json;

namespace FormUp.Application.Domain.Services;

/// <summary>
/// Builds the JSON stored in an audit row's values column, which holds at most 500 characters.
/// </summary>
public static class AuditPayload
{
    /// <summary>Length of the audit values column.</summary>
    public const int MaxLength = 500;

    private static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// Serializes the fields and the free-text reason with System.Text.Json. When the result is longer than the
    /// column, the reason is shortened until it fits, so the stored text is always valid JSON.
    /// </summary>
    /// <param name="fields">Named values to store, in order.</param>
    /// <param name="reason">The caller's free-text reason, or null when there is none.</param>
    public static string WithReason(IReadOnlyDictionary<string, object?> fields, string? reason)
    {
        var text = reason;

        while (true)
        {
            var values = new Dictionary<string, object?>(fields);

            if (text is not null)
            {
                values["reason"] = text;
            }

            var json = JsonSerializer.Serialize(values, Options);

            if (json.Length <= MaxLength || text is null || text.Length == 0)
            {
                return json.Length <= MaxLength ? json : JsonSerializer.Serialize(fields, Options);
            }

            text = text[..Math.Max(0, text.Length - Math.Max(1, json.Length - MaxLength))];

            if (text.Length > 0 && char.IsHighSurrogate(text[^1]))
            {
                text = text[..^1];
            }
        }
    }
}
