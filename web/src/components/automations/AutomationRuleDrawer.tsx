import { useState } from 'react';
import { FormProvider, useForm, useWatch } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { useTranslation } from 'react-i18next';
import { Check } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Sheet, SheetContent, SheetDescription, SheetFooter, SheetHeader, SheetTitle } from '@/components/ui/sheet';
import { AutomationTriggerKind, type AutomationRuleDto, type ClientAutomationItem } from '@/api/automations';
import { useTrainerQuestionnaires } from '@/hooks/useQuestionnaireQueries';
import { FIELD_CLASS, FieldError } from '@/components/automations/automationFormParts';
import AutomationOptionsSection from '@/components/automations/AutomationOptionsSection';
import AutomationPauseSection from '@/components/automations/AutomationPauseSection';
import AutomationWhatSection from '@/components/automations/AutomationWhatSection';
import AutomationWhenSection from '@/components/automations/AutomationWhenSection';
import AutomationWhoSection from '@/components/automations/AutomationWhoSection';
import { formatNextSend, summaryFooter } from '@/components/automations/automationFormat';
import {
  automationRuleSchema,
  defaultRuleValues,
  pauseToFormValues,
  ruleToFormValues,
  summarySensitiveSnapshot,
  type AutomationDrawerVariant,
  type AutomationRuleFormValues,
  type PauseState,
} from '@/components/automations/automationRuleForm';

interface Props {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  /** global: the Automations page; clientOnly: a rule of one client; override: a client's change to a global rule. */
  variant?: AutomationDrawerVariant;
  /** The saved rule being edited; undefined for a new rule. */
  rule?: AutomationRuleDto;
  /** Override variant: the row's current pause, prefilled into the pause section. */
  pause?: PauseState;
  /** Override variant: the client's row, for its own check-in form and its next send. */
  override?: ClientAutomationItem;
  /** Client variants: the client's first name, for the title and description. */
  clientName?: string;
  /** Saves the form. Resolves when the save is done and never rejects; the caller reports errors. */
  onSubmit: (values: AutomationRuleFormValues) => Promise<void>;
  isSaving: boolean;
}

/** Side drawer to create or edit an automation rule: Name, then When, What, Who and Options. */
export default function AutomationRuleDrawer({ open, onOpenChange, ...rest }: Props) {
  return (
    <Sheet open={open} onOpenChange={onOpenChange}>
      <SheetContent className="gap-0 sm:w-drawer sm:max-w-none">
        <DrawerBody key={rest.rule?.ruleId ?? 'new'} onClose={() => onOpenChange(false)} {...rest} />
      </SheetContent>
    </Sheet>
  );
}

interface BodyProps extends Omit<Props, 'open' | 'onOpenChange'> {
  onClose: () => void;
}

function DrawerBody({ variant = 'global', rule, pause, override, clientName = '', onSubmit, isSaving, onClose }: BodyProps) {
  const { t, i18n } = useTranslation();
  const [initial] = useState<AutomationRuleFormValues>(() =>
    rule
      ? {
          ...ruleToFormValues(rule),
          ...pauseToFormValues(pause),
          checkInQuestionnaireId: override?.checkInQuestionnaireId ?? '',
        }
      : defaultRuleValues(),
  );
  const formsQuery = useTrainerQuestionnaires();
  const [initialSnapshot] = useState(() => summarySensitiveSnapshot(initial));

  const methods = useForm<AutomationRuleFormValues>({
    resolver: zodResolver(automationRuleSchema),
    defaultValues: initial,
  });
  const { register, handleSubmit, control } = methods;
  const values = useWatch({ control });

  const triggerKind = values.triggerKind ?? initial.triggerKind;
  const isNew = rule === undefined;
  const sensitiveChanged =
    summarySensitiveSnapshot({ ...initial, ...values } as AutomationRuleFormValues) !== initialSnapshot;
  // A client's check-in override: the footer names the next send and the form the client gets (the pick, or the rule's).
  const isCheckInOverride = variant === 'override' && initial.checkIn;
  const effectiveFormId = values.checkInQuestionnaireId || initial.questionnaireId;
  const effectiveFormTitle = (formsQuery.data ?? []).find((form) => form.publicId === effectiveFormId)?.title;
  const overrideFooter =
    isCheckInOverride && override?.nextSendAt && !sensitiveChanged
      ? t(
          effectiveFormTitle
            ? 'clientDetail.automations.drawer.overrideFooter'
            : 'clientDetail.automations.drawer.overrideFooterNoForm',
          { client: clientName, date: formatNextSend(override.nextSendAt, i18n.language), form: effectiveFormTitle },
        )
      : null;
  const footer = isCheckInOverride
    ? overrideFooter
    : isNew || sensitiveChanged
      ? null
      : summaryFooter(rule?.summary, t, i18n.language);

  const ruleName = rule?.name ?? '';
  const clientCopy = (key: string) => t(`clientDetail.automations.drawer.${key}`, { name: ruleName, client: clientName });
  const title =
    variant === 'override'
      ? clientCopy('overrideTitle')
      : variant === 'clientOnly'
        ? clientCopy(isNew ? 'clientOnlyTitleNew' : 'clientOnlyTitleEdit')
        : isNew
          ? t('automations.drawer.titleNew')
          : t('automations.drawer.titleEdit', { name: ruleName });
  const description =
    variant === 'override'
      ? clientCopy('overrideDescription')
      : variant === 'clientOnly'
        ? clientCopy('clientOnlyDescription')
        : triggerKind === AutomationTriggerKind.Event
          ? t('automations.drawer.descriptionEvent')
          : t('automations.drawer.descriptionSchedule');

  return (
    <form onSubmit={handleSubmit((submitted) => onSubmit(submitted))} noValidate className="flex min-h-0 flex-1 flex-col">
      <SheetHeader className="border-b border-border px-6 py-5 pr-14">
        <SheetTitle className="text-auth-title">{title}</SheetTitle>
        <SheetDescription className="text-body">
          {description}
        </SheetDescription>
      </SheetHeader>

      <FormProvider {...methods}>
        <div className="flex flex-1 flex-col gap-6 overflow-y-auto px-6 py-5">
          {variant !== 'override' && (
            <label className={FIELD_CLASS}>
              <span>{t('automations.drawer.name')}</span>
              <Input {...register('name')} placeholder={t('automations.drawer.namePlaceholder')} />
              <FieldError name="name" />
            </label>
          )}
          <AutomationWhenSection />
          <AutomationWhatSection
            actionLocked={variant === 'override'}
            overrideCheckInForm={isCheckInOverride}
            actionReadOnly={!isNew}
            checkInFormsAllowed={variant === 'global'}
            editingRuleId={rule?.ruleId}
          />
          {variant === 'global' && <AutomationWhoSection initialClients={rule?.clients} />}
          <AutomationOptionsSection />
          {variant === 'override' && <AutomationPauseSection />}
        </div>
      </FormProvider>

      <SheetFooter className="flex-row items-center gap-2 border-t border-border px-6 py-4">
        {footer && <span className="mr-auto text-meta text-muted-foreground">{footer}</span>}
        <Button type="button" variant="outline" size="lg" className={footer ? undefined : 'ml-auto'} onClick={onClose}>
          {t('common.cancel')}
        </Button>
        <Button type="submit" size="lg" disabled={isSaving}>
          <Check aria-hidden="true" />
          {isSaving
            ? t('automations.drawer.saving')
            : isCheckInOverride
              ? t('clientDetail.automations.drawer.overrideSave', { client: clientName })
              : t('automations.drawer.save')}
        </Button>
      </SheetFooter>
    </form>
  );
}
