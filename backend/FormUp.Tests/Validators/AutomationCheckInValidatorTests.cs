using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.Automations.CreateAutomationRule;
using FormUp.Application.Features.Automations.OverrideClientAutomation;
using FluentAssertions;
using FluentValidation.TestHelper;

namespace FormUp.Tests.Validators;

/// <summary>
/// Validator tests for the check-in fields of an automation rule and of a per-client override.
/// </summary>
public class AutomationCheckInValidatorTests
{
    private static CreateAutomationRuleRequest ValidCheckIn() => new()
    {
        Name = "Weekly check-in",
        ActionType = AutomationActionType.SendForm,
        QuestionnaireId = Guid.NewGuid(),
        TriggerKind = AutomationTriggerKind.Schedule,
        ScheduleInterval = 1,
        ScheduleUnit = AutomationScheduleUnit.Weeks,
        LocalTime = TimeSpan.FromHours(18),
        ScheduleWeekday = DayOfWeek.Thursday,
        AnswerWindowHours = 48,
        ReminderAfterHours = 24,
        NotifyCoachOnMissedWeek = true,
        SkipWhilePreviousUnanswered = false,
    };

    private static bool HasCheckInError(TestValidationResult<CreateAutomationRuleRequest> result) =>
        result.Errors.Any(e => e.ErrorCode == ErrorCodes.AutomationCheckInInvalid);

    [Fact]
    public void Validate_ValidCheckInRule_Passes() =>
        new CreateAutomationRuleValidator().TestValidate(ValidCheckIn()).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Validate_CheckInRuleWithoutReminder_Passes()
    {
        var request = ValidCheckIn();
        request.ReminderAfterHours = null;

        new CreateAutomationRuleValidator().TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_UnknownWeekday_Fails()
    {
        var request = ValidCheckIn();
        request.ScheduleWeekday = (DayOfWeek)9;

        HasCheckInError(new CreateAutomationRuleValidator().TestValidate(request)).Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(36)]
    [InlineData(192)]
    public void Validate_AnswerWindowOutsideTheOfferedOptions_Fails(int hours)
    {
        var request = ValidCheckIn();
        request.AnswerWindowHours = hours;

        HasCheckInError(new CreateAutomationRuleValidator().TestValidate(request)).Should().BeTrue();
    }

    [Theory]
    [InlineData(48, 12)]
    [InlineData(24, 24)]
    [InlineData(48, 6)]
    [InlineData(48, 48)]
    public void Validate_ReminderNotMatchingTheWindow_Fails(int windowHours, int reminderHours)
    {
        var request = ValidCheckIn();
        request.AnswerWindowHours = windowHours;
        request.ReminderAfterHours = reminderHours;

        HasCheckInError(new CreateAutomationRuleValidator().TestValidate(request)).Should().BeTrue();
    }

    [Theory]
    [InlineData(24, 12)]
    [InlineData(48, 24)]
    [InlineData(168, 24)]
    public void Validate_WindowWithItsReminder_Passes(int windowHours, int reminderHours)
    {
        var request = ValidCheckIn();
        request.AnswerWindowHours = windowHours;
        request.ReminderAfterHours = reminderHours;

        new CreateAutomationRuleValidator().TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_OverrideWithBadCheckInFields_Fails()
    {
        var request = new OverrideClientAutomationRequest
        {
            ClientId = Guid.NewGuid(),
            RuleId = Guid.NewGuid(),
            TriggerKind = AutomationTriggerKind.Schedule,
            ScheduleInterval = 1,
            ScheduleUnit = AutomationScheduleUnit.Weeks,
            LocalTime = TimeSpan.FromHours(8),
            ScheduleWeekday = (DayOfWeek)9,
            AnswerWindowHours = 12,
        };

        var result = new OverrideClientAutomationValidator().TestValidate(request);

        result.Errors.Count(e => e.ErrorCode == ErrorCodes.AutomationCheckInInvalid).Should().Be(2);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Validate_OverrideCheckInFormId_RejectsOnlyAnEmptyGuid(bool isEmptyGuid, bool passes)
    {
        var request = new OverrideClientAutomationRequest
        {
            ClientId = Guid.NewGuid(),
            RuleId = Guid.NewGuid(),
            TriggerKind = AutomationTriggerKind.Schedule,
            ScheduleInterval = 1,
            ScheduleUnit = AutomationScheduleUnit.Weeks,
            LocalTime = TimeSpan.FromHours(8),
            CheckInQuestionnaireId = isEmptyGuid ? Guid.Empty : Guid.NewGuid(),
        };

        var result = new OverrideClientAutomationValidator().TestValidate(request);

        if (passes)
        {
            result.ShouldNotHaveAnyValidationErrors();
        }
        else
        {
            result.IsValid.Should().BeFalse();
        }
    }
}
