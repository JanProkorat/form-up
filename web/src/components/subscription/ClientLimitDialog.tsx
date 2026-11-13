import { Dialog as DialogPrimitive } from 'radix-ui';
import { useTranslation } from 'react-i18next';
import { Users } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent } from '@/components/ui/dialog';
import UsageBar from '@/components/subscription/UsageBar';
import { useSubscription } from '@/hooks/useSubscriptionQueries';

interface Props {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  /** What was refused: an invite the coach sent, or a client request the coach tried to accept. */
  context: 'invite' | 'accept';
  /** First name of the client whose request was refused, for the accept hint. */
  clientName?: string;
}

/** "Your plan is full": opened when an invite or an accept answers 403 CLIENT_LIMIT_REACHED. */
export default function ClientLimitDialog({ open, onOpenChange, context, clientName }: Props) {
  const { t } = useTranslation();
  const { planName, clientLimit, activeClientCount } = useSubscription();
  const used = clientLimit === null ? activeClientCount : Math.max(activeClientCount, clientLimit);

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-lg gap-4.5 px-7 pt-6.5 pb-6">
        <div className="flex items-start gap-3.5 pr-8">
          <span className="flex size-11 shrink-0 items-center justify-center rounded-md bg-sunken text-ink-2">
            <Users className="size-5" aria-hidden="true" />
          </span>
          <div className="flex flex-col gap-1.25">
            <DialogPrimitive.Title className="font-display text-auth-title font-semibold text-ink">
              {t('subscription.clientLimit.title')}
            </DialogPrimitive.Title>
            <DialogPrimitive.Description className="text-copy text-muted-foreground">
              {clientLimit === null
                ? t('subscription.clientLimit.descriptionUnknown', { plan: planName })
                : t('subscription.clientLimit.description', { plan: planName, limit: clientLimit })}
            </DialogPrimitive.Description>
          </div>
        </div>

        {clientLimit !== null && (
          <div className="flex flex-col gap-2">
            <div className="flex items-center gap-4">
              <span className="font-semibold text-ink">
                {t('subscription.clientsOfLimit', { count: used, limit: clientLimit })}
              </span>
              {planName && <span className="ml-auto text-meta text-muted-foreground">{planName}</span>}
            </div>
            <UsageBar used={used} limit={clientLimit} />
          </div>
        )}

        <p className="text-body text-muted-foreground">
          {context === 'accept'
            ? t('subscription.clientLimit.hintAccept', { name: clientName ?? '' })
            : t('subscription.clientLimit.hintInvite')}
        </p>

        <div className="flex justify-end gap-2.5">
          <Button type="button" variant="outline" size="lg" className="px-3.5 font-semibold" onClick={() => onOpenChange(false)}>
            {t('subscription.clientLimit.notNow')}
          </Button>
          <Button type="button" size="lg" className="px-3.5 font-semibold" disabled>
            {t('subscription.seePlans')}
          </Button>
        </div>
      </DialogContent>
    </Dialog>
  );
}
