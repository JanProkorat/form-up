import { useTranslation } from 'react-i18next';
import { Badge } from '@/components/ui/badge';
import { ClientListStatus } from '@/api/generated';
import { cn } from '@/lib/utils';

interface Props {
  status?: ClientListStatus;
  /** Clients list rows: Active reads "Active plans" and Paused "No plan" (the Filter menu's plan options). */
  planLabels?: boolean;
}

/**
 * Status pill for a clients-list row: neutral outline pill, an ink dot for
 * Active and a grey dot for Paused/Archived (PageClients board). Labels reuse
 * the tab labels (`clients.tabs.*`), or the plan option labels with `planLabels`.
 */
export default function ClientStatusBadge({ status, planLabels = false }: Props) {
  const { t } = useTranslation();

  const label =
    status === ClientListStatus.Active
      ? t(planLabels ? 'clients.filterMenu.plans.active' : 'clients.tabs.active')
      : status === ClientListStatus.Paused
        ? t(planLabels ? 'clients.filterMenu.plans.none' : 'clients.tabs.paused')
        : status === ClientListStatus.Archived
          ? t('clients.tabs.archived')
          : null;

  if (label === null) {
    return null;
  }

  const isActive = status === ClientListStatus.Active;

  return (
    <Badge variant="outline" className={cn('gap-1.5 px-2.25 py-0.75 text-meta font-semibold', !isActive && 'text-ink-2')}>
      <span
        className={cn('size-1.5 rounded-full', isActive ? 'bg-ink' : 'bg-muted-foreground')}
        aria-hidden="true"
      />
      {label}
    </Badge>
  );
}
