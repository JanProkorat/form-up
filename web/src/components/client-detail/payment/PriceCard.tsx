import { useMemo, useRef } from 'react';
import { Controller, useForm, useWatch } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { Box, ChevronDown, Tag, X } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Skeleton } from '@/components/ui/skeleton';
import { Switch } from '@/components/ui/switch';
import { Field } from '@/components/profile/ProfileSection';
import {
  DiscountDuration,
  PaymentCurrency,
  type ClientPaymentSummaryTermsDto,
  type CoachDiscountDto,
  type CoachPackageDto,
  type GetClientPaymentSummaryResponse,
} from '@/api/client-payments';
import {
  useCheckCollaborationEnded,
  useSetClientPaymentTerms,
  usePaymentDiscounts,
  usePaymentPackages,
} from '@/hooks/useClientPaymentQueries';
import {
  CURRENCY_MISMATCH_CODE,
  paymentErrorCode,
} from '@/components/client-detail/payment/paymentErrors';
import {
  billingSuffix,
  discountFitsPackage,
  discountValueLabel,
  previewDiscountAmount,
} from '@/components/client-detail/payment/paymentModel';
import { getErrorStatus, showApiError, showError, showSuccess } from '@/lib/api-errors';
import { formatMoney, parseAmount } from '@/lib/money';
import { formatMonthName, todayIso } from '@/lib/payment-format';

const PRICE_MAX = 9999999.99;

const SELECT_CLASS =
  'flex h-10 w-full min-w-0 appearance-none rounded-field border border-input bg-background py-1 pr-9 pl-9.5 text-copy text-foreground outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 aria-invalid:border-destructive disabled:opacity-50';

interface FormValues {
  packageId: string;
  discountId: string;
  customPriceOn: boolean;
  customPrice: string;
  firstPeriodStart: string;
}

function initialValues(terms: ClientPaymentSummaryTermsDto | undefined): FormValues {
  return {
    packageId: terms?.packageId ?? '',
    discountId: terms?.discountId ?? '',
    customPriceOn: terms?.customPrice != null,
    customPrice: terms?.customPrice == null ? '' : String(terms.customPrice),
    firstPeriodStart: todayIso(),
  };
}

/** The package as the picker knows it; an archived package still in force is rebuilt from the terms. */
function packageFromTerms(terms: ClientPaymentSummaryTermsDto): CoachPackageDto {
  return {
    packageId: terms.packageId,
    name: terms.packageName,
    price: terms.packagePrice,
    currency: terms.currency,
    billing: terms.billing,
    intervalWeeks: terms.intervalWeeks,
  };
}

/** The active packages, plus an archived one still in force, rebuilt from the terms so the select shows it. */
function packageOptionsOf(
  packages: CoachPackageDto[],
  terms: ClientPaymentSummaryTermsDto | undefined,
): CoachPackageDto[] {
  if (terms?.packageId && !packages.some((option) => option.packageId === terms.packageId)) {
    return [packageFromTerms(terms), ...packages];
  }
  return packages;
}

/** The chosen discount; one already in force that was archived since is rebuilt from the terms. */
function findDiscount(
  discountId: string,
  discounts: CoachDiscountDto[],
  terms: ClientPaymentSummaryTermsDto | undefined,
): CoachDiscountDto | undefined {
  if (!discountId) {
    return undefined;
  }
  const known = discounts.find((discount) => discount.discountId === discountId);
  if (known) {
    return known;
  }
  if (terms?.discountId === discountId) {
    return {
      discountId: terms.discountId,
      name: terms.discountName,
      type: terms.discountType,
      value: terms.discountValue,
      currency: terms.currency,
    };
  }
  return undefined;
}

interface FormProps {
  clientId: string;
  clientName: string;
  summary: GetClientPaymentSummaryResponse;
  packages: CoachPackageDto[];
  discounts: CoachDiscountDto[];
}

