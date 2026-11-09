import { useTranslation } from 'react-i18next';
import { ExternalLink } from 'lucide-react';
import {
  AdminCoachHistoryKind,
  BillingInterval,
  type AdminCoachHistoryEntryDto,
  type GetAdminCoachResponse,
} from '@/api/admin-coaches';
import type { SubscriptionPlanDto } from '@/api/admin-subscription-plans';
import { Button } from '@/components/ui/button';
import { Sheet, SheetContent, SheetDescription, SheetFooter, SheetHeader, SheetTitle } from '@/components/ui/sheet';
import { Skeleton } from '@/components/ui/skeleton';
import { planName } from '@/components/admin-plans/planForm';
import CoachActions from '@/components/admin-coaches/CoachActions';
import CoachStatusBadge from '@/components/admin-coaches/CoachStatusBadge';
import { coachPlanName, formatCoachDate, formatCoachMoney } from '@/components/admin-coaches/coachFormat';
import { useAdminCoach } from '@/components/admin-coaches/useAdminCoachesQueries';
import { useAdminPlans } from '@/components/admin-plans/useAdminPlansQueries';

interface Props {
  /** The coach to show; the drawer is closed while this is null. */
  coachId: string | null;
  onClose: () => void;
}

/** Side drawer for one coach: plan card, admin actions and history. Never shows anything about the coach's clients but counts. */
export default function CoachDrawer({ coachId, onClose }: Props) {
  return (
    <Sheet
      open={coachId !== null}
      onOpenChange={(open) => {
        if (!open) {
          onClose();
        }
      }}
    >
      <SheetContent className="gap-0 sm:w-drawer sm:max-w-none">
        {coachId !== null && <DrawerBody key={coachId} coachId={coachId} onClose={onClose} />}
      </SheetContent>
    </Sheet>
  );
}

function DrawerBody({ coachId, onClose }: { coachId: string; onClose: () => void }) {
  const { t, i18n } = useTranslation();
  const coachQuery = useAdminCoach(coachId);
  const plansQuery = useAdminPlans();
  const coach = coachQuery.data;
  const plans = plansQuery.data ?? [];

  const roles = (coach?.roles ?? []).map((role) => t(`adminCoaches.roles.${role}`, { defaultValue: role }));
  const joined = formatCoachDate(coach?.joinedAt, i18n.language);

  return (
    <>
      <SheetHeader className="border-b border-border px-6 py-5 pr-14">
        <SheetTitle className="text-auth-title">{coach?.name ?? t('adminCoaches.drawer.titleFallback')}</SheetTitle>
        <SheetDescription className="text-body">
          {coach
            ? [coach.email, roles.join(' · ') || null, joined ? t('adminCoaches.drawer.joined', { date: joined }) : null]
                .filter(Boolean)
                .join(' · ')
            : t('adminCoaches.drawer.loading')}
        </SheetDescription>
      </SheetHeader>

      <div className="flex flex-1 flex-col gap-6 overflow-y-auto px-6 py-5">
        {coachQuery.isPending && (
          <div className="flex flex-col gap-3">
            <Skeleton className="h-32 w-full rounded-xl" />
            <Skeleton className="h-48 w-full rounded-xl" />
          </div>
        )}
        {coachQuery.isError && (
          <div className="flex flex-col items-start gap-3">
            <p className="text-body text-muted-foreground">{t('adminCoaches.drawer.loadError')}</p>
            <Button type="button" variant="outline" size="sm" onClick={() => void coachQuery.refetch()}>
              {t('adminCoaches.retry')}
            </Button>
          </div>
        )}
        {coach && (
          <>
            <PlanCard coach={coach} />
            <CoachActions coach={coach} coachId={coachId} plans={plans} onGone={onClose} />
            <History entries={coach.history ?? []} plans={plans} />
            <p className="text-meta text-muted-foreground">{t('adminCoaches.drawer.privacy')}</p>
          </>
        )}
      </div>

      <SheetFooter className="flex-row items-center gap-2 border-t border-border px-6 py-4">
        <Button type="button" variant="outline" size="lg" disabled title={t('shell.comingSoon')}>
          <ExternalLink aria-hidden="true" />
          {t('adminCoaches.drawer.openInStripe')}
        </Button>
        <span className="mr-auto" />
        <Button type="button" variant="outline" size="lg" onClick={onClose}>
          {t('common.close')}
        </Button>
      </SheetFooter>
    </>
  );
}

