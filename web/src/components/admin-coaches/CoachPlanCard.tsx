import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { AdminCoachStatus, BillingInterval, type GetAdminCoachResponse } from '@/api/admin-coaches';
import { Skeleton } from '@/components/ui/skeleton';
import CoachStatusBadge from '@/components/admin-coaches/CoachStatusBadge';
import { coachPlanName, formatCoachDate, formatCoachMoney } from '@/components/admin-coaches/coachFormat';

/** The bar turns amber from this share of the cap, and red when the coach is over it. */
const BAR_WARN_PERCENT = 80;

function Tile({ label, value, children }: { label: string; value: string; children?: ReactNode }) {
  return (
    <div className="flex min-w-0 flex-col gap-2 rounded-xl bg-sunken px-4 py-3.5">
      <span className="text-meta font-semibold text-muted-foreground">{label}</span>
      <span className="font-display text-card-title font-semibold text-ink">{value}</span>
      {children}
    </div>
  );
}

/** Plan card of the coach profile: plan, status, price, a status note and three tiles (clients, next payment, promotion). */
export default function CoachPlanCard({ coach }: { coach: GetAdminCoachResponse }) {
  const { t, i18n } = useTranslation();
  const used = coach.clientsUsed ?? 0;
  const max = coach.clientsMax ?? null;
  const percent = max === null || max <= 0 ? 0 : Math.min(100, Math.round((used / max) * 100));
  const overCap = max !== null && used > max;
  const barClass = overCap ? 'bg-error' : percent >= BAR_WARN_PERCENT ? 'bg-training' : 'bg-success';
  const periodEnd = formatCoachDate(coach.currentPeriodEndsAt, i18n.language);
  const giftEnd = formatCoachDate(coach.givenFreeUntil, i18n.language);

  const price =
    coach.priceMinorUnits == null
      ? null
      : t(
          coach.billingInterval === BillingInterval.Annual
            ? 'adminCoaches.drawer.perYear'
            : 'adminCoaches.drawer.perMonth',
          { price: formatCoachMoney(coach.priceMinorUnits, coach.currency, i18n.language) },
        );

  const note = (() => {
    if (coach.failureDetail) {
      return coach.failureDetail;
    }
    if (coach.status === AdminCoachStatus.GivenFree || coach.givenFreeAt) {
      return giftEnd ? t('adminCoaches.drawer.givenUntil', { date: giftEnd }) : t('adminCoaches.drawer.givenNoEnd');
    }
    if (periodEnd && coach.cancelAtPeriodEnd) {
      return t('adminCoaches.drawer.endsOn', { date: periodEnd });
    }
    if (periodEnd && price) {
      return t('adminCoaches.drawer.renews', { date: periodEnd });
    }
    return t('adminCoaches.profile.freeNote');
  })();

  const next = (() => {
    switch (coach.status) {
      case AdminCoachStatus.Paying:
      case AdminCoachStatus.PaymentFailed:
        return { value: periodEnd, note: t('adminCoaches.profile.nextNote.renews') };
      case AdminCoachStatus.Cancelling:
        return { value: periodEnd, note: t('adminCoaches.profile.nextNote.ends') };
      case AdminCoachStatus.GivenFree:
        return giftEnd
          ? { value: giftEnd, note: t('adminCoaches.profile.nextNote.giftEnds') }
          : { value: t('adminCoaches.table.noEnd'), note: t('adminCoaches.profile.nextNote.giftNoEnd') };
      default:
        return { value: t('adminCoaches.profile.noValue'), note: t('adminCoaches.profile.nextNote.nothing') };
    }
  })();

  return (
    <section
      aria-labelledby="coach-plan-heading"
      className="flex flex-col gap-4.5 rounded-2xl border border-line bg-card px-6 py-5.5 shadow-panel"
    >
      <div className="flex flex-wrap items-center gap-3">
        <h2 id="coach-plan-heading" className="font-display text-stat leading-tight font-semibold text-ink">
          {coachPlanName(coach.plan, i18n.language)}
        </h2>
        <CoachStatusBadge status={coach.status} endsAt={coach.currentPeriodEndsAt} />
        {price && <span className="ml-auto text-subhead font-semibold text-ink">{price}</span>}
      </div>

      <p
        className={`text-copy ${coach.failureDetail ? 'rounded-lg bg-error-soft px-3 py-2 text-error' : 'text-muted-foreground'}`}
      >
        {note}
      </p>

      <div className="grid grid-cols-1 gap-4 sm:grid-cols-3">
        <Tile
          label={t('adminCoaches.drawer.clients')}
          value={
            max === null
              ? t('adminCoaches.drawer.clientsUnlimited', { used })
              : t('adminCoaches.drawer.clientsOf', { used, max })
          }
        >
          {max !== null && (
            <div
              role="progressbar"
              aria-label={t('adminCoaches.drawer.clients')}
              aria-valuemin={0}
              aria-valuemax={max}
              aria-valuenow={Math.min(used, max)}
              className="h-1.5 overflow-hidden rounded-full bg-line"
            >
              {/* The fill width is a runtime percentage and cannot be a static class. */}
              <div className={`h-full rounded-full ${barClass}`} style={{ width: `${percent}%` }} />
            </div>
          )}
        </Tile>
        <Tile label={t('adminCoaches.profile.nextPayment')} value={next.value || t('adminCoaches.profile.noValue')}>
          <span className="text-meta text-muted-foreground">{next.note}</span>
        </Tile>
        <Tile label={t('adminCoaches.profile.promotion')} value={t('adminCoaches.profile.promotionNone')}>
          <span className="text-meta text-muted-foreground">{t('adminCoaches.profile.promotionNoneNote')}</span>
        </Tile>
      </div>
    </section>
  );
}

export function CoachPlanCardSkeleton() {
  return (
    <div className="flex flex-col gap-3">
      <Skeleton className="h-48 w-full rounded-2xl" />
      <Skeleton className="h-72 w-full rounded-2xl" />
    </div>
  );
}