function PriceForm({ clientId, clientName, summary, packages, discounts }: FormProps) {
  const { t, i18n } = useTranslation();
  const language = i18n.language;
  const terms = summary.terms;
  const setTermsMutation = useSetClientPaymentTerms(clientId);
  const checkCollaborationEnded = useCheckCollaborationEnded(clientId);
  const packageSelectRef = useRef<HTMLSelectElement | null>(null);

  const schema = useMemo(
    () =>
      z
        .object({
          packageId: z.string().min(1, t('payments.client.terms.packageRequired')),
          discountId: z.string(),
          customPriceOn: z.boolean(),
          customPrice: z.string(),
          firstPeriodStart: z.string(),
        })
        .superRefine((values, ctx) => {
          if (!values.customPriceOn) {
            return;
          }
          const price = parseAmount(values.customPrice);
          if (price === null || price <= 0 || price > PRICE_MAX) {
            ctx.addIssue({ code: 'custom', path: ['customPrice'], message: t('payments.client.terms.customPriceInvalid') });
          }
        })
        .superRefine((values, ctx) => {
          if (!terms && values.firstPeriodStart < todayIso()) {
            ctx.addIssue({ code: 'custom', path: ['firstPeriodStart'], message: t('payments.client.terms.startPast') });
          }
        }),
    [t, terms],
  );

  const {
    register,
    control,
    handleSubmit,
    setValue,
    reset,
    formState: { errors, isDirty },
  } = useForm<FormValues>({
    resolver: zodResolver(schema),
    mode: 'onTouched',
    defaultValues: initialValues(terms),
  });

  const packageId = useWatch({ control, name: 'packageId' });
  const discountId = useWatch({ control, name: 'discountId' });
  const customPriceOn = useWatch({ control, name: 'customPriceOn' });
  const customPriceText = useWatch({ control, name: 'customPrice' });

  const packageOptions = packageOptionsOf(packages, terms);
  const selectedPackage = packageOptions.find((option) => option.packageId === packageId);
  const currency: PaymentCurrency | undefined = selectedPackage?.currency;
  const discountOptions = discounts.filter((discount) => discountFitsPackage(discount, selectedPackage));
  const selectedDiscount = findDiscount(discountId, discounts, terms);

  const customPrice = customPriceOn ? (parseAmount(customPriceText) ?? 0) : 0;
  const hasCustomPrice = customPrice > 0;
  const basePrice = selectedPackage?.price ?? 0;
  const discountAmount = hasCustomPrice ? 0 : previewDiscountAmount(selectedDiscount, basePrice);
  const payable = hasCustomPrice ? customPrice : Math.max(0, basePrice - discountAmount);
  const suffix = billingSuffix(t, selectedPackage?.billing, selectedPackage?.intervalWeeks);

  // Saved terms show what the server bills (a "first N payments" discount may be used up);
  // only unsaved edits are previewed in the browser.
  const serverPrice = terms && !isDirty ? summary.price : undefined;
  const serverCurrency = terms?.currency;
  const showServer = serverPrice?.amount != null && serverCurrency != null;
  const shownCurrency = showServer ? serverCurrency : currency;
  const shownIsCustom = showServer ? !!serverPrice?.isCustomPrice : hasCustomPrice;
  const shownBase = showServer ? (serverPrice?.basePrice ?? basePrice) : basePrice;
  const shownDiscountAmount = showServer ? (serverPrice?.discountAmount ?? 0) : discountAmount;
  const shownDiscountLabel = showServer ? serverPrice?.discountLabel : selectedDiscount?.name;
  const shownCustomPrice = showServer ? (serverPrice?.amount ?? 0) : customPrice;
  const shownPayable = showServer ? (serverPrice?.amount ?? 0) : payable;
  const firstPayments =
    !showServer && !hasCustomPrice && selectedDiscount?.duration === DiscountDuration.FirstPayments
      ? selectedDiscount?.durationPayments
      : undefined;

  const submit = async (values: FormValues) => {
    try {
      // mutateAsync, not mutate callbacks: the refetch can re-key and unmount this form first.
      await setTermsMutation.mutateAsync({
        packageId: values.packageId,
        discountId: values.customPriceOn || !values.discountId ? undefined : values.discountId,
        customPrice: values.customPriceOn ? (parseAmount(values.customPrice) ?? undefined) : undefined,
        firstPeriodStart: terms ? undefined : values.firstPeriodStart,
      });
    } catch (error) {
      if (paymentErrorCode(error) === CURRENCY_MISMATCH_CODE) {
        showError('payments.client.terms.currencyMismatch');
        return;
      }
      // A bare 404 is either an ended link or a vanished package; the summary tells which.
      if (paymentErrorCode(error) === null && getErrorStatus(error) === 404) {
        void checkCollaborationEnded().then((ended) => {
          if (ended) {
            showError('payments.client.terms.collaborationEnded');
          } else {
            showApiError(error, 'payments.client.terms.saveError');
          }
        });
        return;
      }
      showApiError(error, 'payments.client.terms.saveError');
      return;
    }
    reset(values);
    showSuccess('payments.client.terms.saved');
  };

  const appliesFrom = terms
    ? summary.nextPeriodStart
      ? t('payments.client.terms.appliesFrom', { month: formatMonthName(summary.nextPeriodStart, language) })
      : t('payments.client.terms.appliesFromNext')
    : t('payments.client.terms.startsFirst');

  return (
    <form
      noValidate
      onSubmit={handleSubmit(submit)}
      className="flex flex-col gap-3.5 rounded-2xl border border-line bg-card p-5 shadow-panel"
      aria-label={t('payments.client.terms.title', { name: clientName })}
    >
      <div className="flex items-center gap-2.5">
        <h3 className="text-copy font-semibold text-ink">{t('payments.client.terms.title', { name: clientName })}</h3>
        {terms && (
          <button
            type="button"
            onClick={() => packageSelectRef.current?.focus()}
            className="ml-auto rounded-sm text-body font-semibold text-ink outline-none hover:underline focus-visible:ring-3 focus-visible:ring-ring/50"
          >
            {t('payments.client.terms.changePackage')}
          </button>
        )}
      </div>

      {packageOptions.length === 0 ? (
        <p className="text-body text-muted-foreground">
          {t('payments.client.terms.noPackages')}{' '}
          <Link to="/packages" className="font-semibold text-ink underline underline-offset-2">
            {t('payments.client.terms.createPackage')}
          </Link>
        </p>
      ) : (
        <>
          <Field
            label={t('payments.client.terms.package')}
            htmlFor="cp-pkg"
            error={errors.packageId?.message}
          >
            <div className="relative">
              <Box
                className="pointer-events-none absolute top-1/2 left-3 size-4 -translate-y-1/2 text-muted-foreground"
                aria-hidden="true"
              />
              <Controller
                control={control}
                name="packageId"
                render={({ field }) => (
                  <select
                    id="cp-pkg"
                    ref={(element) => {
                      field.ref(element);
                      packageSelectRef.current = element;
                    }}
                    className={SELECT_CLASS}
                    aria-invalid={!!errors.packageId}
                    aria-describedby="cp-pkg-message"
                    value={field.value}
                    onBlur={field.onBlur}
                    onChange={(event) => {
                      field.onChange(event.target.value);
                      const next = packageOptions.find((option) => option.packageId === event.target.value);
                      const current = discounts.find((discount) => discount.discountId === discountId);
                      // A discount that does not fit the new package would be rejected by the server.
                      if (current && !discountFitsPackage(current, next)) {
                        setValue('discountId', '', { shouldDirty: true });
                      }
                    }}
                  >
                    {!field.value && <option value="">{t('payments.client.terms.packagePlaceholder')}</option>}
                    {packageOptions.map((option) => (
                      <option key={option.packageId} value={option.packageId}>
                        {option.name} · {formatMoney(option.price ?? 0, option.currency ?? PaymentCurrency.CZK, language)}{' '}
                        {billingSuffix(t, option.billing, option.intervalWeeks)}
                      </option>
                    ))}
                  </select>
                )}
              />
              <ChevronDown
                className="pointer-events-none absolute top-1/2 right-3 size-4 -translate-y-1/2 text-muted-foreground"
                aria-hidden="true"
              />
            </div>
          </Field>

          <div className="flex flex-col gap-1.75">
            <span className="text-meta font-medium text-ink-2">{t('payments.client.terms.discount')}</span>
            {selectedDiscount ? (
              <div className="flex h-10 items-center gap-2.5 rounded-field border border-input pr-2 pl-3 text-copy text-ink">
                <Tag className="size-4 text-training-ink" aria-hidden="true" />
                <span className="truncate">{selectedDiscount.name}</span>
                <span className="text-body text-muted-foreground">
                  {discountValueLabel(selectedDiscount.type, selectedDiscount.value, selectedDiscount.currency, language)}
                </span>
                <Button
                  type="button"
                  variant="ghost"
                  size="icon-sm"
                  className="ml-auto text-muted-foreground"
                  aria-label={t('payments.client.terms.removeDiscount')}
                  onClick={() => setValue('discountId', '', { shouldDirty: true })}
                >
                  <X aria-hidden="true" />
                </Button>
              </div>
            ) : (
              <div className="relative">
                <Tag
                  className="pointer-events-none absolute top-1/2 left-3 size-4 -translate-y-1/2 text-muted-foreground"
                  aria-hidden="true"
                />
                <select
                  aria-label={t('payments.client.terms.discount')}
                  className={SELECT_CLASS}
                  disabled={customPriceOn || discountOptions.length === 0}
                  value=""
                  onChange={(event) => setValue('discountId', event.target.value, { shouldDirty: true })}
                >
                  <option value="">
                    {customPriceOn
                      ? t('payments.client.terms.discountOffCustom')
                      : discountOptions.length === 0
                        ? t('payments.client.terms.noDiscounts')
                        : t('payments.client.terms.noDiscount')}
                  </option>
                  {discountOptions.map((discount) => (
                    <option key={discount.discountId} value={discount.discountId}>
                      {discount.name} {discountValueLabel(discount.type, discount.value, discount.currency, language)}
                    </option>
                  ))}
                </select>
                <ChevronDown
                  className="pointer-events-none absolute top-1/2 right-3 size-4 -translate-y-1/2 text-muted-foreground"
                  aria-hidden="true"
                />
              </div>
            )}
          </div>

          <div className="flex flex-col gap-2.5">
            <div className="flex items-center gap-3">
              <div className="flex min-w-0 flex-col gap-0.75">
                <span id="cp-custom-label" className="text-copy font-semibold text-ink">
                  {t('payments.client.terms.customPrice')}
                </span>
                <span className="text-body text-muted-foreground">
                  {t('payments.client.terms.customPriceHint', { name: clientName })}
                </span>
              </div>
              <span className="ml-auto">
                <Controller
                  control={control}
                  name="customPriceOn"
                  render={({ field }) => (
                    <Switch
                      aria-labelledby="cp-custom-label"
                      checked={field.value}
                      onCheckedChange={(checked) => {
                        field.onChange(checked);
                        if (checked) {
                          setValue('discountId', '', { shouldDirty: true });
                        }
                      }}
                    />
                  )}
                />
              </span>
            </div>
            {customPriceOn && (
              <Field
                label={t('payments.client.terms.customPriceLabel')}
                htmlFor="cp-custom-price"
                aside={currency}
                error={errors.customPrice?.message}
              >
                <Input
                  id="cp-custom-price"
                  inputMode="decimal"
                  autoComplete="off"
                  className="h-10 tabular-nums"
                  aria-invalid={!!errors.customPrice}
                  aria-describedby="cp-custom-price-message"
                  {...register('customPrice')}
                />
              </Field>
            )}
          </div>

          {!terms && (
            <Field
              label={t('payments.client.terms.firstPeriod')}
              htmlFor="cp-first-period"
              error={errors.firstPeriodStart?.message}
            >
              <Input
                id="cp-first-period"
                type="date"
                min={todayIso()}
                className="h-10"
                aria-invalid={!!errors.firstPeriodStart}
                aria-describedby="cp-first-period-message"
                {...register('firstPeriodStart')}
              />
            </Field>
          )}

          {selectedPackage && shownCurrency && (
            <dl className="grid grid-cols-[1fr_auto] gap-y-1.5 rounded-xl bg-sunken px-3.5 py-3 text-body">
              {!shownIsCustom && (
                <>
                  <dt className="text-muted-foreground">{t('payments.client.terms.packagePrice')}</dt>
                  <dd className="text-right text-ink">{formatMoney(shownBase, shownCurrency, language)}</dd>
                </>
              )}
              {shownDiscountLabel && !shownIsCustom && shownDiscountAmount > 0 && (
                <>
                  <dt className="text-muted-foreground">{shownDiscountLabel}</dt>
                  <dd className="text-right text-ink">−{formatMoney(shownDiscountAmount, shownCurrency, language)}</dd>
                </>
              )}
              {shownIsCustom && (
                <>
                  <dt className="text-muted-foreground">{t('payments.client.terms.customPrice')}</dt>
                  <dd className="text-right text-ink">{formatMoney(shownCustomPrice, shownCurrency, language)}</dd>
                </>
              )}
              <dt className="mt-1.5 border-t border-line pt-1.5 font-semibold text-ink">
                {t('payments.client.terms.pays', { name: clientName })}
              </dt>
              <dd className="mt-1.5 border-t border-line pt-1.5 text-right font-bold text-ink" data-testid="terms-payable">
                {firstPayments != null
                  ? t('payments.client.terms.paysFirstPayments', {
                      count: firstPayments,
                      amount: formatMoney(shownPayable, shownCurrency, language),
                      full: formatMoney(shownBase, shownCurrency, language),
                      suffix,
                    })
                  : `${formatMoney(shownPayable, shownCurrency, language)} ${suffix}`}
              </dd>
            </dl>
          )}

          <span className="text-meta text-muted-foreground">{appliesFrom}</span>

          {(isDirty || !terms) && (
            <div className="flex justify-end gap-2">
              {isDirty && terms && (
                <Button type="button" variant="outline" size="lg" onClick={() => reset(initialValues(terms))}>
                  {t('common.cancel')}
                </Button>
              )}
              <Button type="submit" size="lg" disabled={setTermsMutation.isPending || (!isDirty && !!terms)}>
                {setTermsMutation.isPending ? t('common.saving') : t('payments.client.terms.save')}
              </Button>
            </div>
          )}
        </>
      )}
    </form>
  );
}

