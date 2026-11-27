import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { ExternalLink, Info } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';

interface UpgradePaymentNoticeProps {
  /** Localized name of the plan being taken; not needed when `message` is given. */
  plan?: string;
  /** Localized name of the plan the coach stays on until the payment is confirmed; not needed with `message`. */
  current?: string;
  /** Replaces the upgrade text, for a payment that is not a plan change. */
  message?: string;
  /** Stripe's hosted payment page. Only ever an href: never logged or shown as text. */
  paymentUrl: string;
  /** Called when the coach opens the payment page; starts watching for the plan to change. */
  onOpen: () => void;
  /** Hides the notice; when omitted the dismiss button is not rendered and the action row is right-aligned. */
  onDismiss?: () => void;
  /** Hides the action row, for a host that renders the payment link itself (e.g. in a dialog footer). */
  hideAction?: boolean;
}

/**
 * Shown when an upgrade needs the card to be confirmed on Stripe's page. The link is a real click, so the browser
 * opens the new tab without a popup block; the plan stays as it is until Stripe confirms the payment.
 */
export default function UpgradePaymentNotice({
  plan,
  current,
  message,
  paymentUrl,
  onOpen,
  onDismiss,
  hideAction = false,
}: UpgradePaymentNoticeProps) {
  const { t } = useTranslation();
  return (
    <div
      role="status"
      data-testid="upgrade-payment-notice"
      className="flex flex-col gap-3 rounded-lg bg-sunken px-3 py-2.5 text-body text-muted-foreground"
    >
      <div className="flex items-start gap-3">
        <Info className="mt-0.5 size-4 shrink-0" aria-hidden="true" />
        <p className="min-w-0 flex-1 text-copy">{message ?? t('subscription.billing.upgradePayment.text', { plan, current })}</p>
      </div>
      {!hideAction && (
        <div className={cn('flex flex-wrap items-center gap-3', !onDismiss && 'justify-end')}>
          <Button asChild size="lg" className="gap-2 px-3.5 font-semibold">
            <a href={paymentUrl} target="_blank" rel="noopener noreferrer" onClick={onOpen}>
              {t('subscription.billing.upgradePayment.open')}
              <ExternalLink className="size-4" aria-hidden="true" />
            </a>
          </Button>
          {onDismiss && (
            <Button type="button" variant="outline" size="lg" className="px-3.5 font-semibold" onClick={onDismiss}>
              {t('subscription.billing.upgradePayment.dismiss')}
            </Button>
          )}
        </div>
      )}
    </div>
  );
}

/**
 * Always-rendered wrapper that opens with a height and fade transition, for a notice inside a grid with `gap-4`
 * (the negative margin cancels that gap while closed). Closed it is inert and invisible; reduced motion skips the animation.
 */
export function PaymentNoticeReveal({ open, children }: { open: boolean; children: ReactNode }) {
  return (
    <div
      aria-hidden={!open}
      inert={!open}
      className={cn(
        'grid transition-[grid-template-rows,margin-top,opacity,visibility] duration-300 ease-out motion-reduce:transition-none',
        open ? 'visible mt-0 grid-rows-[1fr] opacity-100' : 'invisible -mt-4 grid-rows-[0fr] opacity-0',
      )}
    >
      <div className="overflow-hidden">{children}</div>
    </div>
  );
}
