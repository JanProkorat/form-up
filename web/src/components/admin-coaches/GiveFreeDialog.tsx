import { useMemo } from 'react';
import { Controller, useForm, useWatch } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { useTranslation } from 'react-i18next';
import { Repeat } from 'lucide-react';
import type { SubscriptionPlanDto } from '@/api/admin-subscription-plans';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { Dialog, DialogContent, DialogDescription, DialogTitle } from '@/components/ui/dialog';
import { DatePicker } from '@/components/ui/date-picker';
import { Textarea } from '@/components/ui/textarea';
import { Field } from '@/components/package-catalog/packageFormParts';
import { planName } from '@/components/admin-plans/planForm';
import PlanCarousel from '@/components/admin-coaches/PlanCarousel';
import { DialogIconTile } from '@/components/admin-coaches/ReasonDialog';
import {
  REASON_MAX,
  endOfLocalDay,
  giveFreeSchema,
  tomorrowInputValue,
  type GiveFreeFormValues,
} from '@/components/admin-coaches/coachActionForms';
import { buildGiveFreeOptions } from '@/components/admin-coaches/giveFreePlans';
import { formatCoachDate } from '@/components/admin-coaches/coachFormat';

interface Props {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  coachId: string;
  /** The coach's first name, for the summary line. */
  coachFirstName: string;
  plans: SubscriptionPlanDto[];
  /** Code of the plan the coach has now; picking it again only changes the end date. */
  currentPlanCode?: string;
  /** Gives the plan; resolves true when it succeeded, which closes the dialog. */
  run: (values: { planCode: string; freeUntil: Date | null; reason: string }) => Promise<boolean>;
}

/** Wide dialog for giving a coach a paid plan for free: plan cards, end date and reason. */
export default function GiveFreeDialog({ open, onOpenChange, ...rest }: Props) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="gap-0 overflow-hidden p-0 sm:max-w-220">
        <GiveFreeForm onClose={() => onOpenChange(false)} {...rest} />
      </DialogContent>
    </Dialog>
  );
}

function GiveFreeForm({
  coachId,
  coachFirstName,
  plans,
  currentPlanCode,
  run,
  onClose,
}: Omit<Props, 'open' | 'onOpenChange'> & { onClose: () => void }) {
  const { t, i18n } = useTranslation();
  const options = useMemo(() => buildGiveFreeOptions(plans), [plans]);
  const {
    register,
    handleSubmit,
    control,
    setValue,
    formState: { errors, isSubmitting },
  } = useForm<GiveFreeFormValues>({
    resolver: zodResolver(giveFreeSchema),
    defaultValues: { planCode: '', noEnd: false, freeUntil: '', reason: '' },
  });
  const [planCode, noEnd, freeUntil] = useWatch({ control, name: ['planCode', 'noEnd', 'freeUntil'] });
  const chosen = options.find((option) => option.plan.code === planCode);
  const chosenName = chosen ? planName(chosen.plan, i18n.language) : '';
  const keepsPlan = chosen !== undefined && chosen.plan.code === currentPlanCode;
  const untilText = noEnd ? '' : formatCoachDate(endOfLocalDay(freeUntil)?.toISOString(), i18n.language);

  const submit = handleSubmit(async (values) => {
    const done = await run({
      planCode: values.planCode,
      freeUntil: values.noEnd ? null : endOfLocalDay(values.freeUntil),
      reason: values.reason.trim(),
    });
    if (done) {
      onClose();
    }
  });

  const summary = (() => {
    if (!chosen) {
      return <span>{t('adminCoaches.giveFree.summaryNone')}</span>;
    }
    // Picking the plan the coach already has only moves its end date.
    const prefix = keepsPlan ? 'adminCoaches.giveFree.keep' : 'adminCoaches.giveFree.summary';
    const key = noEnd ? `${prefix}NoEnd` : untilText ? `${prefix}Until` : `${prefix}PickDate`;
    return (
      <span>
        <strong className="font-semibold text-ink">{chosenName}</strong>{' '}
        {t(key, { date: untilText, name: coachFirstName })}
      </span>
    );
  })();

  return (
    <form onSubmit={submit} noValidate className="flex flex-col">
      <div className="flex items-center gap-3.5 px-6 pt-5.5 pr-14">
        <DialogIconTile tone="orange">
          <Repeat />
        </DialogIconTile>
        <DialogTitle className="font-display text-panel-title leading-snug">
          {t('adminCoaches.actions.giveFree.title')}
        </DialogTitle>
      </div>

      <div className="flex max-h-[70vh] flex-col gap-4.5 overflow-y-auto px-6 pt-2.5 pb-5.5">
        <DialogDescription className="text-copy text-muted-foreground">
          {t('adminCoaches.giveFree.description', { name: coachFirstName })}
        </DialogDescription>

        <PlanCarousel
          options={options}
          value={planCode}
          name={`give-free-plan-${coachId}`}
          currentCode={currentPlanCode}
          onChange={(code) => setValue('planCode', code, { shouldValidate: true, shouldDirty: true })}
          error={errors.planCode?.message ? t(errors.planCode.message) : undefined}
        />

        <div className="grid grid-cols-1 items-start gap-4 sm:grid-cols-[16.25rem_minmax(0,1fr)]">
          <div className="flex flex-col gap-2">
            <Field label={t('adminCoaches.giveFree.until')} error={errors.freeUntil?.message}>
              {(fieldControl) => (
                <Controller
                  control={control}
                  name="freeUntil"
                  render={({ field }) => (
                    <DatePicker
                      value={field.value}
                      onChange={field.onChange}
                      min={tomorrowInputValue()}
                      disabled={noEnd}
                      {...fieldControl}
                    />
                  )}
                />
              )}
            </Field>
            <div className="flex items-center gap-2">
              <Controller
                control={control}
                name="noEnd"
                render={({ field }) => (
                  <Checkbox
                    id={`give-free-no-end-${coachId}`}
                    checked={field.value}
                    onCheckedChange={(next) => field.onChange(next === true)}
                    className="size-4"
                  />
                )}
              />
              <label htmlFor={`give-free-no-end-${coachId}`} className="text-body text-ink-2">
                {t('adminCoaches.actions.giveFree.noEnd')}
              </label>
            </div>
          </div>
          <Field label={t('adminCoaches.actions.reason')} error={errors.reason?.message}>
            {(fieldControl) => (
              <Textarea rows={3} maxLength={REASON_MAX} {...register('reason')} {...fieldControl} />
            )}
          </Field>
        </div>
      </div>

      <div className="flex flex-wrap items-center gap-2 border-t border-line bg-sunken px-6 py-4">
        <span className="flex items-center gap-2 text-body text-ink-2">
          <span aria-hidden="true" className={`size-2 shrink-0 rounded-full ${chosen ? chosen.color.dot : 'bg-faint'}`} />
          {summary}
        </span>
        <span className="mr-auto" />
        <Button type="button" variant="outline" size="lg" disabled={isSubmitting} onClick={onClose}>
          {t('common.cancel')}
        </Button>
        <Button type="submit" size="lg" disabled={isSubmitting}>
          <Repeat aria-hidden="true" />
          {keepsPlan
            ? t('adminCoaches.giveFree.confirmKeep')
            : chosen
              ? t('adminCoaches.giveFree.confirm', { plan: chosenName })
              : t('adminCoaches.actions.giveFree.button')}
        </Button>
      </div>
    </form>
  );
}
