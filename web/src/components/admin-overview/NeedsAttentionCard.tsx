import type { ComponentType } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { ChevronRight, Clock, CreditCard, Gift, Tag } from 'lucide-react';
import { AdminCoachStatusFilter } from '@/api/admin-coaches';
import type { GetAdminAttentionResponse } from '@/api/admin-overview';
import { useAdminAttention } from '@/components/admin-overview/useAdminAttentionQuery';
import { formatCoachDate } from '@/components/admin-coaches/coachFormat';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';

const COACHES_PATH = '/admin/coach-accounts';
const PROMOTIONS_PATH = '/admin/promotion-offers';

type IconComponent = ComponentType<{ className?: string; 'aria-hidden'?: boolean | 'true' | 'false' }>;

interface AttentionRow {
  key: string;
  Icon: IconComponent;
  /** Token pair for the icon tile. */
  toneClass: string;
  title: string;
  detail?: string;
  linkLabel: string;
  linkTo: string;
}

const TONE = {
  error: 'bg-error-soft text-error',
  caution: 'bg-caution-soft text-caution-ink',
  info: 'bg-training-soft text-training-ink',
} as const;

function coachesLink(status: AdminCoachStatusFilter): string {
  return `${COACHES_PATH}?status=${status}`;
}

/** Only the rows whose count is above zero, in board order; a promotion gets one row each. */
function useAttentionRows(attention: GetAdminAttentionResponse | undefined): AttentionRow[] {
  const { t, i18n } = useTranslation();
  const language = i18n.language;
  if (!attention) {
    return [];
  }
  const rows: AttentionRow[] = [];

  const failed = attention.paymentFailedCount ?? 0;
  if (failed > 0) {
    const shown = attention.paymentFailedCoaches ?? [];
    const names = shown.map((coach) => coach.name ?? '').filter(Boolean);
    const more = failed - shown.length;
    const list = names.join(', ');
    rows.push({
      key: 'paymentFailed',
      Icon: CreditCard,
      toneClass: TONE.error,
      title: t('adminOverview.attention.paymentFailed', { count: failed }),
      detail: names.length > 0 ? (more > 0 ? t('adminOverview.attention.namesMore', { names: list, count: more }) : list) : undefined,
      linkLabel: t('adminOverview.attention.openCoaches'),
      linkTo: coachesLink(AdminCoachStatusFilter.PaymentFailed),
    });
  }

  const cancelling = attention.cancellingCount ?? 0;
  if (cancelling > 0) {
    const from = formatCoachDate(attention.cancellingEarliestPeriodEndsAt ?? undefined, language);
    const to = formatCoachDate(attention.cancellingLatestPeriodEndsAt ?? undefined, language);
    let detail: string | undefined;
    if (from && to) {
      detail = from === to ? t('adminOverview.attention.cancellingOn', { date: from }) : t('adminOverview.attention.cancellingBetween', { from, to });
    }
    rows.push({
      key: 'cancelling',
      Icon: Clock,
      toneClass: TONE.caution,
      title: t('adminOverview.attention.cancelling', { count: cancelling }),
      detail,
      linkLabel: t('adminOverview.attention.openCoaches'),
      linkTo: coachesLink(AdminCoachStatusFilter.Cancelling),
    });
  }

  const givenFree = attention.givenFreeEndingSoonCount ?? 0;
  if (givenFree > 0) {
    rows.push({
      key: 'givenFree',
      Icon: Gift,
      toneClass: TONE.info,
      title: t('adminOverview.attention.givenFree', { count: givenFree }),
      detail: t('adminOverview.attention.givenFreeDetail'),
      linkLabel: t('adminOverview.attention.openCoaches'),
      linkTo: coachesLink(AdminCoachStatusFilter.GivenFree),
    });
  }

  for (const promotion of attention.promotionsEndingSoon ?? []) {
    const until = formatCoachDate(promotion.claimUntil ?? undefined, language);
    const parts: string[] = [];
    if (until) {
      parts.push(t('adminOverview.attention.promotionUntil', { date: until }));
    }
    if (typeof promotion.maxUses === 'number') {
      parts.push(t('adminOverview.attention.promotionUses', { used: promotion.usedCount ?? 0, max: promotion.maxUses }));
    }
    rows.push({
      key: `promotion-${promotion.promotionId ?? promotion.name}`,
      Icon: Tag,
      toneClass: TONE.caution,
      title: t('adminOverview.attention.promotion', { name: promotion.name ?? '' }),
      detail: parts.length > 0 ? parts.join(' ') : undefined,
      linkLabel: t('adminOverview.attention.openPromotion'),
      linkTo: PROMOTIONS_PATH,
    });
  }

  return rows;
}

