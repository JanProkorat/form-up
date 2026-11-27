import { z } from 'zod';
import {
  AutomationActionType,
  AutomationAudience,
  AutomationEventType,
  AutomationOffsetDirection,
  AutomationScheduleUnit,
  AutomationTriggerKind,
  DayOfWeek,
  type AutomationRuleDto,
  type ClientAutomationItem,
  type OverrideClientAutomationRequest,
  type PauseClientAutomationRequest,
  type UpdateAutomationRuleRequest,
} from '@/api/automations';
import { isCheckInRule } from '@/components/automations/automationFormat';

export const NAME_MAX = 100;
export const PAUSE_NOTE_MAX = 200;
export const MESSAGE_MAX = 1000;
export const PHOTO_MESSAGE_MAX = 500;
export const DEFAULT_EVENT_TIME = '09:00';
export const DEFAULT_SCHEDULE_TIME = '09:00';
export const STOP_AFTER_DEFAULT = '5';
export const CHECK_IN_DEFAULT_WINDOW_DAYS = 2;
/** The answer window a coach can pick, in days. */
export const CHECK_IN_WINDOW_DAYS: readonly number[] = [1, 2, 3, 4, 5, 6, 7];

/** The reminder goes out after a day, or at half the window when the window is a single day. */
export function checkInReminderHours(windowHours: number): number {
  return Math.min(24, windowHours / 2);
}

/** The weekday buttons of a check-in rule, Monday first. */
export const WEEKDAYS: readonly DayOfWeek[] = [
  DayOfWeek.Monday,
  DayOfWeek.Tuesday,
  DayOfWeek.Wednesday,
  DayOfWeek.Thursday,
  DayOfWeek.Friday,
  DayOfWeek.Saturday,
  DayOfWeek.Sunday,
];

/** Where the drawer is used: the global page, a client's own rule, or a client's override of a global rule. */
export type AutomationDrawerVariant = 'global' | 'clientOnly' | 'override';

export const PLAN_EVENTS: readonly AutomationEventType[] = [
  AutomationEventType.PlanStarted,
  AutomationEventType.PlanEnded,
];

/** Events a coach can pick; payment events do not exist in this set. */
export const EVENT_TYPES: readonly AutomationEventType[] = [
  AutomationEventType.ClientJoined,
  AutomationEventType.PlanStarted,
  AutomationEventType.PlanEnded,
  AutomationEventType.CheckInMissed,
  AutomationEventType.ClientInactive,
  AutomationEventType.FormAnswered,
];

export const isPlanEvent = (eventType: AutomationEventType): boolean => PLAN_EVENTS.includes(eventType);

/** The message limit follows the action: progress photos allow fewer characters. */
export function messageLimitFor(actionType: AutomationActionType): number {
  return actionType === AutomationActionType.RequestProgressPhotos ? PHOTO_MESSAGE_MAX : MESSAGE_MAX;
}

const TIME_PATTERN = /^([01]\d|2[0-3]):[0-5]\d$/;

const INT_PATTERN = /^\d+$/;

/** Today in the browser's time zone as yyyy-MM-dd, the format the pause date is sent in. */
export function todayIsoDate(): string {
  const now = new Date();
  const month = String(now.getMonth() + 1).padStart(2, '0');
  const day = String(now.getDate()).padStart(2, '0');
  return `${now.getFullYear()}-${month}-${day}`;
}

const numberInRange = (value: string, min: number, max: number): boolean => {
  if (!INT_PATTERN.test(value.trim())) {
    return false;
  }
  const parsed = Number(value);
  return parsed >= min && parsed <= max;
};

