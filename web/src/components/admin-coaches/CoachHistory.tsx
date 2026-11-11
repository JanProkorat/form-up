import { useTranslation } from 'react-i18next';
import { Lock } from 'lucide-react';
import { AdminCoachHistoryKind, type AdminCoachHistoryEntryDto } from '@/api/admin-coaches';
import type { SubscriptionPlanDto } from '@/api/admin-subscription-plans';
import { planName } from '@/components/admin-plans/planForm';
import { formatCoachDate } from '@/components/admin-coaches/coachFormat';

const HISTORY_KEY: Record<AdminCoachHistoryKind, string> = {
  [AdminCoachHistoryKind.Joined]: 'joined',
  [AdminCoachHistoryKind.GivenFree]: 'givenFree',
  [AdminCoachHistoryKind.PlanAssigned]: 'planAssigned',
  [AdminCoachHistoryKind.RenewalCancelled]: 'renewalCancelled',
  [AdminCoachHistoryKind.AccountDisabled]: 'accountDisabled',
  [AdminCoachHistoryKind.AccountKept]: 'accountKept',
  [AdminCoachHistoryKind.RoleAdded]: 'roleAdded',
  [AdminCoachHistoryKind.RoleRemoved]: 'roleRemoved',
  [AdminCoachHistoryKind.PlanEnded]: 'planEnded',
};

/** Entries that end something get a red dot. */
const ENDING_KINDS: ReadonlySet<AdminCoachHistoryKind> = new Set([
  AdminCoachHistoryKind.AccountDisabled,
  AdminCoachHistoryKind.PlanEnded,
]);

interface Props {
  entries: AdminCoachHistoryEntryDto[];
  plans: SubscriptionPlanDto[];
}

/** The coach's history timeline plus the note that client data stays hidden from admins. */
export default function CoachHistory({ entries, plans }: Props) {
  const { t, i18n } = useTranslation();

  const planLabel = (code: string | undefined): string => {
    const plan = plans.find((candidate) => candidate.code === code);
    return plan ? planName(plan, i18n.language) : (code ?? '');
  };

  return (
    <>
      <section
        aria-labelledby="coach-history-heading"
        className="flex flex-col gap-4 rounded-2xl border border-line bg-card px-6 py-5.5 shadow-panel"
      >
        <h2 id="coach-history-heading" className="text-subhead font-semibold text-ink">
          {t('adminCoaches.history.title')}
        </h2>
        {entries.length === 0 && <p className="text-body text-muted-foreground">{t('adminCoaches.history.empty')}</p>}
        <ol className="flex flex-col gap-3.5">
          {entries.map((entry, index) => {
            const kind = entry.kind ? HISTORY_KEY[entry.kind] : undefined;
            const who = entry.byCoach
              ? t('adminCoaches.history.byCoach')
              : entry.actorName
                ? t('adminCoaches.history.byAdmin', { name: entry.actorName })
                : null;
            const role = entry.role ? t(`adminCoaches.roles.${entry.role}`, { defaultValue: entry.role }) : '';
            const details = [who, entry.reason ? t('adminCoaches.history.reason', { reason: entry.reason }) : null]
              .filter(Boolean)
              .join(' · ');
            const ending = entry.kind !== undefined && ENDING_KINDS.has(entry.kind);
            return (
              <li key={`${entry.occurredAt}-${index}`} className="grid grid-cols-[3.5rem_0.5rem_minmax(0,1fr)] items-start gap-2.5">
                <span className="pt-0.5 text-meta text-muted-foreground">
                  {formatCoachDate(entry.occurredAt, i18n.language)}
                </span>
                <span
                  aria-hidden="true"
                  className={`mt-1.5 size-2 rounded-full ${ending ? 'bg-error' : 'bg-muted-foreground'}`}
                />
                <span className="flex min-w-0 flex-col gap-0.5">
                  <span className="text-body font-semibold text-ink">
                    {kind
                      ? t(`adminCoaches.history.kind.${kind}`, {
                          plan: planLabel(entry.planCode),
                          date: formatCoachDate(entry.until, i18n.language),
                          role,
                          context: entry.until ? 'until' : 'plain',
                        })
                      : t('adminCoaches.history.kind.unknown')}
                  </span>
                  {details && <span className="text-meta leading-normal text-muted-foreground">{details}</span>}
                </span>
              </li>
            );
          })}
        </ol>
      </section>
      <p className="flex items-start gap-2 px-1 text-meta leading-normal text-muted-foreground">
        <Lock aria-hidden="true" className="mt-px size-4 shrink-0" />
        {t('adminCoaches.drawer.privacy')}
      </p>
    </>
  );
}
