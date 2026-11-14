import { useId, useMemo, useState, type ReactNode } from 'react';
import { Controller, useForm, useWatch } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { useTranslation } from 'react-i18next';
import { CalendarDays, Check, Clock, Layers, Tag, Ticket } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { DatePicker } from '@/components/ui/date-picker';
import { Input } from '@/components/ui/input';
import { Sheet, SheetContent, SheetDescription, SheetFooter, SheetHeader, SheetTitle } from '@/components/ui/sheet';
import { ChoiceButton, SectionHeading } from '@/components/automations/automationFormParts';
import { Field, FormError } from '@/components/package-catalog/packageFormParts';
import {
  PROMOTION_MONTHS_MAX,
  PROMOTION_MONTHS_MIN,
  PROMOTION_PLAN_CODES,
  createPromotionSchema,
  defaultPromotionValues,
  toDateInput,
  type PromotionFormValues,
} from '@/components/admin-promotions/promotionForm';

/** What a save reports back so the drawer can flag the field the server refused, without rejecting. */
export type PromotionSaveOutcome =
  | 'saved'
  | 'error'
  | { field: keyof PromotionFormValues; message: string };

interface Props {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  /** Saves the form. Never rejects; the caller reports errors and returns what happened. */
  onSubmit: (values: PromotionFormValues) => Promise<PromotionSaveOutcome>;
  isSaving: boolean;
}

/** Side drawer to create a promotion: value, how coaches get it, how long it lasts, the claim window, plans and limits. */
export default function PromotionDrawer({ open, onOpenChange, onSubmit, isSaving }: Props) {
  return (
    <Sheet open={open} onOpenChange={onOpenChange}>
      <SheetContent className="gap-0 sm:w-drawer sm:max-w-none">
        <DrawerBody onClose={() => onOpenChange(false)} onSubmit={onSubmit} isSaving={isSaving} />
      </SheetContent>
    </Sheet>
  );
}

interface BodyProps {
  onClose: () => void;
  onSubmit: Props['onSubmit'];
  isSaving: boolean;
}

interface CheckRowProps {
  label: string;
  checked: boolean;
  onCheckedChange: (checked: boolean) => void;
}

function CheckRow({ label, checked, onCheckedChange }: CheckRowProps) {
  const id = useId();
  return (
    <div className="flex items-center gap-2.5">
      <Checkbox
        id={id}
        checked={checked}
        onCheckedChange={(next) => onCheckedChange(next === true)}
        className="size-4.5"
      />
      <label htmlFor={id} className="text-copy font-medium text-ink">
        {label}
      </label>
    </div>
  );
}

/** An input with a unit shown inside its right edge. */
function UnitInput({ unit, children }: { unit: string; children: ReactNode }) {
  return (
    <span className="relative flex items-center">
      {children}
      <span className="pointer-events-none absolute right-3 text-body text-muted-foreground">{unit}</span>
    </span>
  );
}

const UNIT_INPUT_CLASS = 'h-10 pr-12';

