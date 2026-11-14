import { useMemo, useState, type ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { Controller, useForm, useWatch, type Control } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { useTranslation } from 'react-i18next';
import { AlertTriangle, Check, ChevronLeft, CreditCard, Layers, Lock, Star, Users } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import type { SubscriptionPlanDto } from '@/api/admin-subscription-plans';
import { ChoiceButton, SectionHeading } from '@/components/automations/automationFormParts';
import { Field, FieldHint } from '@/components/package-catalog/packageFormParts';
import PlanCheckRow from '@/components/admin-plans/PlanCheckRow';
import {
  EDITOR_FEATURES,
  editorFeatureHintKey,
  editorFeatureLabelKey,
} from '@/components/admin-plans/planFeatureLabels';
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

/** What a save reports back so the form can react (flag a taken code) without rejecting. */
export type PlanSaveOutcome = 'saved' | 'codeTaken' | 'error';

export const PLANS_PATH = '/admin/plans';

interface Props {
  /** The saved plan being edited; undefined for a new plan. */
  plan?: SubscriptionPlanDto;
  /** Saves the form. Never rejects; the caller reports errors and returns what happened. */
  onSubmit: (values: PlanFormValues) => Promise<PlanSaveOutcome>;
  onCancel: () => void;
  isSaving: boolean;
}

const CARD_CLASS = 'flex flex-col gap-4 rounded-2xl border border-line bg-card p-6 shadow-panel';
const FIELD_INPUT_CLASS = 'h-10 pr-12';

/** A price box with the currency shown inside its right edge. */
function MoneyInput({ currency, children }: { currency: string; children: ReactNode }) {
  return (
    <span className="relative flex items-center">
      {children}
      <span className="pointer-events-none absolute right-3 text-body text-muted-foreground">{currency}</span>
    </span>
  );
}

/**
 * The plan editor form: header with the save actions, then Plan, Price, the coach-facing preview, Clients
 * and the Features card. Mount it with a key on the plan code so it initialises once per plan.
 */
export default function PlanEditorForm({ plan, onSubmit, onCancel, isSaving }: Props) {
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

  const featureOn = (flag: (typeof EDITOR_FEATURES)[number]['flag']): boolean => {
    const on = values[flag] ?? initial[flag];
    return flag === 'canUsePerClientCheckInConfig' ? weeklyCheckIns && on : on;
  };
  const featureCount = EDITOR_FEATURES.filter(({ flag }) => featureOn(flag)).length;

  const submit = handleSubmit(async (formValues) => {
    const outcome = await onSubmit(formValues);
    if (outcome === 'codeTaken') {
      setError('code', { type: 'server', message: 'adminPlans.validation.codeTaken' });
    }
  });

  const cap = Number(values.maxActiveClients ?? '');
  const hasSummary = parsedPrice !== null && (unlimited || (Number.isInteger(cap) && cap > 0));
  const previewName =
    planName(
      {
        code: values.code,
        nameCs: values.nameCs,
        nameEn: values.nameEn,
        nameDe: values.nameDe,
      },
      i18n.language,
    ) || t('adminPlans.editor.untitled');
  const addOn = parseAmount(values.addOnPrice ?? '');

  const title = isNew ? t('adminPlans.editor.titleNew') : planName(plan, i18n.language);
  const savedOffered = plan?.isActive ?? false;

  return (
    <form onSubmit={submit} noValidate className="flex flex-col gap-5.5">
      <header className="flex flex-wrap items-center gap-3.5">
        <Link
          to={PLANS_PATH}
          aria-label={t('adminPlans.editor.back')}
          className="flex size-9 shrink-0 items-center justify-center rounded-lg border border-border bg-card text-ink transition-colors outline-none hover:bg-muted focus-visible:ring-3 focus-visible:ring-ring/50"
        >
          <ChevronLeft className="size-4.5" aria-hidden="true" />
        </Link>
        <span
          aria-hidden="true"
          className="flex size-12 shrink-0 items-center justify-center rounded-xl bg-nutrition-soft text-nutrition-ink"
        >
          <Layers className="size-5" />
        </span>
        <div className="flex min-w-0 flex-col gap-0.5">
          <h1 className="truncate text-display font-semibold text-ink">{title}</h1>
          <p className="flex flex-wrap items-center gap-x-2 gap-y-1 text-copy text-muted-foreground">
            {isNew ? (
              t('adminPlans.editor.subtitleNew')
            ) : (
              <>
                <span>{t('adminPlans.table.code', { code: plan.code })}</span>
                <span aria-hidden="true">·</span>
                <span>{t('adminPlans.drawer.coaches', { count: coachCount })}</span>
                <span
                  className={cn(
                    'inline-flex items-center gap-1.5 rounded-full px-2 py-0.5 text-meta font-semibold',
                    savedOffered ? 'bg-success-soft text-success-ink' : 'bg-sunken text-ink-2',
                  )}
                >
                  {savedOffered && <span className="size-1.5 rounded-full bg-current" aria-hidden="true" />}
                  {t(savedOffered ? 'adminPlans.status.active' : 'adminPlans.status.stopped')}
                </span>
              </>
            )}
          </p>
        </div>
        <div className="ml-auto flex items-center gap-2">
          <Button type="button" variant="outline" size="lg" onClick={onCancel}>
            {t('common.cancel')}
          </Button>
          <Button type="submit" size="lg" disabled={isSaving}>
            <Check aria-hidden="true" />
            {isSaving
              ? t('adminPlans.drawer.saving')
              : t(isNew ? 'adminPlans.editor.create' : 'adminPlans.drawer.save')}
          </Button>
        </div>
      </header>

      <div className="grid grid-cols-1 items-start gap-5.5 lg:grid-cols-[minmax(0,1fr)_minmax(0,26rem)]">
        <div className="flex flex-col gap-5.5">
          <section className={CARD_CLASS}>
            <SectionHeading icon={<Layers className="size-4" />}>{t('adminPlans.drawer.sectionPlan')}</SectionHeading>
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
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
            <div className="sm:max-w-md">
              <Field
                label={t('adminPlans.drawer.code')}
                error={errors.code?.message}
                hint={t(isNew ? 'adminPlans.editor.codeHintNew' : 'adminPlans.drawer.codeHint')}
              >
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
                      placeholder={isNew ? t('adminPlans.editor.codePlaceholder') : undefined}
                      className={cn('h-10', !isNew && 'pl-9.5')}
                      {...fieldControl}
                    />
                  </span>
                )}
              </Field>
            </div>
          </section>

          <section className={CARD_CLASS}>
            <SectionHeading icon={<CreditCard className="size-4" />}>{t('adminPlans.drawer.sectionPrice')}</SectionHeading>
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
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
              <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
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
        </div>

        <div className="flex flex-col gap-5.5">
          <section className={CARD_CLASS} aria-labelledby="plan-editor-preview">
            <SectionHeading icon={<Star className="size-4" />}>
              <span id="plan-editor-preview">{t('adminPlans.drawer.sectionPreview')}</span>
            </SectionHeading>
            <div className="flex flex-col gap-3 rounded-xl border border-line px-4 py-4">
              <div className="flex items-center justify-between gap-3">
                <span className="truncate text-copy font-semibold text-ink">{previewName}</span>
                {!isNew || offered ? (
                  <span
                    className={cn(
                      'inline-flex shrink-0 items-center gap-1.5 rounded-full px-2 py-0.5 text-meta font-semibold',
                      offered ? 'bg-success-soft text-success-ink' : 'bg-sunken text-ink-2',
                    )}
                  >
                    {offered && <span className="size-1.5 rounded-full bg-current" aria-hidden="true" />}
                    {t(offered ? 'adminPlans.preview.offered' : 'adminPlans.preview.notOffered')}
                  </span>
                ) : null}
              </div>
              <p className="flex items-baseline gap-1.5 text-ink">
                <span className="text-stat font-semibold">
                  {parsedPrice === null
                    ? t('adminPlans.editor.noPrice', { currency })
                    : formatPlanMoney(newPriceMinor, currency, i18n.language)}
                </span>
                <span className="text-body text-muted-foreground">{t('adminPlans.editor.perMonthSuffix')}</span>
              </p>
              {hasSummary ? (
                <>
                  <p className="text-copy text-ink-2">
                    {secondRole === 'addon'
                      ? addOn !== null && addOn > 0
                        ? t('adminPlans.editor.secondRoleLine', {
                            price: formatPlanMoney(inputToMinor(values.addOnPrice ?? ''), currency, i18n.language),
                          })
                        : null
                      : t('adminPlans.editor.secondRoleIncludedLine')}
                  </p>
                  <p className="text-copy text-ink-2">
                    {unlimited
                      ? t('adminPlans.editor.unlimitedClientsLine')
                      : t('adminPlans.editor.clientsLine', { count: cap })}
                  </p>
                </>
              ) : (
                <p className="text-copy text-ink-2">{t('adminPlans.editor.summaryHint')}</p>
              )}
              {hasSummary && (
                <p className="flex items-center gap-2 border-t border-line pt-3 text-copy text-ink-2">
                  <Check className="size-4 text-success" aria-hidden="true" />
                  {t('adminPlans.editor.featuresIncluded', { count: featureCount, total: EDITOR_FEATURES.length })}
                </p>
              )}
            </div>
            <Controller
              control={control}
              name="isActive"
              render={({ field }) => (
                <PlanCheckRow
                  label={t('adminPlans.drawer.offer')}
                  hint={
                    isFree
                      ? t('adminPlans.drawer.offerFreeHint')
                      : t(isNew ? 'adminPlans.editor.offerHintNew' : 'adminPlans.drawer.offerHint')
                  }
                  checked={isFree ? true : field.value}
                  disabled={isFree}
                  onCheckedChange={field.onChange}
                />
              )}
            />
          </section>

          <section className={CARD_CLASS}>
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
                    <PlanCheckRow
                      label={t('adminPlans.unlimited')}
                      checked={field.value}
                      onCheckedChange={field.onChange}
                    />
                  )}
                />
              </div>
            </div>
          </section>
        </div>
      </div>

      <section className={CARD_CLASS} aria-labelledby="plan-editor-features">
        <div className="flex items-center justify-between gap-3">
          <SectionHeading icon={<Check className="size-4" />}>
            <span id="plan-editor-features">{t('adminPlans.drawer.sectionFeatures')}</span>
          </SectionHeading>
          <span className="text-body text-muted-foreground" aria-live="polite">
            {t('adminPlans.editor.featuresOn', { count: featureCount, total: EDITOR_FEATURES.length })}
          </span>
        </div>
        <div className="grid grid-cols-1 gap-x-8 gap-y-5 sm:grid-cols-2 lg:grid-cols-3">
          {EDITOR_FEATURES.map(({ flag, key }) => (
            <Controller
              key={flag}
              control={control}
              name={flag}
              render={({ field }) => {
                const isPerClient = flag === 'canUsePerClientCheckInConfig';
                return (
                  <div className="flex flex-col gap-1">
                    <PlanCheckRow
                      label={t(editorFeatureLabelKey(key))}
                      hint={t(editorFeatureHintKey(key))}
                      checked={isPerClient ? weeklyCheckIns && field.value : field.value}
                      disabled={isPerClient && !weeklyCheckIns}
                      onCheckedChange={(checked) => {
                        field.onChange(checked);
                        if (flag === 'canUseWeeklyCheckIns' && !checked) {
                          setValue('canUsePerClientCheckInConfig', false, { shouldDirty: true });
                        }
                      }}
                    />
                    {isPerClient && errors.canUsePerClientCheckInConfig?.message && (
                      <span role="alert" className="pl-7 text-meta text-destructive">
                        {t(errors.canUsePerClientCheckInConfig.message)}
                      </span>
                    )}
                  </div>
                );
              }}
            />
          ))}
        </div>
      </section>
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
          <PlanCheckRow
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
