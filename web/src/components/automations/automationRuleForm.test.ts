import { describe, expect, it } from 'vitest';
import { AutomationActionType, AutomationTriggerKind, DayOfWeek } from '@/api/automations';
import {
  formValuesToOverrideRequest,
  overrideChanged,
  overrideInitialValues,
} from '@/components/automations/automationRuleForm';

const PICKED_FORM_ID = '00000000-0000-0000-2308-0000000000f2';

const checkInRow = {
  ruleId: '00000000-0000-0000-2308-000000000001',
  actionType: AutomationActionType.SendForm,
  questionnaireId: '00000000-0000-0000-2308-0000000000f1',
  triggerKind: AutomationTriggerKind.Schedule,
  localTime: '18:00:00',
  scheduleWeekday: DayOfWeek.Thursday,
  answerWindowHours: 48,
};

describe('formValuesToOverrideRequest: the client’s check-in form', () => {
  it('seeds the pick from the row and leaves the rule’s own form alone', () => {
    const values = overrideInitialValues({ ...checkInRow, checkInQuestionnaireId: PICKED_FORM_ID });
    expect(values.checkInQuestionnaireId).toBe(PICKED_FORM_ID);
    expect(values.questionnaireId).toBe(checkInRow.questionnaireId);
  });

  it('sends the current pick on every check-in save, so a weekday-only change keeps it', () => {
    const initial = overrideInitialValues({ ...checkInRow, checkInQuestionnaireId: PICKED_FORM_ID });
    const weekdayOnly = { ...initial, scheduleWeekday: DayOfWeek.Monday };

    expect(formValuesToOverrideRequest(weekdayOnly).checkInQuestionnaireId).toBe(PICKED_FORM_ID);
    expect(overrideChanged(initial, weekdayOnly)).toBe(true);
  });

  it('sends no form when the client follows the rule, and counts a clear as a change', () => {
    const initial = overrideInitialValues({ ...checkInRow, checkInQuestionnaireId: PICKED_FORM_ID });
    const cleared = { ...initial, checkInQuestionnaireId: '' };

    expect(formValuesToOverrideRequest(cleared).checkInQuestionnaireId).toBeUndefined();
    expect(overrideChanged(initial, cleared)).toBe(true);
  });

  it('never sends a form for a rule that is not a check-in', () => {
    const photos = overrideInitialValues({
      ruleId: checkInRow.ruleId,
      actionType: AutomationActionType.RequestProgressPhotos,
      triggerKind: AutomationTriggerKind.Schedule,
    });
    // Even a stray value in the field must not reach the request: the server answers it with a 400.
    const stray = { ...photos, checkInQuestionnaireId: PICKED_FORM_ID };

    expect(formValuesToOverrideRequest(stray).checkInQuestionnaireId).toBeUndefined();
    expect(overrideChanged(photos, stray)).toBe(false);
  });
});
