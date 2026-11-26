import { useMemo, useState } from 'react';
import { Controller, useForm, useWatch } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { Bell, Calendar, ChevronDown, Eye, Mail, Send, Wallet } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { Input } from '@/components/ui/input';
import { Skeleton } from '@/components/ui/skeleton';
import { Sheet, SheetContent, SheetDescription, SheetFooter, SheetHeader, SheetTitle } from '@/components/ui/sheet';
import { Field } from '@/components/profile/ProfileSection';
import { RequestSectionHeading } from '@/components/client-detail/photos/RequestPhotosStatus';
import { ClientPaymentKind, type ClientPaymentRowDto } from '@/api/client-payments';
import { useRequestClientPayment } from '@/hooks/useClientPaymentQueries';
import { usePaymentSettings } from '@/hooks/usePaymentSettingsQueries';
import {
  BANK_ACCOUNT_NOT_SET,
  isAlreadySettled,
  isRateLimited,
  paymentErrorCode,
} from '@/components/client-detail/payment/paymentErrors';
import { getApiErrorMessage, showApiError, showSuccess } from '@/lib/api-errors';
import { parseAmount } from '@/lib/money';
import {
  addDaysIso,
  formatPaymentAmount,
  formatPeriodLabel,
  formatShortDay,
  groupIban,
  todayIso,
} from '@/lib/payment-format';
import { useAuthStore } from '@/stores/auth';

const AMOUNT_MAX = 9999999.99;
const DEFAULT_DUE_DAYS = 7;

const SELECT_CLASS =
  'flex h-10 w-full min-w-0 appearance-none rounded-field border border-input bg-background py-1 pr-9 pl-3 text-copy text-foreground outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 aria-invalid:border-destructive disabled:opacity-50';

interface FormValues {
  paymentId: string;
  dueDate: string;
  amount: string;
  sendNotification: boolean;
  sendEmail: boolean;
}

/** The due date the form starts from: the row's own when it is still ahead, else the coach's payment term. */
function defaultDueDate(row: ClientPaymentRowDto | undefined, dueDays: number): string {
  const today = todayIso();
  const own = row?.dueDate?.slice(0, 10);
  return own && own >= today ? own : addDaysIso(today, dueDays);
}

interface FormProps {
  clientId: string;
  clientName: string;
  rows: readonly ClientPaymentRowDto[];
  initialPaymentId: string;
  onClose: () => void;
}

