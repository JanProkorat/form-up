import { useMemo } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { useTranslation } from 'react-i18next';
import { Check } from 'lucide-react';
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
import type { ClientPaymentRowDto } from '@/api/client-payments';
import { useMarkClientPaymentPaid } from '@/hooks/useClientPaymentQueries';
import {
  PAYMENT_PAID_ON_IN_FUTURE,
  isAlreadySettled,
  paymentErrorCode,
} from '@/components/client-detail/payment/paymentErrors';
import { showApiError, showSuccess } from '@/lib/api-errors';
import { todayIso } from '@/lib/payment-format';

interface FormValues {
  paidOn: string;
}

interface FormProps {
  clientId: string;
  clientName: string;
  row: ClientPaymentRowDto;
  periodLabel: string;
  onClose: () => void;
}

function MarkPaidForm({ clientId, clientName, row, periodLabel, onClose }: FormProps) {
  const { t } = useTranslation();
  const today = todayIso();
  const markMutation = useMarkClientPaymentPaid(clientId);

  const schema = useMemo(
    () =>
      z.object({
        paidOn: z
          .string()
          .min(1, t('payments.client.markPaid.dateRequired'))
          .refine((value) => value <= todayIso(), t('payments.client.markPaid.dateInFuture')),
      }),
    [t],
  );

  const {
    register,
    handleSubmit,
    setError,
    formState: { errors },
  } = useForm<FormValues>({
    resolver: zodResolver(schema),
    mode: 'onTouched',
    defaultValues: { paidOn: today },
  });

  const submit = async (values: FormValues) => {
    if (!row.paymentId) {
      return;
    }
    try {
      // mutateAsync, not mutate callbacks: the refetch can unmount this form first.
      await markMutation.mutateAsync({ paymentId: row.paymentId, request: { paidOn: values.paidOn } });
    } catch (error) {
      if (paymentErrorCode(error) === PAYMENT_PAID_ON_IN_FUTURE) {
        setError('paidOn', { message: t('payments.client.markPaid.dateInFuture') });
        return;
      }
      showApiError(error, 'payments.client.markPaid.error');
      // Settled elsewhere: the rows refetch, so the dialog has nothing left to act on.
      if (isAlreadySettled(error)) {
        onClose();
      }
      return;
    }
    showSuccess('payments.client.markPaid.success');
    onClose();
  };

  return (
    <>
      <DialogHeader className="pr-8 text-left">
        <DialogTitle className="text-lede font-bold">
          {t('payments.client.markPaid.title', { period: periodLabel })}
        </DialogTitle>
        <DialogDescription className="text-copy leading-normal">
          {t('payments.client.markPaid.description', { name: clientName })}
        </DialogDescription>
      </DialogHeader>
      <form noValidate onSubmit={handleSubmit(submit)} className="flex flex-col gap-4">
        <Field
          label={t('payments.client.markPaid.paidOn')}
          htmlFor="mark-paid-date"
          error={errors.paidOn?.message}
        >
          <Input
            id="mark-paid-date"
            type="date"
            max={today}
            className="h-10"
            aria-invalid={!!errors.paidOn}
            aria-describedby="mark-paid-date-message"
            {...register('paidOn')}
          />
        </Field>
        <DialogFooter>
          <Button type="button" variant="outline" size="lg" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button type="submit" size="lg" disabled={markMutation.isPending}>
            <Check aria-hidden="true" />
            {markMutation.isPending ? t('common.saving') : t('payments.client.markPaid.submit')}
          </Button>
        </DialogFooter>
      </form>
    </>
  );
}

interface Props {
  clientId: string;
  clientName: string;
  /** The row being settled; null keeps the dialog closed. */
  row: ClientPaymentRowDto | null;
  periodLabel: string;
  onClose: () => void;
}

/** Confirms that a client's money arrived, on a day the coach picks (today by default). */
export default function MarkPaidDialog({ clientId, clientName, row, periodLabel, onClose }: Props) {
  return (
    <Dialog open={row !== null} onOpenChange={(open) => !open && onClose()}>
      <DialogContent className="max-w-110">
        {row && (
          <MarkPaidForm
            key={row.paymentId}
            clientId={clientId}
            clientName={clientName}
            row={row}
            periodLabel={periodLabel}
            onClose={onClose}
          />
        )}
      </DialogContent>
    </Dialog>
  );
}
