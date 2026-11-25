using FormUp.Application.Features.Questionnaires.Shared;
using FluentAssertions;

namespace FormUp.Tests.Validators;

public class AnswerValueCheckerTests
{
    private const string Choices = """{"options":["A","B","C"]}""";
    private const string ChoicesOther = """{"options":["A","B"],"allowOther":true}""";

    [Theory]
    [InlineData(QuestionTypes.ShortText, null)]
    [InlineData(QuestionTypes.Section, null)]
    [InlineData(QuestionTypes.SingleChoice, Choices)]
    public void FindProblem_BlankAnswer_IsValidForEveryType(string type, string? config)
    {
        AnswerValueChecker.FindProblem(type, config, "  ", null, "", null).Should().BeNull();
        AnswerValueChecker.IsBlank("  ", null, "", null).Should().BeTrue();
        AnswerValueChecker.IsBlankUpload("  ", null, "", null).Should().BeTrue();
    }

    [Theory]
    // single_choice
    [InlineData(QuestionTypes.SingleChoice, Choices, "A", null, null, false, true)]
    [InlineData(QuestionTypes.SingleChoice, Choices, "Z", null, null, false, false)]
    [InlineData(QuestionTypes.SingleChoice, ChoicesOther, "Z", null, null, false, true)]
    [InlineData(QuestionTypes.SingleChoice, Choices, "A", 1.0, null, false, false)]
    // multi_select
    [InlineData(QuestionTypes.MultiSelect, Choices, null, null, """["A","B"]""", false, true)]
    [InlineData(QuestionTypes.MultiSelect, Choices, null, null, "not json", false, false)]
    [InlineData(QuestionTypes.MultiSelect, Choices, null, null, """{"a":1}""", false, false)]
    [InlineData(QuestionTypes.MultiSelect, Choices, null, null, """["A",1]""", false, false)]
    [InlineData(QuestionTypes.MultiSelect, Choices, null, null, """["A","A"]""", false, false)]
    [InlineData(QuestionTypes.MultiSelect, Choices, null, null, """["Z"]""", false, false)]
    [InlineData(QuestionTypes.MultiSelect, Choices, "Other", null, """["A"]""", false, false)]
    [InlineData(QuestionTypes.MultiSelect, ChoicesOther, "Other", null, """["A"]""", false, true)]
    // short_text
    [InlineData(QuestionTypes.ShortText, null, "hello", null, null, false, true)]
    [InlineData(QuestionTypes.ShortText, null, "hello", 3.0, null, false, false)]
    [InlineData(QuestionTypes.ShortText, null, "hello", null, """["x"]""", false, false)]
    // number
    [InlineData(QuestionTypes.Number, """{"min":10,"max":20}""", null, 15.0, null, false, true)]
    [InlineData(QuestionTypes.Number, """{"min":10,"max":20}""", null, 9.0, null, false, false)]
    [InlineData(QuestionTypes.Number, """{"min":10,"max":20}""", null, 21.0, null, false, false)]
    [InlineData(QuestionTypes.Number, null, null, 12.5, null, false, true)]
    [InlineData(QuestionTypes.Number, null, "x", 12.5, null, false, false)]
    // smiley / star
    [InlineData(QuestionTypes.SmileyRating, null, null, 1.0, null, false, true)]
    [InlineData(QuestionTypes.SmileyRating, null, null, 5.0, null, false, true)]
    [InlineData(QuestionTypes.SmileyRating, null, null, 6.0, null, false, false)]
    [InlineData(QuestionTypes.SmileyRating, null, null, 0.0, null, false, false)]
    [InlineData(QuestionTypes.StarRating, null, null, 2.5, null, false, false)]
    [InlineData(QuestionTypes.StarRating, null, null, 4.0, null, false, true)]
    // scale (default 1-10, config overrides)
    [InlineData(QuestionTypes.Scale, null, null, 10.0, null, false, true)]
    [InlineData(QuestionTypes.Scale, null, null, 11.0, null, false, false)]
    [InlineData(QuestionTypes.Scale, null, null, 5.5, null, false, false)]
    [InlineData(QuestionTypes.Scale, """{"min":0,"max":5}""", null, 0.0, null, false, true)]
    [InlineData(QuestionTypes.Scale, """{"min":0,"max":5}""", null, 6.0, null, false, false)]
    // file_upload
    [InlineData(QuestionTypes.FileUpload, null, null, null, null, true, true)]
    [InlineData(QuestionTypes.FileUpload, null, "x", null, null, true, false)]
    [InlineData(QuestionTypes.FileUpload, null, null, 1.0, null, true, false)]
    [InlineData(QuestionTypes.FileUpload, null, null, null, """["x"]""", true, false)]
    [InlineData(QuestionTypes.ShortText, null, "hello", null, null, true, false)]
    [InlineData(QuestionTypes.SingleChoice, Choices, "A", null, null, true, false)]
    // section never takes an answer
    [InlineData(QuestionTypes.Section, null, "x", null, null, false, false)]
    public void FindProblem_PerType_AcceptsOnlyTheDocumentedEncoding(
        string type, string? config, string? valueText, double? valueNumber, string? valueJson, bool withUpload,
        bool expectedValid)
    {
        var problem = AnswerValueChecker.FindProblem(
            type, config, valueText, valueNumber is null ? null : (decimal)valueNumber, valueJson, withUpload ? Guid.NewGuid() : null);

        (problem is null).Should().Be(expectedValid, "problem was: {0}", problem);
    }
}
