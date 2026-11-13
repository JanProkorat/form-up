import { Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { ChevronRight } from 'lucide-react';
import { SubscriptionStatus } from '@/api/generated';
import UsageBar from '@/components/subscription/UsageBar';
import { KEEP_CLIENTS_PATH, PLAN_BILLING_PATH } from '@/components/subscription/planLinks';

interface Props {
  planName: string;
  status: SubscriptionStatus | undefined;
  used: number;
  /** Null means unlimited. */
  limit: number | null;
  onNavigate?: () => void;
  /** A lapsed coach, or one on Free over the client cap: the row links to the keep-clients screen instead of Plan and billing. */
  lapsed?: boolean;
}

/** Plan row of the sidebar account card: plan, clients used of the cap, and a link to Plan and billing. */
export default function SidebarPlanRow({ planName, status, used, limit, onNavigate, lapsed = false }: Props) {
  const { t } = useTranslation();
  const pastDue = status === SubscriptionStatus.PastDue;
  const usage =
    limit === null ? t('subscription.sidebar.unlimited', { count: used }) : t('subscription.sidebar.usage', { used, limit });

  return (
    <Link
      to={lapsed ? KEEP_CLIENTS_PATH : PLAN_BILLING_PATH}
      onClick={onNavigate}
      aria-label={t(lapsed ? 'subscription.sidebar.ariaLapsed' : 'subscription.sidebar.aria', {
        plan: planName,
        detail: pastDue ? t('subscription.status.PastDue') : usage,
      })}
      className="mx-1 mb-1 flex flex-col gap-2 rounded-md px-1.75 py-1.5 text-sidebar-text outline-none focus-visible:ring-2 focus-visible:ring-sidebar-text/60"
    >
      <span className="flex items-center gap-1.5 text-body font-semibold">
        {t('subscription.sidebar.plan', { plan: planName })}
        {/* Plain template, not cn(): twMerge would drop text-meta next to a text colour class. */}
        <span className={`ml-auto text-meta font-medium ${pastDue ? 'text-error' : 'text-sidebar-muted'}`}>
          {pastDue ? t('subscription.status.PastDue') : usage}
        </span>
        <ChevronRight className="size-3.5 shrink-0 text-sidebar-muted" aria-hidden="true" />
      </span>
      <UsageBar used={used} limit={limit} trackClassName="bg-sidebar" sizeClassName="h-1.5 rounded-full" danger={pastDue} />
    </Link>
  );
}
