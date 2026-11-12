import { useTranslation } from 'react-i18next';
import { Lock } from 'lucide-react';

/** Grey "Outside plan" pill with a lock, shown next to the status chip of a restricted client. */
export default function OutsidePlanChip() {
  const { t } = useTranslation();

  return (
    <span
      title={t('clients.outsidePlan.tooltip')}
      className="inline-flex items-center gap-1.25 rounded-full bg-sunken px-2.25 py-0.75 text-meta font-semibold whitespace-nowrap text-muted-foreground"
    >
      <Lock className="size-3" aria-hidden="true" />
      {t('clients.outsidePlan.chip')}
    </span>
  );
}