function DrawerBody({ onSubmit, isSaving, onClose }: BodyProps) {
  const { t } = useTranslation();
  const [initial] = useState<PromotionFormValues>(() => defaultPromotionValues());
  const schema = useMemo(() => createPromotionSchema(), []);
  const {
    register,
    handleSubmit,
    control,
    setValue,
    setError,
    formState: { errors },
  } = useForm<PromotionFormValues>({ resolver: zodResolver(schema), defaultValues: initial });

  const values = useWatch({ control });
  const kind = values.kind ?? initial.kind;
  const mode = values.mode ?? initial.mode;
  const duration = values.duration ?? initial.duration;
  const today = toDateInput(new Date());

  const submit = handleSubmit(async (formValues) => {
    const outcome = await onSubmit(formValues);
    if (typeof outcome === 'object') {
      setError(outcome.field, { type: 'server', message: outcome.message });
    }
  });

  return (
    <form onSubmit={submit} noValidate className="flex min-h-0 flex-1 flex-col">
      <SheetHeader className="border-b border-border px-6 py-5 pr-14">
        <SheetTitle className="text-auth-title">{t('adminPromotions.drawer.titleNew')}</SheetTitle>
        <SheetDescription className="text-body">{t('adminPromotions.drawer.description')}</SheetDescription>
      </SheetHeader>

      <div className="flex flex-1 flex-col gap-6 overflow-y-auto px-6 py-5">
        <section className="flex flex-col gap-4">
          <SectionHeading icon={<Tag className="size-4" />}>{t('adminPromotions.drawer.sectionPromotion')}</SectionHeading>
          <Field
            label={t('adminPromotions.drawer.name')}
            error={errors.name?.message}
            hint={t('adminPromotions.drawer.nameHint')}
          >
            {(fieldControl) => <Input {...register('name')} autoComplete="off" className="h-10" {...fieldControl} />}
          </Field>
          <div className="flex flex-col gap-1.5">
            <span className="text-body font-medium text-ink-2">{t('adminPromotions.drawer.type')}</span>
            <div role="group" aria-label={t('adminPromotions.drawer.type')} className="grid grid-cols-2 gap-2">
              <ChoiceButton
                selected={kind === 'percent'}
                icon={null}
                onClick={() => setValue('kind', 'percent', { shouldDirty: true })}
              >
                {t('adminPromotions.drawer.typePercent')}
              </ChoiceButton>
              <ChoiceButton
                selected={kind === 'fixed'}
                icon={null}
                onClick={() => setValue('kind', 'fixed', { shouldDirty: true })}
              >
                {t('adminPromotions.drawer.typeFixed')}
              </ChoiceButton>
            </div>
          </div>
          <div className="grid grid-cols-2 gap-3">
            <Field label={t('adminPromotions.drawer.value')} error={errors.value?.message}>
              {(fieldControl) => (
                <UnitInput unit={kind === 'percent' ? '%' : 'CZK'}>
                  <Input {...register('value')} inputMode="decimal" className={UNIT_INPUT_CLASS} {...fieldControl} />
                </UnitInput>
              )}
            </Field>
          </div>
        </section>

        <section className="flex flex-col gap-4">
          <SectionHeading icon={<Ticket className="size-4" />}>{t('adminPromotions.drawer.sectionHow')}</SectionHeading>
          <div className="flex flex-col gap-1.5">
            <span className="text-body font-medium text-ink-2">{t('adminPromotions.drawer.claimed')}</span>
            <div role="group" aria-label={t('adminPromotions.drawer.claimed')} className="grid grid-cols-2 gap-2">
              <ChoiceButton
                selected={mode === 'automatic'}
                icon={null}
                onClick={() => setValue('mode', 'automatic', { shouldDirty: true })}
              >
                {t('adminPromotions.drawer.automatic')}
              </ChoiceButton>
              <ChoiceButton
                selected={mode === 'code'}
                icon={null}
                onClick={() => setValue('mode', 'code', { shouldDirty: true })}
              >
                {t('adminPromotions.drawer.withCode')}
              </ChoiceButton>
            </div>
          </div>
          {mode === 'code' && (
            <Field
              label={t('adminPromotions.drawer.code')}
              error={errors.code?.message}
              hint={t('adminPromotions.drawer.codeHint')}
            >
              {(fieldControl) => (
                <Input
                  {...register('code')}
                  autoComplete="off"
                  spellCheck={false}
                  className="h-10 font-mono uppercase"
                  {...fieldControl}
                />
              )}
            </Field>
          )}
        </section>

        <section className="flex flex-col gap-4">
          <SectionHeading icon={<Clock className="size-4" />}>{t('adminPromotions.drawer.sectionLasts')}</SectionHeading>
          <div className="flex flex-col gap-1.5">
            <span className="text-body font-medium text-ink-2">{t('adminPromotions.drawer.lasts')}</span>
            <div role="group" aria-label={t('adminPromotions.drawer.lasts')} className="grid grid-cols-3 gap-2">
              <ChoiceButton
                selected={duration === 'first'}
                icon={null}
                onClick={() => setValue('duration', 'first', { shouldDirty: true })}
              >
                {t('adminPromotions.drawer.firstMonth')}
              </ChoiceButton>
              <ChoiceButton
                selected={duration === 'months'}
                icon={null}
                onClick={() => setValue('duration', 'months', { shouldDirty: true })}
              >
                {t('adminPromotions.drawer.months')}
              </ChoiceButton>
              <ChoiceButton
                selected={duration === 'forever'}
                icon={null}
                onClick={() => setValue('duration', 'forever', { shouldDirty: true })}
              >
                {t('adminPromotions.drawer.forever')}
              </ChoiceButton>
            </div>
          </div>
          {duration === 'months' && (
            <div className="grid grid-cols-2 gap-3">
              <Field
                label={t('adminPromotions.drawer.monthCount')}
                error={errors.durationMonths?.message}
                hint={t('adminPromotions.drawer.monthCountHint', {
                  min: PROMOTION_MONTHS_MIN,
                  max: PROMOTION_MONTHS_MAX,
                })}
              >
                {(fieldControl) => <Input {...register('durationMonths')} inputMode="numeric" className="h-10" {...fieldControl} />}
              </Field>
            </div>
          )}
        </section>

        <section className="flex flex-col gap-4">
          <SectionHeading icon={<CalendarDays className="size-4" />}>{t('adminPromotions.drawer.sectionWindow')}</SectionHeading>
          <div className="grid grid-cols-2 gap-3">
            <Field label={t('adminPromotions.drawer.claimFrom')} error={errors.claimFrom?.message}>
              {(fieldControl) => (
                <Controller
                  control={control}
                  name="claimFrom"
                  render={({ field }) => (
                    <DatePicker
                      value={field.value}
                      onChange={field.onChange}
                      id={fieldControl.id}
                      aria-describedby={fieldControl['aria-describedby']}
                      aria-invalid={fieldControl['aria-invalid']}
                    />
                  )}
                />
              )}
            </Field>
            <Field
              label={t('adminPromotions.drawer.claimUntil')}
              error={errors.claimUntil?.message}
              hint={t('adminPromotions.drawer.optional')}
            >
              {(fieldControl) => (
                <Controller
                  control={control}
                  name="claimUntil"
                  render={({ field }) => (
                    <DatePicker
                      value={field.value}
                      onChange={field.onChange}
                      min={values.claimFrom || today}
                      id={fieldControl.id}
                      aria-describedby={fieldControl['aria-describedby']}
                      aria-invalid={fieldControl['aria-invalid']}
                    />
                  )}
                />
              )}
            </Field>
          </div>
        </section>

        <section className="flex flex-col gap-4">
          <SectionHeading icon={<Layers className="size-4" />}>{t('adminPromotions.drawer.sectionLimits')}</SectionHeading>
          <div className="flex flex-col gap-2.5">
            <span className="text-body font-medium text-ink-2">{t('adminPromotions.drawer.plans')}</span>
            <Controller
              control={control}
              name="planCodes"
              render={({ field }) => (
                <div role="group" aria-label={t('adminPromotions.drawer.plans')} className="flex flex-wrap gap-x-6 gap-y-2.5">
                  {PROMOTION_PLAN_CODES.map((code) => (
                    <CheckRow
                      key={code}
                      label={t(`adminPromotions.plans.${code}`)}
                      checked={field.value.includes(code)}
                      onCheckedChange={(checked) =>
                        field.onChange(checked ? [...field.value, code] : field.value.filter((value) => value !== code))
                      }
                    />
                  ))}
                </div>
              )}
            />
            <FormError message={errors.planCodes?.message} />
          </div>
          <div className="grid grid-cols-2 gap-3">
            <Field
              label={t('adminPromotions.drawer.maxUses')}
              error={errors.maxUses?.message}
              hint={t('adminPromotions.drawer.maxUsesHint')}
            >
              {(fieldControl) => <Input {...register('maxUses')} inputMode="numeric" className="h-10" {...fieldControl} />}
            </Field>
          </div>
          <div className="flex flex-col gap-2.5">
            <Controller
              control={control}
              name="newCoachesOnly"
              render={({ field }) => (
                <CheckRow
                  label={t('adminPromotions.drawer.newCoachesOnly')}
                  checked={field.value}
                  onCheckedChange={field.onChange}
                />
              )}
            />
            <Controller
              control={control}
              name="oneUsePerCoach"
              render={({ field }) => (
                <CheckRow
                  label={t('adminPromotions.drawer.oneUsePerCoach')}
                  checked={field.value}
                  onCheckedChange={field.onChange}
                />
              )}
            />
          </div>
          <p className="text-meta text-muted-foreground">{t('adminPromotions.drawer.coversBill')}</p>
        </section>
      </div>

      <SheetFooter className="flex-row items-center gap-2 border-t border-border px-6 py-4">
        <span className="mr-auto" />
        <Button type="button" variant="outline" size="lg" onClick={onClose}>
          {t('common.cancel')}
        </Button>
        <Button type="submit" size="lg" disabled={isSaving}>
          <Check aria-hidden="true" />
          {isSaving ? t('adminPromotions.drawer.saving') : t('adminPromotions.drawer.save')}
        </Button>
      </SheetFooter>
    </form>
  );
}