/** Mounted only while the drawer is open, so every opening starts from the defaults. */
function RequestForm({ clientId, clientName, rows, initialPaymentId, onClose }: FormProps) {
  const { t, i18n } = useTranslation();
  const language = i18n.language;
  const coachName = useAuthStore((state) => state.user?.firstName?.trim()) || t('payments.client.request.coachFallback');
  const settingsQuery = usePaymentSettings();
  const requestMutation = useRequestClientPayment(clientId);
  const dueDays = settingsQuery.data?.paymentDueDays ?? DEFAULT_DUE_DAYS;
  const [editingAmount, setEditingAmount] = useState(false);
  const [submitError, setSubmitError] = useState<{ message: string; needsBankAccount: boolean } | null>(null);

  const schema = useMemo(
    () =>
      z
        .object({
          paymentId: z.string().min(1),
          dueDate: z
            .string()
            .min(1, t('payments.client.request.dueRequired'))
            .refine((value) => value >= todayIso(), t('payments.client.request.duePast')),
          amount: z.string(),
          sendNotification: z.boolean(),
          sendEmail: z.boolean(),
        })
        .refine((values) => values.sendNotification || values.sendEmail, {
          message: t('payments.client.request.channelRequired'),
          path: ['sendEmail'],
        }),
    [t],
  );

  const initialRow = rows.find((row) => row.paymentId === initialPaymentId) ?? rows[0];

  const {
    register,
    control,
    handleSubmit,
    setValue,
    formState: { errors },
  } = useForm<FormValues>({
    resolver: zodResolver(schema),
    mode: 'onTouched',
    defaultValues: {
      paymentId: initialRow?.paymentId ?? '',
      dueDate: defaultDueDate(initialRow, dueDays),
      amount: initialRow?.amount == null ? '' : String(initialRow.amount),
      sendNotification: true,
      sendEmail: true,
    },
  });

  const paymentId = useWatch({ control, name: 'paymentId' });
  const dueDate = useWatch({ control, name: 'dueDate' });
  const amountText = useWatch({ control, name: 'amount' });
  const sendNotification = useWatch({ control, name: 'sendNotification' });
  const sendEmail = useWatch({ control, name: 'sendEmail' });
  const row = rows.find((candidate) => candidate.paymentId === paymentId) ?? initialRow;

  const periodLabelOf = (candidate: ClientPaymentRowDto) =>
    candidate.title ?? formatPeriodLabel(candidate.periodStart, candidate.periodEnd, language);

  const isOneOff = row?.kind === ClientPaymentKind.OneOff;

  const amountValue = editingAmount ? parseAmount(amountText) : row?.amount;
  const amountInvalid =
    editingAmount && !(amountValue != null && amountValue > 0 && amountValue <= AMOUNT_MAX);

  const submit = async (values: FormValues) => {
    if (amountInvalid || !row?.paymentId) {
      return;
    }
    setSubmitError(null);
    let response;
    try {
      // mutateAsync, not mutate callbacks: the refetch can unmount this form first.
      response = await requestMutation.mutateAsync({
        paymentId: values.paymentId,
        request: {
          dueDate: values.dueDate,
          amount: editingAmount ? (parseAmount(values.amount) ?? undefined) : undefined,
          sendNotification: values.sendNotification,
          sendEmail: values.sendEmail,
        },
      });
    } catch (error) {
      if (isAlreadySettled(error)) {
        showApiError(error, 'payments.client.request.error');
        onClose();
        return;
      }
      setSubmitError({
        message: isRateLimited(error)
          ? t('apiErrors.PAYMENT_REQUEST_RATE_LIMITED')
          : getApiErrorMessage(error, 'payments.client.request.error'),
        needsBankAccount: paymentErrorCode(error) === BANK_ACCOUNT_NOT_SET,
      });
      return;
    }
    showSuccess(
      values.sendEmail && response.emailSent === false
        ? 'payments.client.request.sentEmailFailed'
        : 'payments.client.request.sent',
    );
    onClose();
  };

  if (!row) {
    return (
      <div className="flex flex-1 flex-col gap-3 px-6 py-5">
        <p className="text-copy text-muted-foreground">{t('payments.client.request.nothingToRequest')}</p>
      </div>
    );
  }

  const previewDue = formatShortDay(dueDate || row.dueDate, language);
  const previewAmount = formatPaymentAmount(
    amountValue != null && Number.isFinite(amountValue) ? amountValue : undefined,
    row.currency,
    language,
  );

  return (
    <form onSubmit={handleSubmit(submit)} className="flex min-h-0 flex-1 flex-col" noValidate>
      <div className="flex flex-1 flex-col gap-6 overflow-y-auto px-6 py-5">
        <section className="flex flex-col gap-3">
          <RequestSectionHeading icon={<Wallet className="size-4" />}>
            {t('payments.client.request.payment.heading')}
          </RequestSectionHeading>
          <Field
            label={t('payments.client.request.for')}
            htmlFor="request-payment-for"
            hint={t('payments.client.request.forHint')}
          >
            <div className="relative">
              <Controller
                control={control}
                name="paymentId"
                render={({ field }) => (
                  <select
                    id="request-payment-for"
                    aria-describedby="request-payment-for-message"
                    className={SELECT_CLASS}
                    disabled={requestMutation.isPending}
                    value={field.value}
                    onChange={(event) => {
                      const next = rows.find((candidate) => candidate.paymentId === event.target.value);
                      field.onChange(event.target.value);
                      setValue('dueDate', defaultDueDate(next, dueDays));
                      setValue('amount', next?.amount == null ? '' : String(next.amount));
                      setEditingAmount(false);
                    }}
                  >
                    {rows.map((candidate) => (
                      <option key={candidate.paymentId} value={candidate.paymentId}>
                        {periodLabelOf(candidate)} · {formatPaymentAmount(candidate.amount, candidate.currency, language)}
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
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
            <Field
              label={t('payments.client.request.dueDate')}
              htmlFor="request-payment-due"
              error={errors.dueDate?.message}
            >
              <div className="relative">
                <Calendar
                  className="pointer-events-none absolute top-1/2 left-3 size-4 -translate-y-1/2 text-muted-foreground"
                  aria-hidden="true"
                />
                <Input
                  id="request-payment-due"
                  type="date"
                  min={todayIso()}
                  className="h-10 pl-9.5"
                  aria-invalid={!!errors.dueDate}
                  aria-describedby="request-payment-due-message"
                  {...register('dueDate')}
                />
              </div>
            </Field>
            <Field
              label={t('payments.client.request.amount')}
              htmlFor="request-payment-amount"
              hint={
                editingAmount
                  ? t('payments.client.request.amountEditing')
                  : isOneOff
                    ? t('payments.client.request.amountHintOneOff')
                    : t('payments.client.request.amountHint')
              }
              error={amountInvalid ? t('payments.client.request.amountInvalid') : undefined}
              aside={row.currency}
            >
              <div className="flex items-center gap-2">
                <Input
                  id="request-payment-amount"
                  inputMode="decimal"
                  readOnly={!editingAmount}
                  className="h-10 tabular-nums read-only:bg-sunken read-only:text-muted-foreground"
                  aria-invalid={amountInvalid}
                  aria-describedby="request-payment-amount-message"
                  {...register('amount')}
                />
                {!editingAmount && (
                  <Button type="button" variant="ghost" size="sm" className="shrink-0 font-semibold" onClick={() => setEditingAmount(true)}>
                    {t('payments.client.request.changeAmount')}
                  </Button>
                )}
              </div>
            </Field>
          </div>
        </section>

        <section className="flex flex-col gap-3">
          <RequestSectionHeading icon={<Send className="size-4" />}>
            {t('payments.client.request.sendBy.heading')}
          </RequestSectionHeading>
          <div className="flex flex-col gap-2">
            <Controller
              control={control}
              name="sendNotification"
              render={({ field }) => (
                <label className="flex cursor-pointer items-center gap-3 rounded-field border border-border px-3.5 py-3 text-copy text-ink">
                  <Checkbox
                    checked={field.value}
                    onCheckedChange={(checked) => field.onChange(checked === true)}
                    onBlur={field.onBlur}
                  />
                  <Bell className="size-4 text-muted-foreground" aria-hidden="true" />
                  {t('payments.client.request.sendBy.notification')}
                </label>
              )}
            />
            <Controller
              control={control}
              name="sendEmail"
              render={({ field }) => (
                <label className="flex cursor-pointer items-center gap-3 rounded-field border border-border px-3.5 py-3 text-copy text-ink">
                  <Checkbox
                    checked={field.value}
                    onCheckedChange={(checked) => field.onChange(checked === true)}
                    onBlur={field.onBlur}
                  />
                  <Mail className="size-4 text-muted-foreground" aria-hidden="true" />
                  {t('payments.client.request.sendBy.email')}
                </label>
              )}
            />
            {errors.sendEmail?.message && (
              <p role="alert" className="text-meta text-error">
                {errors.sendEmail.message}
              </p>
            )}
          </div>
        </section>

        <section className="flex flex-col gap-3">
          <RequestSectionHeading icon={<Eye className="size-4" />}>
            {t('payments.client.request.preview.heading', { name: clientName })}
          </RequestSectionHeading>
          <div className="flex flex-col gap-2 rounded-2xl border border-border bg-sunken p-4">
            <span className="text-copy font-semibold text-ink">
              {t('payments.client.request.preview.asks', { coach: coachName, amount: previewAmount })}
            </span>
            <span className="text-meta text-muted-foreground">
              {[isOneOff ? undefined : row.packageName, periodLabelOf(row), t('payments.client.request.preview.due', { date: previewDue })]
                .filter(Boolean)
                .join(' · ')}
            </span>
            {settingsQuery.isPending ? (
              <Skeleton className="h-16 w-full" />
            ) : (
              <dl className="mt-1 grid grid-cols-[auto_minmax(0,1fr)] gap-x-4 gap-y-1.5 text-body">
                <dt className="text-muted-foreground">{t('payments.client.request.preview.account')}</dt>
                <dd className="font-medium break-all text-ink tabular-nums">
                  {groupIban(settingsQuery.data?.iban) || '—'}
                </dd>
                <dt className="text-muted-foreground">{t('payments.client.request.preview.variableSymbol')}</dt>
                <dd className="font-medium text-ink tabular-nums">
                  {row.variableSymbol ?? t('payments.client.request.preview.symbolOnSend')}
                </dd>
                <dt className="text-muted-foreground">{t('payments.client.request.preview.message')}</dt>
                <dd className="font-medium text-ink">{row.message ?? t('payments.client.request.preview.messageOnSend')}</dd>
              </dl>
            )}
          </div>
        </section>

        {submitError && (
          <div role="alert" className="flex flex-col gap-1.5 rounded-field bg-error-soft px-3.5 py-3 text-body text-error">
            <span>{submitError.message}</span>
            {submitError.needsBankAccount && (
              <Link to="/settings" className="font-semibold underline underline-offset-2">
                {t('payments.client.request.openSettings')}
              </Link>
            )}
          </div>
        )}
      </div>

      <SheetFooter className="flex-row items-center gap-2 border-t border-border px-6 py-4">
        <span className="mr-auto text-meta text-muted-foreground">{t('payments.client.request.footnote')}</span>
        <Button type="button" variant="outline" size="lg" onClick={onClose}>
          {t('common.cancel')}
        </Button>
        <Button
          type="submit"
          size="lg"
          disabled={requestMutation.isPending || amountInvalid || (!sendNotification && !sendEmail)}
        >
          <Send aria-hidden="true" />
          {requestMutation.isPending ? t('common.sending') : t('payments.client.request.submit')}
        </Button>
      </SheetFooter>
    </form>
  );
}

interface Props {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  clientId: string;
  clientName: string;
  /** The payments that can still be requested, earliest first. */
  rows: readonly ClientPaymentRowDto[];
  /** The row the drawer opens on; the first row when none. */
  initialPaymentId: string | null;
}

/** Side drawer of the Payment tab: ask the client to pay one open payment by notification and/or email. */
export default function RequestPaymentDrawer({ open, onOpenChange, clientId, clientName, rows, initialPaymentId }: Props) {
  const { t } = useTranslation();
  return (
    <Sheet open={open} onOpenChange={onOpenChange}>
      <SheetContent className="gap-0 sm:w-drawer sm:max-w-none">
        <SheetHeader className="border-b border-border px-6 py-5 pr-14">
          <SheetTitle className="text-auth-title">{t('payments.client.request.title')}</SheetTitle>
          <SheetDescription className="text-body">
            {t('payments.client.request.description', { name: clientName })}
          </SheetDescription>
        </SheetHeader>
        {open && (
          <RequestForm
            key={initialPaymentId ?? 'first'}
            clientId={clientId}
            clientName={clientName}
            rows={rows}
            initialPaymentId={initialPaymentId ?? rows[0]?.paymentId ?? ''}
            onClose={() => onOpenChange(false)}
          />
        )}
      </SheetContent>
    </Sheet>
  );
}