interface Props {
  clientId: string;
  clientName: string;
  summary: GetClientPaymentSummaryResponse;
}

/** "Price for {client}": the package, discount or custom price a client pays under, with a live breakdown. */
export default function PriceCard({ clientId, clientName, summary }: Props) {
  const { t } = useTranslation();
  const packagesQuery = usePaymentPackages(true);
  const discountsQuery = usePaymentDiscounts(true);

  if (packagesQuery.isPending || discountsQuery.isPending) {
    return <Skeleton className="h-72 w-full rounded-2xl" />;
  }
  if (packagesQuery.isError || discountsQuery.isError) {
    return (
      <div role="alert" className="flex flex-col items-start gap-2 rounded-2xl border border-line bg-card p-5 shadow-panel">
        <p className="text-body text-muted-foreground">{t('payments.client.terms.loadError')}</p>
        <Button
          type="button"
          variant="outline"
          onClick={() => {
            void packagesQuery.refetch();
            void discountsQuery.refetch();
          }}
        >
          {t('payments.client.retry')}
        </Button>
      </div>
    );
  }

  return (
    <PriceForm
      // A saved terms change swaps the baseline the form resets to.
      key={`${summary.terms?.packageId ?? 'none'}-${summary.terms?.discountId ?? ''}-${summary.terms?.customPrice ?? ''}-${summary.terms?.effectiveFromUtc ?? ''}`}
      clientId={clientId}
      clientName={clientName}
      summary={summary}
      packages={packagesQuery.data}
      discounts={discountsQuery.data}
    />
  );
}
