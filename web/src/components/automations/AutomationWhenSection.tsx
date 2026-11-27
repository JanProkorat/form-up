import { useFormContext, useWatch } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { Clock, Flag } from 'lucide-react';
import { Input } from '@/components/ui/input';
import {
  type AutomationRuleDto,
  AutomationEventType,
  AutomationOffsetDirection,
  AutomationScheduleUnit,
  AutomationTriggerKind,
} from '@/api/automations';
import {
  FIELD_CLASS,
  ChoiceButton,
  FieldError,
  SELECT_CLASS,
  SectionHeading,
  SelectChevron,
} from '@/components/automations/automationFormParts';
import {
  CHECK_IN_WINDOW_DAYS,
  DEFAULT_EVENT_TIME,
  EVENT_TYPES,
  isPlanEvent,
  WEEKDAYS,
  type AutomationRuleFormValues,
} from '@/components/automations/automationRuleForm';
import { cn } from '@/lib/utils';

/** Section 1: schedule or event trigger, with its timing fields. */
export default function AutomationWhenSection({ parentRule }: { parentRule?: AutomationRuleDto }) {
  const { t } = useTranslation();
  const ruleWeekday = parentRule?.scheduleWeekday;
  const ruleWindowDays =
    parentRule?.answerWindowHours != null ? Math.round(parentRule.answerWindowHours / 24) : undefined;
  const { register, setValue, control, getValues } = useFormContext<AutomationRuleFormValues>();
  const triggerKind = useWatch({ control, name: 'triggerKind' });
  const eventType = useWatch({ control, name: 'eventType' });
  const checkIn = useWatch({ control, name: 'checkIn' });
  const scheduleWeekday = useWatch({ control, name: 'scheduleWeekday' });
  const isSchedule = checkIn || triggerKind === AutomationTriggerKind.Schedule;
  const planEvent = isPlanEvent(eventType);

  const chooseKind = (kind: AutomationTriggerKind) => {
    setValue('triggerKind', kind, { shouldDirty: true });
    if (getValues('localTime') === '') {
      setValue('localTime', DEFAULT_EVENT_TIME, { shouldDirty: true });
    }
  };

  const timeField = (
    <label className={FIELD_CLASS}>
      <span>{t('automations.drawer.when.at')}</span>
      <Input type="time" step={60} {...register('localTime')} aria-label={t('automations.drawer.when.at')} />
      <span className="text-meta font-normal text-muted-foreground">{t('automations.drawer.when.clientTime')}</span>
      <FieldError name="localTime" />
    </label>
  );

  return (
    <section className="flex flex-col gap-3">
      <SectionHeading icon={isSchedule ? <Clock className="size-4" /> : <Flag className="size-4" />}>
        {t('automations.drawer.when.heading')}
      </SectionHeading>
      <div role="group" aria-label={t('automations.drawer.when.heading')} className="grid grid-cols-2 gap-2">
        <ChoiceButton
          selected={isSchedule}
          onClick={() => chooseKind(AutomationTriggerKind.Schedule)}
          icon={<Clock className="size-4" />}
        >
          {t('automations.drawer.when.schedule')}
        </ChoiceButton>
        <ChoiceButton
          selected={!isSchedule}
          disabled={checkIn}
          onClick={() => chooseKind(AutomationTriggerKind.Event)}
          icon={<Flag className="size-4" />}
        >
          {t('automations.drawer.when.event')}
        </ChoiceButton>
      </div>

      {checkIn ? (
        <>
          <div className={FIELD_CLASS}>
            <span id="check-in-weekday-label">{t('automations.drawer.when.weekdayLabel')}</span>
            <div
              role="group"
              aria-labelledby="check-in-weekday-label"
              className="grid grid-cols-7 gap-1.5"
            >
              {WEEKDAYS.map((day) => (
                <button
                  key={day}
                  type="button"
                  aria-pressed={scheduleWeekday === day}
                  onClick={() => setValue('scheduleWeekday', day, { shouldDirty: true })}
                  className={cn(
                    'flex h-11 min-w-0 items-center justify-center rounded-field border px-1 text-body font-semibold outline-none transition-colors focus-visible:ring-3 focus-visible:ring-ring/50',
                    scheduleWeekday === day
                      ? 'border-success bg-success-soft text-success-ink'
                      : 'border-input bg-background text-foreground hover:bg-sunken',
                  )}
                >
                  {t(`automations.weekday.short.${day}`)}
                </button>
              ))}
            </div>
            {ruleWeekday && (
              <span className="text-meta font-normal text-muted-foreground">
                {t('automations.drawer.when.weekdayRuleHint', { day: t(`automations.weekday.every.${ruleWeekday}`) })}
              </span>
            )}
          </div>
          <div className="grid grid-cols-2 gap-3">
            {timeField}
            <label className={FIELD_CLASS}>
              <span>{t('automations.drawer.when.answerWithin')}</span>
              <div className="relative">
                <select {...register('answerWindowDays')} className={SELECT_CLASS}>
                  {CHECK_IN_WINDOW_DAYS.map((days) => (
                    <option key={days} value={String(days)}>
                      {t('automations.drawer.when.answerWindowDays', { count: days })}
                    </option>
                  ))}
                </select>
                <SelectChevron />
              </div>
              <span className="text-meta font-normal text-muted-foreground">
                {ruleWindowDays !== undefined
                  ? t('automations.drawer.when.answerWindowRuleHint', {
                      window: t('automations.drawer.when.answerWindowDays', { count: ruleWindowDays }),
                    })
                  : t('automations.drawer.when.answerWindowHint')}
              </span>
            </label>
          </div>
          <div className={FIELD_CLASS}>
            <span>{t('automations.drawer.when.starting')}</span>
            <p className="rounded-field border border-line bg-sunken px-3 py-2.5 text-copy font-normal text-ink-2">
              {t('automations.drawer.when.startingCheckIn')}
            </p>
          </div>
        </>
      ) : isSchedule ? (
        <>
          <div className="grid grid-cols-3 gap-3">
            <label className={FIELD_CLASS}>
              <span>{t('automations.drawer.when.every')}</span>
              <Input type="number" inputMode="numeric" min={1} max={365} {...register('scheduleInterval')} />
            </label>
            <label className={FIELD_CLASS}>
              <span>{t('automations.drawer.when.unit')}</span>
              <div className="relative">
                <select {...register('scheduleUnit')} className={SELECT_CLASS}>
                  <option value={AutomationScheduleUnit.Days}>{t('automations.drawer.when.days')}</option>
                  <option value={AutomationScheduleUnit.Weeks}>{t('automations.drawer.when.weeks')}</option>
                </select>
                <SelectChevron />
              </div>
            </label>
            {timeField}
          </div>
          <FieldError name="scheduleInterval" />
          <div className={FIELD_CLASS}>
            <span>{t('automations.drawer.when.starting')}</span>
            <p className="rounded-field border border-line bg-sunken px-3 py-2.5 text-copy font-normal text-ink-2">
              {t('automations.drawer.when.startingFixed')}
            </p>
          </div>
        </>
      ) : (
        <>
          <label className={FIELD_CLASS}>
            <span>{t('automations.drawer.when.eventLabel')}</span>
            <div className="relative">
              <select
                {...register('eventType', {
                  onChange: (event) => {
                    if (!isPlanEvent(event.target.value as AutomationEventType)) {
                      setValue('oncePerPlan', false, { shouldDirty: true });
                      setValue('eventOffsetDirection', AutomationOffsetDirection.After, { shouldDirty: true });
                    }
                  },
                })}
                className={SELECT_CLASS}
              >
                {EVENT_TYPES.map((value) => (
                  <option key={value} value={value}>
                    {t(`automations.events.${value}`)}
                  </option>
                ))}
              </select>
              <SelectChevron />
            </div>
          </label>
          <div className="grid grid-cols-3 gap-3">
            <label className={FIELD_CLASS}>
              <span>{t('automations.drawer.when.timing')}</span>
              <Input type="number" inputMode="numeric" min={0} max={365} {...register('eventOffsetDays')} />
            </label>
            <label className={FIELD_CLASS}>
              <span>{t('automations.drawer.when.unit')}</span>
              <div className="relative">
                <select {...register('eventOffsetDirection')} className={SELECT_CLASS}>
                  <option value={AutomationOffsetDirection.After}>{t('automations.drawer.when.daysAfter')}</option>
                  {planEvent && (
                    <option value={AutomationOffsetDirection.Before}>{t('automations.drawer.when.daysBefore')}</option>
                  )}
                </select>
                <SelectChevron />
              </div>
            </label>
            {timeField}
          </div>
          <FieldError name="eventOffsetDays" />
        </>
      )}
    </section>
  );
}
