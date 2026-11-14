import { useTranslation } from 'react-i18next';
import { CreditCard, TriangleAlert } from 'lucide-react';
import { SubscriptionStatus } from '@/api/generated';
import { showBillingError } from '@/components/subscription/billingErrors';
import { Button } from '@/components/ui/button';
import { useOpenBillingPortal, useSubscription } from '@/hooks/useSubscriptionQueries';

/**
 * Red banner on Plan and billing while the last payment failed: when the card is tried again, how long
 * everything keeps working, and "Update card", which opens the Stripe Customer Portal.
 */
export default function PastDueBanner() {
  const { t, i18n } = useTranslation();
  const { data } = useSubscription();
  const portal = useOpenBillingPortal();

  if (data?.status !== SubscriptionStatus.PastDue) return null;

  const format = new Intl.DateTimeFormat(i18n.language, { dateStyle: 'medium' });
  const retry = data.nextPaymentAttemptAt ? format.format(new Date(data.nextPaymentAttemptAt)) : null;
  const until = data.currentPeriodEndsAt ? format.format(new Date(data.currentPeriodEndsAt)) : null;
  const body =
    retry && until
      ? t('subscription.billing.pastDue.body', { retry, until })
      : until
        ? t('subscription.billing.pastDue.bodyUntil', { until })
        : retry
          ? t('subscription.billing.pastDue.bodyRetry', { retry })
          : t('subscription.billing.pastDue.bodyNoDates');

  return (
    <div role="alert" className="flex flex-wrap items-center gap-3.5 rounded-xl bg-error-soft px-4 py-3.5">
      <TriangleAlert className="size-4.5 shrink-0 text-error" aria-hidden="true" />
      <div className="flex min-w-0 flex-col gap-0.5">
        <span className="text-copy font-semibold text-ink">{t('subscription.billing.pastDue.title')}</span>
        <span className="text-body text-ink-2">{body}</span>
      </div>
      <Button
        type="button"
        size="lg"
        className="ml-auto gap-1.75 px-3.5 font-semibold"
        disabled={portal.isPending || !data.hasBillingAccount}
        onClick={() => portal.mutate(undefined, { onError: (error) => showBillingError(error, 'subscription.billing.errors.portal') })}
      >
        <CreditCard aria-hidden="true" />
        {t('subscription.billing.pastDue.updateCard')}
      </Button>
    </div>
  );
}