function PlanCard({ coach }: { coach: GetAdminCoachResponse }) {
  const { t, i18n } = useTranslation();
  const used = coach.clientsUsed ?? 0;
  const max = coach.clientsMax ?? null;
  const percent = max === null || max <= 0 ? 0 : Math.min(100, Math.round((used / max) * 100));
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

  return (
    <section className="flex flex-col gap-4 rounded-xl border border-line px-4 py-4" aria-label={t('adminCoaches.drawer.plan')}>
      <div className="flex flex-wrap items-center gap-3">
        <span className="text-copy font-semibold text-ink">{coachPlanName(coach.plan, i18n.language)}</span>
        <CoachStatusBadge status={coach.status} endsAt={coach.currentPeriodEndsAt} />
        {price && <span className="ml-auto text-copy font-semibold text-ink">{price}</span>}
      </div>

      {coach.failureDetail && (
        <p className="rounded-lg bg-error-soft px-3 py-2 text-body text-error">{coach.failureDetail}</p>
      )}
      {periodEnd && !coach.cancelAtPeriodEnd && price && (
        <p className="text-body text-muted-foreground">{t('adminCoaches.drawer.renews', { date: periodEnd })}</p>
      )}
      {periodEnd && coach.cancelAtPeriodEnd && (
        <p className="text-body text-muted-foreground">{t('adminCoaches.drawer.endsOn', { date: periodEnd })}</p>
      )}
      {coach.givenFreeAt && (
        <p className="text-body text-muted-foreground">
          {giftEnd ? t('adminCoaches.drawer.givenUntil', { date: giftEnd }) : t('adminCoaches.drawer.givenNoEnd')}
        </p>
      )}

      <div className="flex flex-col gap-1.5">
        <div className="flex items-baseline justify-between text-body">
          <span className="font-medium text-ink-2">{t('adminCoaches.drawer.clients')}</span>
          <span className="text-muted-foreground">
            {max === null
              ? t('adminCoaches.drawer.clientsUnlimited', { used })
              : t('adminCoaches.drawer.clientsOf', { used, max })}
          </span>
        </div>
        {max !== null && (
          <div
            role="progressbar"
            aria-label={t('adminCoaches.drawer.clients')}
            aria-valuemin={0}
            aria-valuemax={max}
            aria-valuenow={Math.min(used, max)}
            className="h-2 overflow-hidden rounded-full bg-sunken"
          >
            {/* The fill width is a runtime percentage and cannot be a static class. */}
            <div className="h-full rounded-full bg-success" style={{ width: `${percent}%` }} />
          </div>
        )}
      </div>
    </section>
  );
}

const HISTORY_KEY: Record<AdminCoachHistoryKind, string> = {
  [AdminCoachHistoryKind.Joined]: 'joined',
  [AdminCoachHistoryKind.GivenFree]: 'givenFree',
  [AdminCoachHistoryKind.PlanAssigned]: 'planAssigned',
  [AdminCoachHistoryKind.RenewalCancelled]: 'renewalCancelled',
  [AdminCoachHistoryKind.AccountDisabled]: 'accountDisabled',
  [AdminCoachHistoryKind.AccountKept]: 'accountKept',
  [AdminCoachHistoryKind.RoleAdded]: 'roleAdded',
  [AdminCoachHistoryKind.RoleRemoved]: 'roleRemoved',
};

function History({ entries, plans }: { entries: AdminCoachHistoryEntryDto[]; plans: SubscriptionPlanDto[] }) {
  const { t, i18n } = useTranslation();

  const planLabel = (code: string | undefined): string => {
    const plan = plans.find((candidate) => candidate.code === code);
    return plan ? planName(plan, i18n.language) : (code ?? '');
  };

  return (
    <section className="flex flex-col gap-3" aria-labelledby="coach-history-heading">
      <h3 id="coach-history-heading" className="text-copy font-semibold text-ink">
        {t('adminCoaches.history.title')}
      </h3>
      {entries.length === 0 && <p className="text-body text-muted-foreground">{t('adminCoaches.history.empty')}</p>}
      <ol className="flex flex-col gap-3">
        {entries.map((entry, index) => {
          const kind = entry.kind ? HISTORY_KEY[entry.kind] : undefined;
          const who = entry.byCoach
            ? t('adminCoaches.history.byCoach')
            : entry.actorName
              ? t('adminCoaches.history.byAdmin', { name: entry.actorName })
              : null;
          const role = entry.role ? t(`adminCoaches.roles.${entry.role}`, { defaultValue: entry.role }) : '';
          const details = [
            who,
            entry.reason ? t('adminCoaches.history.reason', { reason: entry.reason }) : null,
          ]
            .filter(Boolean)
            .join(' · ');
          return (
            <li key={`${entry.occurredAt}-${index}`} className="flex gap-3">
              <span className="w-24 shrink-0 text-body text-muted-foreground">
                {formatCoachDate(entry.occurredAt, i18n.language)}
              </span>
              <div className="flex min-w-0 flex-col">
                <span className="text-copy text-ink">
                  {kind
                    ? t(`adminCoaches.history.kind.${kind}`, {
                        plan: planLabel(entry.planCode),
                        date: formatCoachDate(entry.until, i18n.language),
                        role,
                        context: entry.until ? 'until' : 'plain',
                      })
                    : t('adminCoaches.history.kind.unknown')}
                </span>
                {details && <span className="text-meta text-muted-foreground">{details}</span>}
              </div>
            </li>
          );
        })}
      </ol>
    </section>
  );
}
