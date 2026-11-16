import { useTranslation } from 'react-i18next';
import { CalendarClock } from 'lucide-react';
import { Button } from '@/components/ui/button';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';

interface Props {
  open: boolean;
  /** Name of the plan the coach stays on, already translated. */
  planName: string;
  /** Date the current plan renews, already formatted; null when unknown. */
  renewsOn: string | null;
  pending: boolean;
  onConfirm: () => void;
  onClose: () => void;
}

/** Confirm dialog for cancelling a scheduled plan change: the coach stays on the current plan. */
export default function CancelScheduledChangeDialog({ open, planName, renewsOn, pending, onConfirm, onClose }: Props) {
  const { t } = useTranslation();

  return (
    <Dialog open={open} onOpenChange={(next) => !next && !pending && onClose()}>
      <DialogContent className="max-w-125 gap-5 rounded-xl px-7 pt-6.5 pb-6 sm:max-w-125">
        <DialogHeader className="flex-row items-start gap-3.5 text-left">
          <span className="flex size-11 shrink-0 items-center justify-center rounded-xl bg-sunken text-ink">
            <CalendarClock className="size-4.5" aria-hidden="true" />
          </span>
          <div className="flex flex-col gap-1.25">
            <DialogTitle className="text-auth-title leading-tight">
              {t('subscription.plan.scheduled.cancelDialog.title')}
            </DialogTitle>
            <DialogDescription className="text-copy leading-normal">
              {renewsOn
                ? t('subscription.plan.scheduled.cancelDialog.body', { plan: planName, date: renewsOn })
                : t('subscription.plan.scheduled.cancelDialog.bodyNoDate', { plan: planName })}
            </DialogDescription>
          </div>
        </DialogHeader>

        <DialogFooter>
          <Button type="button" variant="outline" size="lg" disabled={pending} onClick={onClose}>
            {t('common.close')}
          </Button>
          <Button type="button" size="lg" disabled={pending} onClick={onConfirm}>
            {t('subscription.plan.scheduled.cancel')}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
