import { useEffect, useMemo } from 'react';
import { Controller, useFormContext, useWatch } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { Camera, FileText, Ruler, Send } from 'lucide-react';
import { Checkbox } from '@/components/ui/checkbox';
import { Switch } from '@/components/ui/switch';
import { Textarea } from '@/components/ui/textarea';
import { AutomationActionType, AutomationScheduleUnit, AutomationTriggerKind } from '@/api/automations';
import { QuestionnaireType } from '@/api/questionnaires';
import { useAutomationRules } from '@/hooks/useAutomationQueries';
import { useTrainerQuestionnaires } from '@/hooks/useQuestionnaireQueries';
import { useSubscription } from '@/hooks/useSubscriptionQueries';
import { isCheckInRule } from '@/components/automations/automationFormat';
import {
  FIELD_CLASS,
  ChoiceButton,
  FieldError,
  SELECT_CLASS,
  SectionHeading,
  SelectChevron,
} from '@/components/automations/automationFormParts';
import {
  checkInReminderHours,
  DEFAULT_SCHEDULE_TIME,
  messageLimitFor,
  type AutomationRuleFormValues,
} from '@/components/automations/automationRuleForm';

const ACTIONS = [
  { value: AutomationActionType.SendForm, icon: <FileText className="size-4" />, labelKey: 'sendForm' },
  { value: AutomationActionType.RequestProgressPhotos, icon: <Camera className="size-4" />, labelKey: 'requestPhotos' },
  { value: AutomationActionType.RequestMeasurements, icon: <Ruler className="size-4" />, labelKey: 'requestMeasurements' },
] as const;

interface Props {
  /** Override variant: the action belongs to the parent rule and cannot be changed. */
  actionLocked?: boolean;
  /** Editing a saved rule: the action tiles show the chosen action but cannot change it. */
  actionReadOnly?: boolean;
  /** Whether check-in forms can be picked; a rule for one client never offers them. */
  checkInFormsAllowed?: boolean;
  /** The saved rule being edited, so its own enabled state does not count as "another" check-in rule. */
  editingRuleId?: string;
}