export const automationRuleSchema = z
  .object({
    name: z.string(),
    actionType: z.enum(AutomationActionType),
    questionnaireId: z.string(),
    /** Client override of a check-in rule: the form this client gets instead of the rule's; empty follows the rule. */
    checkInQuestionnaireId: z.string(),
    message: z.string(),
    triggerKind: z.enum(AutomationTriggerKind),
    scheduleInterval: z.string(),
    scheduleUnit: z.enum(AutomationScheduleUnit),
    localTime: z.string(),
    /** The rule sends a CheckIn form: weekly on a weekday, with an answer window and its own options. */
    checkIn: z.boolean(),
    scheduleWeekday: z.enum(DayOfWeek),
    /** Days the client has to answer, as the select's string value. */
    answerWindowDays: z.string().refine((value) => numberInRange(value, 1, 7)),
    reminderEnabled: z.boolean(),
    notifyCoachOnMissedWeek: z.boolean(),
    eventType: z.enum(AutomationEventType),
    eventOffsetDays: z.string(),
    eventOffsetDirection: z.enum(AutomationOffsetDirection),
    audience: z.enum(AutomationAudience),
    audienceTagId: z.string(),
    audienceClientIds: z.array(z.string()),
    skipWhilePreviousUnanswered: z.boolean(),
    stopAfterEnabled: z.boolean(),
    stopAfterSends: z.string(),
    oncePerPlan: z.boolean(),
    enabled: z.boolean(),
    /** Client override only: pause the rule for this client. */
    paused: z.boolean(),
    /** yyyy-MM-dd, or empty to pause until resumed. */
    pausedUntil: z.string(),
    pauseNote: z.string(),
  })
  .superRefine((values, ctx) => {
    const fail = (path: keyof AutomationRuleFormValues, message: string) =>
      ctx.addIssue({ code: 'custom', path: [path], message });

    const name = values.name.trim();
    if (name.length === 0) {
      fail('name', 'automations.errors.nameRequired');
    } else if (name.length > NAME_MAX) {
      fail('name', 'automations.errors.nameTooLong');
    }

    if (values.message.length > messageLimitFor(values.actionType)) {
      fail(
        'message',
        values.actionType === AutomationActionType.RequestProgressPhotos
          ? 'apiErrors.AUTOMATION_PHOTO_MESSAGE_TOO_LONG'
          : 'automations.errors.messageTooLong',
      );
    }

    if (values.actionType === AutomationActionType.SendForm && values.questionnaireId === '') {
      fail('questionnaireId', 'automations.errors.formRequired');
    }

    if (values.checkIn) {
      if (!TIME_PATTERN.test(values.localTime)) {
        fail('localTime', 'automations.errors.timeRequired');
      }
    } else if (values.triggerKind === AutomationTriggerKind.Schedule) {
      if (!numberInRange(values.scheduleInterval, 1, 365)) {
        fail('scheduleInterval', 'automations.errors.intervalRange');
      }
      if (!TIME_PATTERN.test(values.localTime)) {
        fail('localTime', 'automations.errors.timeRequired');
      }
    } else {
      const minOffset = values.eventType === AutomationEventType.ClientInactive ? 1 : 0;
      if (!numberInRange(values.eventOffsetDays, minOffset, 365)) {
        fail(
          'eventOffsetDays',
          minOffset === 1 ? 'automations.errors.offsetInactiveRange' : 'automations.errors.offsetRange',
        );
      }
      if (values.localTime !== '' && !TIME_PATTERN.test(values.localTime)) {
        fail('localTime', 'automations.errors.timeInvalid');
      }
    }

    if (!values.checkIn && values.stopAfterEnabled && !numberInRange(values.stopAfterSends, 1, 1000)) {
      fail('stopAfterSends', 'automations.errors.stopAfterRange');
    }

    if (!values.paused && values.pausedUntil !== '') {
      fail('pausedUntil', 'apiErrors.AUTOMATION_PAUSE_DETAILS_WITHOUT_PAUSE');
    } else if (values.paused && values.pausedUntil !== '' && values.pausedUntil < todayIsoDate()) {
      fail('pausedUntil', 'apiErrors.AUTOMATION_PAUSED_UNTIL_IN_PAST');
    }
    if (!values.paused && values.pauseNote.trim() !== '') {
      fail('pauseNote', 'apiErrors.AUTOMATION_PAUSE_DETAILS_WITHOUT_PAUSE');
    } else if (values.pauseNote.trim().length > PAUSE_NOTE_MAX) {
      fail('pauseNote', 'automations.errors.pauseNoteTooLong');
    }

    if (values.audience === AutomationAudience.Tag && values.audienceTagId === '') {
      fail('audienceTagId', 'automations.errors.tagRequired');
    }
    if (values.audience === AutomationAudience.Chosen && values.audienceClientIds.length === 0) {
      fail('audienceClientIds', 'automations.errors.clientsRequired');
    }
  });

export type AutomationRuleFormValues = z.infer<typeof automationRuleSchema>;

