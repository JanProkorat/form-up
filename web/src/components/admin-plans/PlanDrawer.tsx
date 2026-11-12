import { useId, useMemo, useState, type ReactNode } from 'react';
import { Controller, useForm, useWatch, type Control } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { useTranslation } from 'react-i18next';
import { AlertTriangle, Check, CreditCard, Layers, Lock, Star, Users } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { Input } from '@/components/ui/input';
import { Sheet, SheetContent, SheetDescription, SheetFooter, SheetHeader, SheetTitle } from '@/components/ui/sheet';
import { Switch } from '@/components/ui/switch';
import type { SubscriptionPlanDto } from '@/api/admin-subscription-plans';
import { ChoiceButton, SectionHeading } from '@/components/automations/automationFormParts';
import { Field, FieldHint } from '@/components/package-catalog/packageFormParts';
import {
  createPlanSchema,
  defaultPlanValues,
  formatPlanMoney,
  inputToMinor,
  isFreePlan,
  planName,
  planToFormValues,
  type PlanFormValues,
} from '@/components/admin-plans/planForm';
import { parseAmount } from '@/lib/money';
import { cn } from '@/lib/utils';

/** What a save reports back so the drawer can react (flag a taken code) without rejecting. */
export type PlanSaveOutcome = 'saved' | 'codeTaken' | 'error';

interface Props {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  /** The saved plan being edited; undefined for a new plan. */
  plan?: SubscriptionPlanDto;
  /** Saves the form. Never rejects; the caller reports errors and returns what happened. */
  onSubmit: (values: PlanFormValues) => Promise<PlanSaveOutcome>;
  /** Stops offering the plan being edited. */
  onStopOffering: (plan: SubscriptionPlanDto) => void;
  isSaving: boolean;
}

/** Side drawer to create or edit a plan: names, price, second role, client cap, features, visibility. */
export default function PlanDrawer({ open, onOpenChange, ...rest }: Props) {
  return (
    <Sheet open={open} onOpenChange={onOpenChange}>
      <SheetContent className="gap-0 sm:w-drawer sm:max-w-none">
        <DrawerBody key={rest.plan?.code ?? 'new'} onClose={() => onOpenChange(false)} {...rest} />
      </SheetContent>
    </Sheet>
  );
}

interface BodyProps extends Omit<Props, 'open' | 'onOpenChange'> {
  onClose: () => void;
}

interface CheckRowProps {
  label: string;
  hint?: string;
  checked: boolean;
  onCheckedChange: (checked: boolean) => void;
  disabled?: boolean;
}

/** A labelled checkbox with an optional quiet hint underneath. */
function CheckRow({ label, hint, checked, onCheckedChange, disabled = false }: CheckRowProps) {
  const id = useId();
  const hintId = `${id}-hint`;
  return (
    <div className="flex items-start gap-2.5">
      <Checkbox
        id={id}
        checked={checked}
        disabled={disabled}
        aria-describedby={hint ? hintId : undefined}
        onCheckedChange={(next) => onCheckedChange(next === true)}
        className="mt-0.5 size-4.5"
      />
      <label htmlFor={id} className="flex flex-col gap-0.5 text-copy text-ink">
        <span className="font-medium">{label}</span>
        {hint && (
          <span id={hintId} className="text-meta font-normal text-muted-foreground">
            {hint}
          </span>
        )}
      </label>
    </div>
  );
}

/** A price box with the currency shown inside its right edge. */
function MoneyInput({ currency, children }: { currency: string; children: ReactNode }) {
  return (
    <span className="relative flex items-center">
      {children}
      <span className="pointer-events-none absolute right-3 text-body text-muted-foreground">{currency}</span>
    </span>
  );
}

const FIELD_INPUT_CLASS = 'h-10 pr-12';

