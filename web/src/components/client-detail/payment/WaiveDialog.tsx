import { useMemo } from 'react';
import { useForm, useWatch } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/button';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';
import { Textarea } from '@/components/ui/textarea';
import { Field } from '@/components/profile/ProfileSection';
import type { ClientPaymentRowDto } from '@/api/client-payments';
import { useWaiveClientPayment } from '@/hooks/useClientPaymentQueries';
import { isAlreadySettled } from '@/components/client-detail/payment/paymentErrors';
import { showApiError, showSuccess } from '@/lib/api-errors';

const REASON_MAX = 100;

interface FormValues {
  reason: string;
}

interface FormProps {
  clientId: string;
  clientName: string;
  row: ClientPaymentRowDto;
  periodLabel: string;
  onClose: () => void;
}

function WaiveForm({ clientId, clientName, row, periodLabel, onClose }: FormProps) {
  const { t } = useTranslation();
  const waiveMutation = useWaiveClientPayment(clientId);

  const schema = useMemo(
    () => z.object({ reason: z.string().max(REASON_MAX, t('payments.client.waive.reasonTooLong', { max: REASON_MAX })) }),
    [t],
  );

  const {
    register,
    handleSubmit,
    control,
    formState: { errors },
  } = useForm<FormValues>({
    resolver: zodResolver(schema),
    mode: 'onTouched',
    defaultValues: { reason: '' },
  });
  const reasonLength = useWatch({ control, name: 'reason' }).length;

  const submit = async (values: FormValues) => {
    if (!row.paymentId) {
      return;
    }
    const reason = values.reason.trim();
    try {
      // mutateAsync, not mutate callbacks: the refetch can unmount this form first.
      await waiveMutation.mutateAsync({
        paymentId: row.paymentId,
        request: { reason: reason === '' ? undefined : reason },
      });
    } catch (error) {
      showApiError(error, 'payments.client.waive.error');
      if (isAlreadySettled(error)) {
        onClose();
      }
      return;
    }
    showSuccess('payments.client.waive.success');
    onClose();
  };

  return (
    <>
      <DialogHeader className="pr-8 text-left">
        <DialogTitle className="text-lede font-bold">
          {t('payments.client.waive.title', { period: periodLabel })}
        </DialogTitle>
        <DialogDescription className="text-copy leading-normal">
          {t('payments.client.waive.description', { name: clientName })}
        </DialogDescription>
      </DialogHeader>
      <form noValidate onSubmit={handleSubmit(submit)} className="flex flex-col gap-4">
        <Field
          label={t('payments.client.waive.reason')}
          htmlFor="waive-reason"
          aside={`${reasonLength} / ${REASON_MAX}`}
          hint={t('payments.client.waive.reasonHint')}
          error={errors.reason?.message}
        >
          <Textarea
            id="waive-reason"
            rows={2}
            className="min-h-16 resize-none"
            aria-invalid={!!errors.reason}
            aria-describedby="waive-reason-message"
            {...register('reason')}
          />
        </Field>
        <DialogFooter>
          <Button type="button" variant="outline" size="lg" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button type="submit" size="lg" disabled={waiveMutation.isPending}>
            {waiveMutation.isPending ? t('common.saving') : t('payments.client.waive.submit')}
          </Button>
        </DialogFooter>
      </form>
    </>
  );
}

interface Props {
  clientId: string;
  clientName: string;
  /** The row being waived; null keeps the dialog closed. */
  row: ClientPaymentRowDto | null;
  periodLabel: string;
  onClose: () => void;
}

/** Waives one payment with an optional reason only the coach sees. */
export default function WaiveDialog({ clientId, clientName, row, periodLabel, onClose }: Props) {
  return (
    <Dialog open={row !== null} onOpenChange={(open) => !open && onClose()}>
      <DialogContent className="max-w-110">
        {row && (
          <WaiveForm
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
