import { useMemo } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { useTranslation } from 'react-i18next';
import { ChevronDown, Plus } from 'lucide-react';
import { Button } from '@/components/ui/button';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { Field } from '@/components/profile/ProfileSection';
import { PaymentCurrency } from '@/api/client-payments';
import { useAddClientOneOffPayment } from '@/hooks/useClientPaymentQueries';
import { showApiError, showSuccess } from '@/lib/api-errors';
import { parseAmount } from '@/lib/money';
import { addDaysIso, todayIso } from '@/lib/payment-format';

const TITLE_MAX = 100;
const AMOUNT_MAX = 9999999.99;

const SELECT_CLASS =
  'flex h-10 w-full min-w-0 appearance-none rounded-field border border-input bg-background py-1 pr-9 pl-3 text-copy text-foreground outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 aria-invalid:border-destructive disabled:opacity-50';

interface FormValues {
  title: string;
  amount: string;
  currency: PaymentCurrency;
  dueDate: string;
}

interface FormProps {
  clientId: string;
  clientName: string;
  defaultCurrency: PaymentCurrency;
  defaultDueDays: number;
  onClose: () => void;
}

function OneOffForm({ clientId, clientName, defaultCurrency, defaultDueDays, onClose }: FormProps) {
  const { t } = useTranslation();
  const addMutation = useAddClientOneOffPayment(clientId);

  const schema = useMemo(
    () =>
      z.object({
        title: z
          .string()
          .trim()
          .min(1, t('payments.client.oneOff.titleRequired'))
          .max(TITLE_MAX, t('payments.client.oneOff.titleTooLong', { max: TITLE_MAX })),
        amount: z.string().refine((raw) => {
          const value = parseAmount(raw);
          return value !== null && value > 0 && value <= AMOUNT_MAX;
        }, t('payments.client.oneOff.amountInvalid')),
        currency: z.enum(PaymentCurrency),
        dueDate: z.string().min(1, t('payments.client.oneOff.dueRequired')),
      }),
    [t],
  );

  const {
    register,
    handleSubmit,
    formState: { errors },
  } = useForm<FormValues>({
    resolver: zodResolver(schema),
    mode: 'onTouched',
    defaultValues: {
      title: '',
      amount: '',
      currency: defaultCurrency,
      dueDate: addDaysIso(todayIso(), defaultDueDays),
    },
  });

  const submit = async (values: FormValues) => {
    try {
      // mutateAsync, not mutate callbacks: the refetch can unmount this form first.
      await addMutation.mutateAsync({
        title: values.title.trim(),
        amount: parseAmount(values.amount) ?? 0,
        currency: values.currency,
        dueDate: values.dueDate,
      });
    } catch (error) {
      showApiError(error, 'payments.client.oneOff.error');
      return;
    }
    showSuccess('payments.client.oneOff.success');
    onClose();
  };

  return (
    <>
      <DialogHeader className="pr-8 text-left">
        <DialogTitle className="text-lede font-bold">{t('payments.client.oneOff.title')}</DialogTitle>
        <DialogDescription className="text-copy leading-normal">
          {t('payments.client.oneOff.description', { name: clientName })}
        </DialogDescription>
      </DialogHeader>
      <form noValidate onSubmit={handleSubmit(submit)} className="flex flex-col gap-4">
        <Field
          label={t('payments.client.oneOff.forLabel')}
          htmlFor="one-off-title"
          hint={t('payments.client.oneOff.forHint')}
          error={errors.title?.message}
        >
          <Input
            id="one-off-title"
            className="h-10"
            maxLength={TITLE_MAX}
            aria-invalid={!!errors.title}
            aria-describedby="one-off-title-message"
            {...register('title')}
          />
        </Field>
        <div className="grid grid-cols-[minmax(0,1fr)_7rem] gap-3">
          <Field
            label={t('payments.client.oneOff.amount')}
            htmlFor="one-off-amount"
            error={errors.amount?.message}
          >
            <Input
              id="one-off-amount"
              inputMode="decimal"
              autoComplete="off"
              className="h-10 tabular-nums"
              aria-invalid={!!errors.amount}
              aria-describedby="one-off-amount-message"
              {...register('amount')}
            />
          </Field>
          <Field label={t('payments.client.oneOff.currency')} htmlFor="one-off-currency">
            <div className="relative">
              <select id="one-off-currency" className={SELECT_CLASS} {...register('currency')}>
                {Object.values(PaymentCurrency).map((currency) => (
                  <option key={currency} value={currency}>
                    {currency}
                  </option>
                ))}
              </select>
              <ChevronDown
                className="pointer-events-none absolute top-1/2 right-3 size-4 -translate-y-1/2 text-muted-foreground"
                aria-hidden="true"
              />
            </div>
          </Field>
        </div>
        <Field label={t('payments.client.oneOff.due')} htmlFor="one-off-due" error={errors.dueDate?.message}>
          <Input
            id="one-off-due"
            type="date"
            className="h-10"
            aria-invalid={!!errors.dueDate}
            aria-describedby="one-off-due-message"
            {...register('dueDate')}
          />
        </Field>
        <DialogFooter>
          <Button type="button" variant="outline" size="lg" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button type="submit" size="lg" disabled={addMutation.isPending}>
            <Plus aria-hidden="true" />
            {addMutation.isPending ? t('common.saving') : t('payments.client.oneOff.submit')}
          </Button>
        </DialogFooter>
      </form>
    </>
  );
}

interface Props {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  clientId: string;
  clientName: string;
  /** The client's terms currency, else the coach's main currency. */
  defaultCurrency: PaymentCurrency;
  /** The coach's payment term in days, for the pre-filled due date. */
  defaultDueDays: number;
}

/** Adds a payment outside the package: a title, an amount in one currency and a due date. */
export default function OneOffDialog({
  open,
  onOpenChange,
  clientId,
  clientName,
  defaultCurrency,
  defaultDueDays,
}: Props) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-110">
        <OneOffForm
          clientId={clientId}
          clientName={clientName}
          defaultCurrency={defaultCurrency}
          defaultDueDays={defaultDueDays}
          onClose={() => onOpenChange(false)}
        />
      </DialogContent>
    </Dialog>
  );
}
