import { Dialog as DialogPrimitive } from 'radix-ui';
import { useTranslation } from 'react-i18next';
import { Check, Lock } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { useSubscription } from '@/hooks/useSubscriptionQueries';
import { cn } from '@/lib/utils';

export type PaywallFeature =
  | 'forms'
  | 'checkIns'
  | 'chat'
  | 'recipes'
  | 'ingredients'
  | 'planTemplates'
  | 'automations'
  | 'packages'
  | 'progressPhotos'
  | 'measurements'
  | 'payments';

const BENEFIT_KEYS = ['benefit1', 'benefit2'] as const;

interface Props {
  feature: PaywallFeature;
  /** Rendered inside a Dialog: the title and text use the dialog's accessible title and description. */
  inDialog?: boolean;
  className?: string;
}

const TITLE_CLASS = 'font-display text-auth-title font-semibold text-ink';
const BODY_CLASS = 'text-copy text-muted-foreground';

/**
 * The feature paywall body, shared by the page-level card and the dialog: the coach's current plan,
 * what the feature gives, and a "See plans" action that stays disabled until plan selection exists.
 */
export default function PaywallCard({ feature, inDialog = false, className }: Props) {
  const { t } = useTranslation();
  const { planName } = useSubscription();
  const Title = inDialog ? DialogPrimitive.Title : 'h2';
  const Body = inDialog ? DialogPrimitive.Description : 'p';

  return (
    <div
      role={inDialog ? undefined : 'region'}
      aria-label={inDialog ? undefined : t(`subscription.paywall.${feature}.title`)}
      className={cn(
        'flex flex-col gap-4.5',
        !inDialog && 'w-full max-w-xl rounded-2xl border border-border bg-card p-7 shadow-dialog',
        className,
      )}
    >
      <div className="flex items-center gap-3.5">
        <span className="flex size-11 shrink-0 items-center justify-center rounded-md bg-training-soft text-training-ink">
          <Lock className="size-5" aria-hidden="true" />
        </span>
        {planName && (
          <span className="inline-flex h-5.5 items-center rounded-md bg-sunken px-2 text-meta font-semibold whitespace-nowrap text-muted-foreground">
            {t('subscription.paywall.notOnPlan', { plan: planName })}
          </span>
        )}
      </div>
      <Title className={TITLE_CLASS}>{t(`subscription.paywall.${feature}.title`)}</Title>
      <Body className={BODY_CLASS}>{t(`subscription.paywall.${feature}.body`)}</Body>
      <ul className="flex flex-col gap-2.5">
        {BENEFIT_KEYS.map((key) => (
          <li key={key} className="flex gap-2.5 text-copy text-ink-2">
            <Check className="mt-0.5 size-4 shrink-0 text-success" aria-hidden="true" />
            {t(`subscription.paywall.${feature}.${key}`)}
          </li>
        ))}
      </ul>
      <div className="flex items-center justify-end gap-2 border-t border-border pt-4.5">
        <Button type="button" size="lg" className="px-3.5 font-semibold" disabled>
          {t('subscription.seePlans')}
        </Button>
      </div>
    </div>
  );
}