/** Fields that decide who is covered and when, so the saved-rule summary stops describing them once edited. */
export const SUMMARY_SENSITIVE_FIELDS = [
  'triggerKind',
  'scheduleInterval',
  'scheduleUnit',
  'localTime',
  'checkIn',
  'scheduleWeekday',
  'answerWindowDays',
  'eventType',
  'eventOffsetDays',
  'eventOffsetDirection',
  'audience',
  'audienceTagId',
  'audienceClientIds',
] as const satisfies readonly (keyof AutomationRuleFormValues)[];

export function summarySensitiveSnapshot(values: AutomationRuleFormValues): string {
  return JSON.stringify(SUMMARY_SENSITIVE_FIELDS.map((field) => values[field]));
}

export function defaultRuleValues(): AutomationRuleFormValues {
  return {
    name: '',
    actionType: AutomationActionType.SendForm,
    questionnaireId: '',
    checkInQuestionnaireId: '',
    message: '',
    triggerKind: AutomationTriggerKind.Schedule,
    scheduleInterval: '1',
    scheduleUnit: AutomationScheduleUnit.Weeks,
    localTime: DEFAULT_SCHEDULE_TIME,
    checkIn: false,
    scheduleWeekday: DayOfWeek.Monday,
    answerWindowDays: String(CHECK_IN_DEFAULT_WINDOW_DAYS),
    reminderEnabled: true,
    notifyCoachOnMissedWeek: true,
    eventType: AutomationEventType.ClientJoined,
    eventOffsetDays: '0',
    eventOffsetDirection: AutomationOffsetDirection.After,
    audience: AutomationAudience.All,
    audienceTagId: '',
    audienceClientIds: [],
    skipWhilePreviousUnanswered: true,
    stopAfterEnabled: false,
    stopAfterSends: STOP_AFTER_DEFAULT,
    oncePerPlan: false,
    enabled: true,
    paused: false,
    pausedUntil: '',
    pauseNote: '',
  };
}

/** The server sends a time of day as HH:mm:ss; the form works in HH:mm. */
function toFormTime(time: string | undefined): string {
  return time ? time.slice(0, 5) : '';
}

export function ruleToFormValues(rule: AutomationRuleDto): AutomationRuleFormValues {
  const defaults = defaultRuleValues();
  const isEvent = rule.triggerKind === AutomationTriggerKind.Event;
  const audience = rule.audience ?? AutomationAudience.All;
  const checkIn = isCheckInRule(rule);
  return {
    ...defaults,
    checkIn,
    scheduleWeekday: rule.scheduleWeekday ?? defaults.scheduleWeekday,
    answerWindowDays:
      checkIn && rule.answerWindowHours != null
        ? String(Math.round(rule.answerWindowHours / 24))
        : defaults.answerWindowDays,
    reminderEnabled: checkIn ? rule.reminderAfterHours != null : defaults.reminderEnabled,
    notifyCoachOnMissedWeek: checkIn ? (rule.notifyCoachOnMissedWeek ?? false) : defaults.notifyCoachOnMissedWeek,
    name: rule.name ?? '',
    actionType: rule.actionType ?? defaults.actionType,
    questionnaireId: rule.questionnaireId ?? '',
    message: rule.message ?? '',
    triggerKind: rule.triggerKind ?? defaults.triggerKind,
    scheduleInterval: rule.scheduleInterval != null ? String(rule.scheduleInterval) : defaults.scheduleInterval,
    scheduleUnit: rule.scheduleUnit ?? defaults.scheduleUnit,
    localTime: isEvent ? toFormTime(rule.localTime) || DEFAULT_EVENT_TIME : toFormTime(rule.localTime),
    eventType: rule.eventType ?? defaults.eventType,
    eventOffsetDays: rule.eventOffsetDays != null ? String(rule.eventOffsetDays) : defaults.eventOffsetDays,
    eventOffsetDirection: rule.eventOffsetDirection ?? defaults.eventOffsetDirection,
    audience,
    audienceTagId: audience === AutomationAudience.Tag ? (rule.tag?.tagId ?? '') : '',
    audienceClientIds:
      audience === AutomationAudience.Chosen ? (rule.clients ?? []).flatMap((c) => (c.clientId ? [c.clientId] : [])) : [],
    skipWhilePreviousUnanswered: rule.skipWhilePreviousUnanswered ?? defaults.skipWhilePreviousUnanswered,
    stopAfterEnabled: rule.stopAfterSends != null,
    stopAfterSends: rule.stopAfterSends != null ? String(rule.stopAfterSends) : STOP_AFTER_DEFAULT,
    oncePerPlan: rule.oncePerPlan ?? false,
    enabled: rule.enabled ?? true,
  };
}

