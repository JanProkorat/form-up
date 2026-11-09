import { useTranslation } from 'react-i18next';
import { AdminCoachStatus } from '@/api/admin-coaches';
import { STATUS_BADGE_CLASS, formatCoachDate } from '@/components/admin-coaches/coachFormat';
import { cn } from '@/lib/utils';

interface Props {
  status: AdminCoachStatus | undefined;
  /** End of the paid period; shown on a Cancelling badge as "Cancels <date>". */
  endsAt?: string;
}

/** The coach's status as a pill. Unknown or missing statuses render as Free. */
export default function CoachStatusBadge({ status = AdminCoachStatus.Free, endsAt }: Props) {
  const { t, i18n } = useTranslation();
  const endDate = formatCoachDate(endsAt, i18n.language);
  const label =
    status === AdminCoachStatus.Cancelling && endDate
      ? t('adminCoaches.status.cancellingOn', { date: endDate })
      : t(`adminCoaches.status.${status}`);
  return (
    <span
      className={cn(
        'inline-flex items-center gap-1.5 rounded-full px-2.5 py-1 text-meta font-semibold whitespace-nowrap',
        STATUS_BADGE_CLASS[status],
      )}
    >
      <span className="size-1.5 rounded-full bg-current" aria-hidden="true" />
      {label}
    </span>
  );
}
