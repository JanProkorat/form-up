import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Lock } from 'lucide-react';
import { Button } from '@/components/ui/button';
import PaywallDialog from '@/components/subscription/PaywallDialog';
import type { PaywallFeature } from '@/components/subscription/PaywallCard';

interface Props {
  feature: PaywallFeature;
  /** Card title, e.g. "Weight". */
  title: string;
}

/** A card on the client overview whose data the coach's plan does not include; opens the feature paywall. */
export default function LockedFeatureCard({ feature, title }: Props) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  return (
    <section
      data-testid="locked-feature-card"
      className="flex h-full flex-col gap-3 rounded-2xl border border-border bg-card p-5"
    >
      <h2 className="text-body font-semibold text-ink">{title}</h2>
      <p className="flex items-center gap-2 text-body text-muted-foreground">
        <Lock className="size-4 shrink-0" aria-hidden="true" />
        {t(`subscription.paywall.${feature}.title`)}
      </p>
      <Button type="button" variant="outline" size="sm" className="w-fit" onClick={() => setOpen(true)}>
        {t('subscription.seePlans')}
      </Button>
      <PaywallDialog open={open} onOpenChange={setOpen} feature={feature} />
    </section>
  );
}