/**
 * Builds the save request from the form. The audience is left out for a client-only rule,
 * where the backend requires it to be null.
 */
export function formValuesToRequest(
  values: AutomationRuleFormValues,
  variant: AutomationDrawerVariant = 'global',
): UpdateAutomationRuleRequest {
  const checkIn = values.checkIn && values.actionType === AutomationActionType.SendForm;
  const isSchedule = checkIn || values.triggerKind === AutomationTriggerKind.Schedule;
  const planEvent = !isSchedule && isPlanEvent(values.eventType);
  const answerWindowHours = Number(values.answerWindowDays) * 24;
  const request: UpdateAutomationRuleRequest = {
    name: values.name.trim(),
    actionType: values.actionType,
    questionnaireId: values.actionType === AutomationActionType.SendForm ? values.questionnaireId : undefined,
    message: values.message.trim() === '' ? undefined : values.message,
    triggerKind: checkIn ? AutomationTriggerKind.Schedule : values.triggerKind,
    scheduleInterval: checkIn ? 1 : isSchedule ? Number(values.scheduleInterval) : undefined,
    scheduleUnit: checkIn ? AutomationScheduleUnit.Weeks : isSchedule ? values.scheduleUnit : undefined,
    localTime: values.localTime === '' ? undefined : values.localTime,
    // Check-in fields go out for a check-in rule only; the backend refuses them on any other rule.
    scheduleWeekday: checkIn ? values.scheduleWeekday : undefined,
    answerWindowHours: checkIn ? answerWindowHours : undefined,
    reminderAfterHours: checkIn && values.reminderEnabled ? checkInReminderHours(answerWindowHours) : undefined,
    notifyCoachOnMissedWeek: checkIn ? values.notifyCoachOnMissedWeek : undefined,
    eventType: isSchedule ? undefined : values.eventType,
    eventOffsetDays: isSchedule ? undefined : Number(values.eventOffsetDays),
    eventOffsetDirection: isSchedule ? undefined : planEvent ? values.eventOffsetDirection : AutomationOffsetDirection.After,
    oncePerPlan: planEvent && values.oncePerPlan,
    // A check-in week stands on its own, so the skip option is off and there is no send limit.
    skipWhilePreviousUnanswered: checkIn ? false : values.skipWhilePreviousUnanswered,
    stopAfterSends: !checkIn && values.stopAfterEnabled ? Number(values.stopAfterSends) : undefined,
    enabled: values.enabled,
  };

  if (variant === 'global') {
    request.audience = values.audience;
    request.audienceTagId = values.audience === AutomationAudience.Tag ? values.audienceTagId : undefined;
    request.audienceClientIds = values.audience === AutomationAudience.Chosen ? values.audienceClientIds : [];
  }
  return request;
}

/** The pause state of a client's row, used to prefill the override drawer. */
export interface PauseState {
  isPaused?: boolean;
  pausedUntil?: string;
  pauseNote?: string;
}

/** Pause form fields from a row's pause state; a date-only or date-time value is cut to yyyy-MM-dd. */
export function pauseToFormValues(
  pause: PauseState | undefined,
): Pick<AutomationRuleFormValues, 'paused' | 'pausedUntil' | 'pauseNote'> {
  return {
    paused: pause?.isPaused === true,
    pausedUntil: pause?.isPaused === true ? (pause.pausedUntil ?? '').slice(0, 10) : '',
    pauseNote: pause?.isPaused === true ? (pause.pauseNote ?? '') : '',
  };
}

/** A client's effective row as a rule, so the drawer and the row formatters can read it. */
export function clientItemToRule(item: ClientAutomationItem): AutomationRuleDto {
  return {
    ruleId: item.ruleId,
    name: item.name,
    message: item.message,
    actionType: item.actionType,
    questionnaireId: item.questionnaireId,
    triggerKind: item.triggerKind,
    scheduleInterval: item.scheduleInterval,
    scheduleUnit: item.scheduleUnit,
    localTime: item.localTime,
    scheduleWeekday: item.scheduleWeekday,
    answerWindowHours: item.answerWindowHours,
    reminderAfterHours: item.reminderAfterHours,
    notifyCoachOnMissedWeek: item.notifyCoachOnMissedWeek,
    eventType: item.eventType,
    eventOffsetDays: item.eventOffsetDays,
    eventOffsetDirection: item.eventOffsetDirection,
    oncePerPlan: item.oncePerPlan,
    skipWhilePreviousUnanswered: item.skipWhilePreviousUnanswered,
    stopAfterSends: item.stopAfterSends,
    enabled: item.enabled,
  };
}