function DrawerBody({ plan, onSubmit, onStopOffering, isSaving, onClose }: BodyProps) {
  const { t, i18n } = useTranslation();
  const [initial] = useState<PlanFormValues>(() => (plan ? planToFormValues(plan) : defaultPlanValues()));
  const isNew = plan === undefined;
  const isFree = isFreePlan(plan);
  const currency = plan?.currency ?? 'CZK';
  const coachCount = plan?.coachCount ?? 0;
  const schema = useMemo(() => createPlanSchema(isNew), [isNew]);

  const {
    register,
    handleSubmit,
    control,
    setValue,
    setError,
    formState: { errors },
  } = useForm<PlanFormValues>({ resolver: zodResolver(schema), defaultValues: initial });

  const values = useWatch({ control });
  const secondRole = values.secondRole ?? initial.secondRole;
  const unlimited = values.unlimited ?? initial.unlimited;
  const weeklyCheckIns = values.canUseWeeklyCheckIns ?? initial.canUseWeeklyCheckIns;
  const offered = isFree ? true : (values.isActive ?? initial.isActive);

  const priceText = values.price ?? initial.price;
  const parsedPrice = parseAmount(priceText);
  const savedPriceMinor = plan?.priceMinorUnits ?? 0;
  const newPriceMinor = inputToMinor(priceText);
  const showPriceWarning = !isNew && !isFree && coachCount > 0 && parsedPrice !== null && newPriceMinor !== savedPriceMinor;

  const submit = handleSubmit(async (formValues) => {
    const outcome = await onSubmit(formValues);
    if (outcome === 'codeTaken') {
      setError('code', { type: 'server', message: 'adminPlans.validation.codeTaken' });
    }
  });

  const previewName = planName(
    {
      code: values.code,
      nameCs: values.nameCs,
      nameEn: values.nameEn,
      nameDe: values.nameDe,
    },
    i18n.language,
  );
  const previewParts: string[] = [
    t('adminPlans.preview.price', {
      price: formatPlanMoney(parsedPrice === null ? 0 : newPriceMinor, currency, i18n.language),
    }),
  ];
  if (secondRole === 'addon') {
    const addOn = parseAmount(values.addOnPrice ?? '');
    if (addOn !== null && addOn > 0) {
      previewParts.push(
        t('adminPlans.preview.secondRole', { price: formatPlanMoney(inputToMinor(values.addOnPrice ?? ''), currency, i18n.language) }),
      );
    }
  } else {
    previewParts.push(t('adminPlans.preview.secondRoleIncluded'));
  }
  const cap = Number(values.maxActiveClients ?? '');
  if (unlimited) {
    previewParts.push(t('adminPlans.preview.unlimitedClients'));
  } else if (Number.isInteger(cap) && cap > 0) {
    previewParts.push(t('adminPlans.preview.clients', { count: cap }));
  }

  return (
    <form onSubmit={submit} noValidate className="flex min-h-0 flex-1 flex-col">
      <SheetHeader className="border-b border-border px-6 py-5 pr-14">
        <SheetTitle className="text-auth-title">
          {isNew
            ? t('adminPlans.drawer.titleNew')
            : t('adminPlans.drawer.titleEdit', { name: planName(plan, i18n.language) })}
        </SheetTitle>
        <SheetDescription className="text-body">
          {isNew ? t('adminPlans.drawer.descriptionNew') : t('adminPlans.drawer.coaches', { count: coachCount })}
        </SheetDescription>
      </SheetHeader>

      <div className="flex flex-1 flex-col gap-6 overflow-y-auto px-6 py-5">
        <section className="flex flex-col gap-4">
          <SectionHeading icon={<Layers className="size-4" />}>{t('adminPlans.drawer.sectionPlan')}</SectionHeading>
          <div className="grid grid-cols-3 gap-3">
            <Field label={t('adminPlans.drawer.nameCs')} error={errors.nameCs?.message}>
              {(fieldControl) => <Input {...register('nameCs')} className="h-10" {...fieldControl} />}
            </Field>
            <Field label={t('adminPlans.drawer.nameEn')} error={errors.nameEn?.message}>
              {(fieldControl) => <Input {...register('nameEn')} className="h-10" {...fieldControl} />}
            </Field>
            <Field label={t('adminPlans.drawer.nameDe')} error={errors.nameDe?.message}>
              {(fieldControl) => <Input {...register('nameDe')} className="h-10" {...fieldControl} />}
            </Field>
          </div>
          <Field label={t('adminPlans.drawer.code')} error={errors.code?.message} hint={t('adminPlans.drawer.codeHint')}>
            {(fieldControl) => (
              <span className="relative flex items-center">
                {!isNew && (
                  <Lock className="pointer-events-none absolute left-3 size-4 text-muted-foreground" aria-hidden="true" />
                )}
                <Input
                  {...register('code')}
                  readOnly={!isNew}
                  autoComplete="off"
                  spellCheck={false}
                  className={cn('h-10', !isNew && 'pl-9.5')}
                  {...fieldControl}
                />
              </span>
            )}
          </Field>
        </section>

        <section className="flex flex-col gap-4">
          <SectionHeading icon={<CreditCard className="size-4" />}>{t('adminPlans.drawer.sectionPrice')}</SectionHeading>
          <div className="grid grid-cols-2 gap-3">
            <Field
              label={t('adminPlans.drawer.perMonth')}
              error={errors.price?.message}
              hint={isFree ? t('adminPlans.drawer.freePriceHint') : undefined}
            >
              {(fieldControl) => (
                <MoneyInput currency={currency}>
                  <Input
                    {...register('price')}
                    inputMode="decimal"
                    readOnly={isFree}
                    className={FIELD_INPUT_CLASS}
                    {...fieldControl}
                  />
                </MoneyInput>
              )}
            </Field>
            <div className="flex flex-col gap-1.5">
              <span className="text-body font-medium text-ink-2">{t('adminPlans.drawer.billing')}</span>
              <span className="flex h-10 items-center text-copy text-ink-2">{t('adminPlans.drawer.billingMonthly')}</span>
              <FieldHint>{t('adminPlans.drawer.billingAnnualLater')}</FieldHint>
            </div>
          </div>

          <div className="flex flex-col gap-1.5">
            <span className="text-body font-medium text-ink-2">{t('adminPlans.drawer.secondRole')}</span>
            <div role="group" aria-label={t('adminPlans.drawer.secondRole')} className="grid grid-cols-2 gap-2">
              <ChoiceButton
                selected={secondRole === 'addon'}
                icon={null}
                onClick={() => setValue('secondRole', 'addon', { shouldDirty: true })}
              >
                {t('adminPlans.drawer.secondRoleAddOn')}
              </ChoiceButton>
              <ChoiceButton
                selected={secondRole === 'included'}
                icon={null}
                onClick={() => setValue('secondRole', 'included', { shouldDirty: true })}
              >
                {t('adminPlans.drawer.secondRoleIncluded')}
              </ChoiceButton>
            </div>
          </div>

          {secondRole === 'addon' && (
            <div className="grid grid-cols-2 gap-3">
              <Field label={t('adminPlans.drawer.addOnPrice')} error={errors.addOnPrice?.message}>
                {(fieldControl) => (
                  <MoneyInput currency={currency}>
                    <Input {...register('addOnPrice')} inputMode="decimal" className={FIELD_INPUT_CLASS} {...fieldControl} />
                  </MoneyInput>
                )}
              </Field>
            </div>
          )}

          {showPriceWarning && plan && (
            <PriceChangeWarning
              control={control}
              from={formatPlanMoney(savedPriceMinor, currency, i18n.language)}
              to={formatPlanMoney(newPriceMinor, currency, i18n.language)}
              coachCount={coachCount}
              planLabel={planName(plan, i18n.language)}
            />
          )}
        </section>

        <section className="flex flex-col gap-4">
          <SectionHeading icon={<Users className="size-4" />}>{t('adminPlans.drawer.sectionClients')}</SectionHeading>
          <div className="grid grid-cols-2 items-start gap-3">
            <Field
              label={t('adminPlans.drawer.activeClients')}
              error={errors.maxActiveClients?.message}
              hint={t('adminPlans.drawer.activeClientsHint')}
            >
              {(fieldControl) => (
                <Input
                  {...register('maxActiveClients')}
                  inputMode="numeric"
                  disabled={unlimited}
                  className="h-10"
                  {...fieldControl}
                />
              )}
            </Field>
            <div className="pt-8.5">
              <Controller
                control={control}
                name="unlimited"
                render={({ field }) => (
                  <CheckRow
                    label={t('adminPlans.unlimited')}
                    checked={field.value}
                    onCheckedChange={field.onChange}
                  />
                )}
              />
            </div>
          </div>
        </section>

        <section className="flex flex-col gap-3">
          <SectionHeading icon={<Check className="size-4" />}>{t('adminPlans.drawer.sectionFeatures')}</SectionHeading>
          <Controller
            control={control}
            name="canCreatePlans"
            render={({ field }) => (
              <CheckRow
                label={t('adminPlans.features.plans')}
                checked={field.value}
                onCheckedChange={field.onChange}
              />
            )}
          />
          <Controller
            control={control}
            name="canMessage"
            render={({ field }) => (
              <CheckRow
                label={t('adminPlans.features.chat')}
                checked={field.value}
                onCheckedChange={field.onChange}
              />
            )}
          />
          <Controller
            control={control}
            name="canSendQuestionnaires"
            render={({ field }) => (
              <CheckRow
                label={t('adminPlans.features.forms')}
                hint={t('adminPlans.features.formsHint')}
                checked={field.value}
                onCheckedChange={field.onChange}
              />
            )}
          />
          <Controller
            control={control}
            name="canUseWeeklyCheckIns"
            render={({ field }) => (
              <CheckRow
                label={t('adminPlans.features.checkIns')}
                checked={field.value}
                onCheckedChange={(checked) => {
                  field.onChange(checked);
                  if (!checked) {
                    setValue('canUsePerClientCheckInConfig', false, { shouldDirty: true });
                  }
                }}
              />
            )}
          />
          <Controller
            control={control}
            name="canUsePerClientCheckInConfig"
            render={({ field }) => (
              <CheckRow
                label={t('adminPlans.features.perClientCheckIns')}
                hint={t('adminPlans.features.perClientCheckInsHint')}
                checked={weeklyCheckIns && field.value}
                disabled={!weeklyCheckIns}
                onCheckedChange={field.onChange}
              />
            )}
          />
          <Controller
            control={control}
            name="canUsePayments"
            render={({ field }) => (
              <CheckRow
                label={t('adminPlans.features.payments')}
                hint={t('adminPlans.features.paymentsHint')}
                checked={field.value}
                onCheckedChange={field.onChange}
              />
            )}
          />
        </section>

        <section className="flex flex-col gap-3">
          <SectionHeading icon={<Star className="size-4" />}>{t('adminPlans.drawer.sectionPreview')}</SectionHeading>
          <div className="flex items-center gap-3 rounded-xl border border-line px-3.5 py-3">
            <span className="text-copy font-semibold text-ink">{previewName}</span>
            <span className="min-w-0 text-body text-muted-foreground">{previewParts.join(' · ')}</span>
            <span
              className={cn(
                'ml-auto inline-flex shrink-0 items-center gap-1.5 rounded-full px-2 py-0.5 text-meta font-semibold',
                offered ? 'bg-success-soft text-success-ink' : 'bg-sunken text-ink-2',
              )}
            >
              {offered && <span className="size-1.5 rounded-full bg-current" aria-hidden="true" />}
              {t(offered ? 'adminPlans.preview.offered' : 'adminPlans.preview.notOffered')}
            </span>
          </div>
          <div className="flex items-start justify-between gap-4">
            <div className="flex flex-col">
              <span id="plan-offer-to-coaches" className="text-copy font-semibold text-ink">
                {t('adminPlans.drawer.offer')}
              </span>
              <span className="text-meta text-muted-foreground">
                {isFree ? t('adminPlans.drawer.offerFreeHint') : t('adminPlans.drawer.offerHint')}
              </span>
            </div>
            <Controller
              control={control}
              name="isActive"
              render={({ field }) => (
                <Switch
                  checked={isFree ? true : field.value}
                  disabled={isFree}
                  onCheckedChange={field.onChange}
                  aria-labelledby="plan-offer-to-coaches"
                />
              )}
            />
          </div>
        </section>
      </div>

      <SheetFooter className="flex-row items-center gap-2 border-t border-border px-6 py-4">
        {plan && !isFree && (plan.isActive ?? false) && (
          <Button type="button" variant="ghost" size="lg" onClick={() => onStopOffering(plan)} disabled={isSaving}>
            {t('adminPlans.drawer.stopOffering')}
          </Button>
        )}
        <span className="mr-auto" />
        <Button type="button" variant="outline" size="lg" onClick={onClose}>
          {t('common.cancel')}
        </Button>
        <Button type="submit" size="lg" disabled={isSaving}>
          <Check aria-hidden="true" />
          {isSaving ? t('adminPlans.drawer.saving') : t('adminPlans.drawer.save')}
        </Button>
      </SheetFooter>
    </form>
  );
}

interface WarningProps {
  control: Control<PlanFormValues>;
  from: string;
  to: string;
  coachCount: number;
  planLabel: string;
}

/** Shown when a plan with coaches gets a new price; the tick moves those coaches at their renewal. */
function PriceChangeWarning({ control, from, to, coachCount, planLabel }: WarningProps) {
  const { t } = useTranslation();
  return (
    <div role="status" className="flex flex-col gap-2.5 rounded-xl bg-caution-soft px-4 py-3.5">
      <div className="flex items-center gap-2.5 text-copy font-semibold text-ink">
        <AlertTriangle className="size-4.5 shrink-0 text-caution-ink" aria-hidden="true" />
        {t('adminPlans.priceChange.title', { from, to })}
      </div>
      <p className="text-body text-muted-foreground">
        {t('adminPlans.priceChange.body', { from, to, count: coachCount, name: planLabel })}
      </p>
      <Controller
        control={control}
        name="moveExistingCoaches"
        render={({ field }) => (
          <CheckRow
            label={t('adminPlans.priceChange.move', { price: to })}
            hint={t('adminPlans.priceChange.moveHint')}
            checked={field.value}
            onCheckedChange={field.onChange}
          />
        )}
      />
    </div>
  );
}
