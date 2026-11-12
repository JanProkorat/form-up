import { Dialog as DialogPrimitive } from 'radix-ui';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { Check, Lock } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { PLAN_BILLING_PATH } from '@/components/subscription/planLinks';
import { useSubscription } from '@/hooks/useSubscriptionQueries';
import { cn } from '@/lib/utils';

const BENEFIT_KEYS = ['benefit1', 'benefit2', 'benefit3'] as const;

interface Props {
  firstName: string;
  /** Rendered inside a Dialog: the title and text use the dialog's accessible title and description. */
  inDialog?: boolean;
}

/** Shown instead of the plan-start actions for a client outside the coach's plan: new plans need a higher plan. */
export default function OutsidePlanPaywall({ firstName, inDialog = false }: Props) {
  const { t } = useTranslation();
  const { planName } = useSubscription();
  const Title = inDialog ? DialogPrimitive.Title : 'h3';
  const Body = inDialog ? DialogPrimitive.Description : 'p';
  // The dialog opens from an existing plan in the editor; the inline card sits where a new plan would start.
  const title = t(inDialog ? 'clientDetail.outsidePlanPaywall.editTitle' : 'clientDetail.outsidePlanPaywall.title', {
    name: firstName,
  });

  return (
    <div
      role={inDialog ? undefined : 'region'}
      aria-label={inDialog ? undefined : title}
      className={cn('flex flex-col gap-4', !inDialog && 'rounded-2xl border border-border bg-sunken p-6')}
    >
      <div className="flex items-center gap-3.5">
        <span className="flex size-11 shrink-0 items-center justify-center rounded-md bg-training-soft text-training-ink">
          <Lock className="size-5" aria-hidden="true" />
        </span>
        {planName && (
          <span
            className={cn(
              'inline-flex h-5.5 items-center rounded-md px-2 text-meta font-semibold whitespace-nowrap text-muted-foreground',
              inDialog ? 'bg-sunken' : 'bg-card',
            )}
          >
            {t('subscription.paywall.notOnPlan', { plan: planName })}
          </span>
        )}
      </div>
      <Title className={inDialog ? 'font-display text-auth-title font-semibold text-ink' : 'text-subhead font-bold text-ink'}>
        {title}
      </Title>
      <Body className="max-w-xl text-copy text-muted-foreground">
        {t('clientDetail.outsidePlanPaywall.body', { name: firstName, plan: planName })}
      </Body>
      <p className="max-w-xl text-copy font-semibold text-ink">{t('clientDetail.outsidePlanPaywall.lead')}</p>
      <ul className="flex flex-col gap-2.5">
        {BENEFIT_KEYS.map((key) => (
          <li key={key} className="flex gap-2.5 text-copy text-ink-2">
            <Check className="mt-0.5 size-4 shrink-0 text-success" aria-hidden="true" />
            {t(`clientDetail.outsidePlanPaywall.${key}`, { name: firstName })}
          </li>
        ))}
      </ul>
      <div className="flex justify-end border-t border-border pt-4">
        <Button asChild size="lg" className="px-3.5 font-semibold">
          <Link to={PLAN_BILLING_PATH}>{t('subscription.outsidePlan.upgrade')}</Link>
        </Button>
      </div>
    </div>
  );
}
