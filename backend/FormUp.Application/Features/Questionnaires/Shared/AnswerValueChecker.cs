using System.Text.Json;

namespace FormUp.Application.Features.Questionnaires.Shared;

/// <summary>
/// Checks one client answer against its question's type and config.
/// Encoding: single_choice = valueText; multi_select = valueJson string array (+ valueText for "Other");
/// short_text = valueText; number / scale = valueNumber within config bounds (scale integer, default 1-10);
/// smiley_rating / star_rating = integer valueNumber 1-5; file_upload = fileUploadId; section takes no answer.
/// An answer with every value blank is "cleared" and always valid.
/// </summary>
public static class AnswerValueChecker
{
    private const int MaxTextLength = 2000;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>True when no value field carries content (a cleared answer); <paramref name="fileUrl"/> is the stored value.</summary>
    public static bool IsBlank(string? valueText, decimal? valueNumber, string? valueJson, string? fileUrl) =>
        IsBlankCore(valueText, valueNumber, valueJson, !string.IsNullOrWhiteSpace(fileUrl));

    /// <summary>Same as <see cref="IsBlank"/>, for a client request that carries a <paramref name="fileUploadId"/>.</summary>
    public static bool IsBlankUpload(string? valueText, decimal? valueNumber, string? valueJson, Guid? fileUploadId) =>
        IsBlankCore(valueText, valueNumber, valueJson, fileUploadId.HasValue);

    private static bool IsBlankCore(string? valueText, decimal? valueNumber, string? valueJson, bool hasFile) =>
        string.IsNullOrWhiteSpace(valueText)
        && valueNumber is null
        && string.IsNullOrWhiteSpace(valueJson)
        && !hasFile;

    /// <summary>Returns why the answer is invalid, or null when it is acceptable.</summary>
    public static string? FindProblem(
        string? type, string? config, string? valueText, decimal? valueNumber, string? valueJson, Guid? fileUploadId)
    {
        if (IsBlankUpload(valueText, valueNumber, valueJson, fileUploadId))
        {
            return null;
        }

        var hasText = !string.IsNullOrWhiteSpace(valueText);
        var hasJson = !string.IsNullOrWhiteSpace(valueJson);
        var hasFile = fileUploadId.HasValue;

        if (valueText is { Length: > MaxTextLength })
        {
            return "Text answer is too long.";
        }

        try
        {
            return type switch
            {
                QuestionTypes.SingleChoice => CheckSingleChoice(config, valueText, valueNumber, hasJson, hasFile),
                QuestionTypes.MultiSelect => CheckMultiSelect(config, hasText, valueNumber, valueJson, hasFile),
                QuestionTypes.ShortText => hasText && valueNumber is null && !hasJson && !hasFile
                    ? null
                    : "Short text takes valueText only.",
                QuestionTypes.Number => CheckNumber(config, hasText, valueNumber, hasJson, hasFile),
                QuestionTypes.SmileyRating or QuestionTypes.StarRating =>
                    CheckWholeNumber(1, 5, hasText, valueNumber, hasJson, hasFile),
                QuestionTypes.Scale => CheckScale(config, hasText, valueNumber, hasJson, hasFile),
                QuestionTypes.FileUpload => CheckFile(hasFile, hasText, valueNumber, hasJson),
                QuestionTypes.Section => "A section takes no answer.",
                _ => "Unknown question type.",
            };
        }
        catch (JsonException)
        {
            return "Question config or answer JSON is malformed.";
        }
    }

    private static string? CheckSingleChoice(string? config, string? valueText, decimal? valueNumber, bool hasJson, bool hasFile)
    {
        if (string.IsNullOrWhiteSpace(valueText) || valueNumber is not null || hasJson || hasFile)
        {
            return "Single choice takes valueText only.";
        }

        var choice = ParseChoice(config);
        return choice.AllowOther || choice.Options.Contains(valueText)
            ? null
            : "Answer is not one of the options.";
    }

    private static string? CheckMultiSelect(string? config, bool hasText, decimal? valueNumber, string? valueJson, bool hasFile)
    {
        if (valueNumber is not null || hasFile)
        {
            return "Multi select takes valueJson and an optional Other valueText only.";
        }

        var choice = ParseChoice(config);

        if (hasText && !choice.AllowOther)
        {
            return "Other text is not allowed for this question.";
        }

        if (string.IsNullOrWhiteSpace(valueJson))
        {
            return null;
        }

        using var document = JsonDocument.Parse(valueJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return "valueJson must be a JSON array.";
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                return "valueJson must contain strings only.";
            }

            var option = item.GetString()!;
            if (!seen.Add(option))
            {
                return "valueJson must not contain duplicates.";
            }

            if (!choice.Options.Contains(option))
            {
                return "valueJson contains a value that is not an option.";
            }
        }

        return null;
    }

    private static string? CheckNumber(string? config, bool hasText, decimal? valueNumber, bool hasJson, bool hasFile)
    {
        if (valueNumber is null || hasText || hasJson || hasFile)
        {
            return "Number takes valueNumber only.";
        }

        var limits = string.IsNullOrWhiteSpace(config)
            ? null
            : JsonSerializer.Deserialize<NumberQuestionConfig>(config, Options);

        return (limits?.Min is null || valueNumber >= limits.Min) && (limits?.Max is null || valueNumber <= limits.Max)
            ? null
            : "Number is outside the allowed range.";
    }

    private static string? CheckScale(string? config, bool hasText, decimal? valueNumber, bool hasJson, bool hasFile)
    {
        var limits = string.IsNullOrWhiteSpace(config)
            ? null
            : JsonSerializer.Deserialize<ScaleQuestionConfig>(config, Options);

        return CheckWholeNumber(limits?.Min ?? 1, limits?.Max ?? 10, hasText, valueNumber, hasJson, hasFile);
    }

    private static string? CheckWholeNumber(
        decimal min, decimal max, bool hasText, decimal? valueNumber, bool hasJson, bool hasFile)
    {
        if (valueNumber is null || hasText || hasJson || hasFile)
        {
            return "This question takes valueNumber only.";
        }

        return valueNumber == decimal.Truncate(valueNumber.Value) && valueNumber >= min && valueNumber <= max
            ? null
            : $"valueNumber must be a whole number from {min} to {max}.";
    }

    private static string? CheckFile(bool hasFile, bool hasText, decimal? valueNumber, bool hasJson) =>
        hasFile && !hasText && valueNumber is null && !hasJson
            ? null
            : "File upload takes fileUploadId only.";

    private static (HashSet<string> Options, bool AllowOther) ParseChoice(string? config)
    {
        var parsed = string.IsNullOrWhiteSpace(config)
            ? null
            : JsonSerializer.Deserialize<ChoiceQuestionConfig>(config, Options);

        var options = parsed?.Options?.OfType<string>().ToHashSet(StringComparer.Ordinal) ?? [];
        return (options, parsed?.AllowOther == true);
    }
}