/** Section 2: the action, its form and the message. The message limit follows the action. */
export default function AutomationWhatSection({
  actionLocked = false,
  actionReadOnly = false,
  checkInFormsAllowed = true,
  editingRuleId,
}: Props) {
  const { t } = useTranslation();
  const { register, setValue, trigger, control, getValues } = useFormContext<AutomationRuleFormValues>();
  const actionType = useWatch({ control, name: 'actionType' });
  const message = useWatch({ control, name: 'message' }) ?? '';
  const questionnaireId = useWatch({ control, name: 'questionnaireId' });
  const checkIn = useWatch({ control, name: 'checkIn' });
  const answerWindowDays = useWatch({ control, name: 'answerWindowDays' });
  const reminderHours = checkInReminderHours(Number(answerWindowDays) * 24);
  const limit = messageLimitFor(actionType);
  // An automation sends an active onboarding or check-in form. One enabled check-in rule is allowed per coach,
  // so check-in forms are hidden while another enabled check-in rule exists.
  const formsQuery = useTrainerQuestionnaires();
  const rulesQuery = useAutomationRules();
  // The weekly check-in rule type needs the plan feature; the other rule types stay usable.
  const { isEntitled } = useSubscription();
  const checkInsLocked = !isEntitled('canUseWeeklyCheckIns');
  const otherCheckInRuleEnabled = (rulesQuery.data?.rules ?? []).some(
    (rule) => isCheckInRule(rule) && rule.enabled === true && rule.ruleId !== editingRuleId,
  );
  const forms = useMemo(
    () =>
      (formsQuery.data ?? []).filter((form) => {
        if (!form.isActive) return false;
        if (form.type === QuestionnaireType.Onboarding) return true;
        if (form.type !== QuestionnaireType.CheckIn) return false;
        return form.publicId === questionnaireId || (checkInFormsAllowed && !otherCheckInRuleEnabled);
      }),
    [formsQuery.data, checkInFormsAllowed, otherCheckInRuleEnabled, questionnaireId],
  );
  const loadedForms = formsQuery.isSuccess && !formsQuery.isPlaceholderData ? forms : undefined;
  const selectedForm = forms.find((form) => form.publicId === questionnaireId);

  // A saved rule may point at a form that is no longer offered; clear it so the required-form check shows.
  useEffect(() => {
    if (!loadedForms || actionLocked || actionType !== AutomationActionType.SendForm) return;
    const current = getValues('questionnaireId');
    if (current !== '' && !loadedForms.some((form) => form.publicId === current)) {
      setValue('questionnaireId', '', { shouldDirty: true });
      void trigger('questionnaireId');
    }
  }, [loadedForms, actionLocked, actionType, getValues, setValue, trigger]);

  /** Picking a check-in form switches the drawer to check-in mode; any other form switches it back. */
  const applyForm = (publicId: string) => {
    const isCheckIn = forms.find((form) => form.publicId === publicId)?.type === QuestionnaireType.CheckIn;
    if (isCheckIn === getValues('checkIn')) return;
    setValue('checkIn', isCheckIn, { shouldDirty: true });
    if (isCheckIn) {
      setValue('triggerKind', AutomationTriggerKind.Schedule, { shouldDirty: true });
      setValue('scheduleInterval', '1', { shouldDirty: true });
      setValue('scheduleUnit', AutomationScheduleUnit.Weeks, { shouldDirty: true });
      setValue('skipWhilePreviousUnanswered', false, { shouldDirty: true });
      setValue('stopAfterEnabled', false, { shouldDirty: true });
      if (getValues('localTime') === '') {
        setValue('localTime', DEFAULT_SCHEDULE_TIME, { shouldDirty: true });
      }
    } else {
      setValue('skipWhilePreviousUnanswered', true, { shouldDirty: true });
    }
  };

  const chooseAction = (value: AutomationActionType) => {
    setValue('actionType', value, { shouldDirty: true });
    if (value !== AutomationActionType.SendForm && getValues('checkIn')) {
      setValue('questionnaireId', '', { shouldDirty: true });
      applyForm('');
    }
    // The limit differs per action, so recheck the message already typed.
    void trigger('message');
  };

  return (
    <section className="flex flex-col gap-3">
      <SectionHeading icon={<Send className="size-4" />}>{t('automations.drawer.what.heading')}</SectionHeading>
      {!actionLocked && (
        <div role="group" aria-label={t('automations.drawer.what.heading')} className="grid grid-cols-2 gap-2">
          {ACTIONS.map(({ value, icon, labelKey }) => (
            <ChoiceButton
              key={value}
              selected={actionType === value}
              disabled={actionReadOnly}
              onClick={() => chooseAction(value)}
              icon={icon}
            >
              {t(`automations.drawer.what.${labelKey}`)}
            </ChoiceButton>
          ))}
        </div>
      )}
      {!actionLocked && actionReadOnly && (
        <p className="text-meta font-normal text-muted-foreground">{t('automations.drawer.what.actionLockedHint')}</p>
      )}

      {actionType === AutomationActionType.SendForm && !actionLocked && (
        <label className={FIELD_CLASS}>
          <span>{t('automations.drawer.what.form')}</span>
          <div className="relative">
            <select
              {...register('questionnaireId', { onChange: (event) => applyForm(event.target.value as string) })}
              className={SELECT_CLASS}
            >
              <option value="">{t('automations.drawer.what.formPlaceholder')}</option>
              {forms.map((form) => (
                <option
                  key={form.publicId}
                  value={form.publicId}
                  disabled={checkInsLocked && form.type === QuestionnaireType.CheckIn && form.publicId !== questionnaireId}
                >
                  {form.type === QuestionnaireType.CheckIn
                    ? t('automations.drawer.what.formOptionCheckIn', { title: form.title })
                    : t('automations.drawer.what.formOption', { title: form.title, count: form.questionCount })}
                </option>
              ))}
            </select>
            <SelectChevron />
          </div>
          {formsQuery.isError && (
            <span className="text-meta font-normal text-destructive">{t('automations.drawer.what.formsError')}</span>
          )}
          {checkInsLocked && forms.some((form) => form.type === QuestionnaireType.CheckIn) && (
            <span className="text-meta font-normal text-muted-foreground">{t('subscription.checkIns.locked')}</span>
          )}
          {checkIn && selectedForm && (
            <span className="text-meta font-normal text-muted-foreground">
              {t('automations.drawer.what.formHintCheckIn', { title: selectedForm.title })}
            </span>
          )}
          <FieldError name="questionnaireId" />
        </label>
      )}

      <label className={FIELD_CLASS}>
        <span>{t('automations.drawer.what.message')}</span>
        <Textarea {...register('message')} rows={3} className="min-h-21 resize-none leading-normal" />
        <span className="flex items-start justify-between gap-2">
          <FieldError name="message" />
          <span className="ml-auto text-meta font-normal text-muted-foreground">
            {t('automations.drawer.what.counter', { current: message.length, max: limit })}
          </span>
        </span>
      </label>

      {checkIn && (
        <div className="flex flex-col gap-3">
          <Controller
            control={control}
            name="reminderEnabled"
            render={({ field }) => (
              <label className="flex cursor-pointer items-start gap-2.5">
                <Checkbox
                  className="mt-1"
                  checked={field.value}
                  onCheckedChange={(checked) => field.onChange(checked === true)}
                />
                <span className="text-copy text-ink">{t('automations.drawer.what.reminder', { hours: reminderHours })}</span>
              </label>
            )}
          />
          <Controller
            control={control}
            name="notifyCoachOnMissedWeek"
            render={({ field }) => (
              <div className="flex items-start justify-between gap-3">
                <label htmlFor="check-in-missed-week" className="flex cursor-pointer flex-col">
                  <span className="text-copy text-ink">{t('automations.drawer.what.missedWeek')}</span>
                  <span className="text-meta text-muted-foreground">{t('automations.drawer.what.missedWeekHint')}</span>
                </label>
                <Switch id="check-in-missed-week" checked={field.value} onCheckedChange={field.onChange} />
              </div>
            )}
          />
        </div>
      )}
    </section>
  );
}
