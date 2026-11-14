import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Lock } from 'lucide-react';
import { Button } from '@/components/ui/button';
import PaywallDialog from '@/components/subscription/PaywallDialog';

/** Stands in for the composer when the coach's plan has no chat: reading stays open, sending opens the paywall. */
export default function PlanLockedComposer() {
  const { t } = useTranslation();
  const [paywallOpen, setPaywallOpen] = useState(false);
  return (
    <div
      data-testid="composer-plan-locked"
      className="flex items-center gap-3 border-t border-border bg-card px-5 py-3.5"
    >
      <p className="flex min-w-0 flex-1 items-center gap-2 px-1 text-meta text-muted-foreground">
        <Lock className="size-4 shrink-0" aria-hidden="true" />
        {t('inbox.composer.planLockedNotice')}
      </p>
      <Button type="button" variant="outline" size="sm" onClick={() => setPaywallOpen(true)}>
        {t('inbox.composer.planLockedAction')}
      </Button>
      <PaywallDialog open={paywallOpen} onOpenChange={setPaywallOpen} feature="chat" />
    </div>
  );
}
