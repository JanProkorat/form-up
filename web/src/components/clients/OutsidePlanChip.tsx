import { useTranslation } from 'react-i18next';
import { Lock } from 'lucide-react';

interface Props {
  /** Date-only value: the day the client's published weeks end. Set while the client is winding down. */
  readOnlyFrom?: string;
}

function formatDay(value: string, language: string): string {
  return new Intl.DateTimeFormat(language, { day: 'numeric', month: 'short', timeZone: 'UTC' }).format(new Date(value));
}

/**
 * Chip next to the status chip of a restricted client: amber "Read-only from {date}" while the published weeks
 * still run, grey "Outside plan" with a lock otherwise.
 */
export default function OutsidePlanChip({ readOnlyFrom }: Props) {
  const { t, i18n } = useTranslation();

  if (readOnlyFrom) {
    const date = formatDay(readOnlyFrom, i18n.language);
    return (
      <span
        title={t('clients.outsidePlan.readOnlyTooltip', { date })}
        className="inline-flex items-center gap-1.25 rounded-full bg-training-soft px-2.25 py-0.75 text-meta font-semibold whitespace-nowrap text-training-ink"
      >
        <span className="size-1.5 rounded-full bg-current" aria-hidden="true" />
        {t('clients.outsidePlan.readOnlyFrom', { date })}
      </span>
    );
  }

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