/** The values the override drawer starts from: the client's effective row, its pause and its own check-in form. */
export function overrideInitialValues(item: ClientAutomationItem): AutomationRuleFormValues {
  return {
    ...ruleToFormValues(clientItemToRule(item)),
    ...pauseToFormValues(item),
    checkInQuestionnaireId: item.checkInQuestionnaireId ?? '',
  };
}

/**
 * The customisable part only: message, trigger, options and, for a check-in rule, the client's own form.
 * Name, action and audience stay with the global rule.
 */
export function formValuesToOverrideRequest(values: AutomationRuleFormValues): OverrideClientAutomationRequest {
  const full = formValuesToRequest(values, 'clientOnly');
  return {
    // The server reads a missing form as "back to the rule's form", so a check-in save always carries the pick.
    // Any other rule must not send it at all (400 AUTOMATION_CHECK_IN_FIELDS_NOT_ALLOWED).
    checkInQuestionnaireId: values.checkIn && values.checkInQuestionnaireId !== '' ? values.checkInQuestionnaireId : undefined,
    message: full.message,
    triggerKind: full.triggerKind,
    scheduleInterval: full.scheduleInterval,
    scheduleUnit: full.scheduleUnit,
    localTime: full.localTime,
    scheduleWeekday: full.scheduleWeekday,
    answerWindowHours: full.answerWindowHours,
    reminderAfterHours: full.reminderAfterHours,
    notifyCoachOnMissedWeek: full.notifyCoachOnMissedWeek,
    eventType: full.eventType,
    eventOffsetDays: full.eventOffsetDays,
    eventOffsetDirection: full.eventOffsetDirection,
    oncePerPlan: full.oncePerPlan,
    skipWhilePreviousUnanswered: full.skipWhilePreviousUnanswered,
    stopAfterSends: full.stopAfterSends,
    enabled: full.enabled,
  };
}

/** Resume sends only the flag; the server clears the date and note. */
export function formValuesToPauseRequest(values: AutomationRuleFormValues): PauseClientAutomationRequest {
  if (!values.paused) {
    return { isPaused: false };
  }
  const note = values.pauseNote.trim();
  return {
    isPaused: true,
    pausedUntil: values.pausedUntil === '' ? undefined : values.pausedUntil,
    pauseNote: note === '' ? undefined : note,
  };
}

/** Whether message, trigger or options differ, i.e. whether an override PUT is needed. */
export function overrideChanged(initial: AutomationRuleFormValues, current: AutomationRuleFormValues): boolean {
  return JSON.stringify(formValuesToOverrideRequest(initial)) !== JSON.stringify(formValuesToOverrideRequest(current));
}

/** Whether the pause fields differ, i.e. whether a pause PUT is needed. */
export function pauseChanged(initial: AutomationRuleFormValues, current: AutomationRuleFormValues): boolean {
  return JSON.stringify(formValuesToPauseRequest(initial)) !== JSON.stringify(formValuesToPauseRequest(current));
}

/** A whole-rule PUT that flips only the enabled flag; the audience is omitted so the server keeps it. */
export function ruleToToggleRequest(rule: AutomationRuleDto, enabled: boolean): UpdateAutomationRuleRequest {
  const values = { ...ruleToFormValues(rule), enabled };
  const request = formValuesToRequest(values, 'clientOnly');
  // Keep fields exactly as stored; a stored empty time on an event rule stays empty.
  if (rule.triggerKind === AutomationTriggerKind.Event && !rule.localTime) {
    request.localTime = undefined;
  }
  if (isCheckInRule(rule)) {
    request.answerWindowHours = rule.answerWindowHours ?? CHECK_IN_DEFAULT_WINDOW_DAYS * 24;
    request.reminderAfterHours = rule.reminderAfterHours;
  }
  return request;
}