function Row({ row }: { row: AttentionRow }) {
  const { Icon } = row;
  return (
    <li className="flex items-center gap-3.5 border-t border-line px-4.5 py-3.5">
      <span className={`flex size-9.5 shrink-0 items-center justify-center rounded-xl ${row.toneClass}`}>
        <Icon className="size-4.5" aria-hidden="true" />
      </span>
      <span className="flex min-w-0 grow flex-col gap-0.75">
        <span className="text-copy font-semibold text-ink">{row.title}</span>
        {row.detail && <span className="text-body leading-snug text-muted-foreground">{row.detail}</span>}
      </span>
      <Button asChild variant="outline" className="shrink-0 font-semibold">
        <Link to={row.linkTo}>
          {row.linkLabel}
          <ChevronRight aria-hidden="true" />
        </Link>
      </Button>
    </li>
  );
}

/** What needs the admin now: failed payments, cancelling coaches, gifts and promotions about to end. Fails and retries on its own. */
export default function NeedsAttentionCard() {
  const { t } = useTranslation();
  const attentionQuery = useAdminAttention();
  const rows = useAttentionRows(attentionQuery.data);
  const loaded = !attentionQuery.isPending && !attentionQuery.isError && attentionQuery.data !== undefined;

  return (
    <section
      aria-labelledby="overview-attention"
      className="overflow-hidden rounded-2xl border border-line bg-card shadow-panel"
    >
      <div className="flex items-baseline gap-2.5 px-4.5 py-4">
        <h2 id="overview-attention" className="font-display text-card-title font-semibold text-ink">
          {t('adminOverview.attention.title')}
        </h2>
        {loaded && rows.length > 0 && (
          <span className="text-body text-muted-foreground">
            {t('adminOverview.attention.items', { count: rows.length })}
          </span>
        )}
      </div>

      {attentionQuery.isPending && (
        <div className="flex flex-col gap-3 border-t border-line p-4.5" aria-hidden="true">
          <Skeleton className="h-10 rounded-xl" />
          <Skeleton className="h-10 rounded-xl" />
        </div>
      )}

      {attentionQuery.isError && (
        <div role="alert" className="flex flex-col items-center gap-3 border-t border-line px-5 py-8 text-center">
          <p className="text-body text-muted-foreground">{t('adminOverview.attention.loadError')}</p>
          <Button type="button" variant="outline" size="sm" onClick={() => void attentionQuery.refetch()}>
            {t('adminOverview.retry')}
          </Button>
        </div>
      )}

      {loaded && rows.length === 0 && (
        <div className="flex flex-col gap-1 border-t border-line px-4.5 py-6">
          <span className="text-copy font-semibold text-ink">{t('adminOverview.attention.allClear')}</span>
          <span className="text-body text-muted-foreground">{t('adminOverview.attention.allClearDetail')}</span>
        </div>
      )}

      {loaded && rows.length > 0 && (
        <ul className="m-0 list-none p-0">
          {rows.map((row) => (
            <Row key={row.key} row={row} />
          ))}
        </ul>
      )}
    </section>
  );
}
