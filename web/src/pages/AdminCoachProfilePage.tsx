import { Link, useNavigate, useParams } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { ChevronLeft, ExternalLink } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import CoachActions from '@/components/admin-coaches/CoachActions';
import CoachHistory from '@/components/admin-coaches/CoachHistory';
import CoachPlanCard from '@/components/admin-coaches/CoachPlanCard';
import { coachInitials, formatCoachDate } from '@/components/admin-coaches/coachFormat';
import { useAdminCoach } from '@/components/admin-coaches/useAdminCoachesQueries';
import { useAdminPlans } from '@/components/admin-plans/useAdminPlansQueries';

const COACHES_PATH = '/admin/coaches';

/** Role chip colours: nutrition green and training orange, anything else neutral. */
const ROLE_CHIP_CLASS: Record<string, string> = {
  Nutritionist: 'bg-nutrition-soft text-nutrition-ink',
  Trainer: 'bg-training-soft text-training-ink',
};

/** Form Up admin → one coach: plan, admin actions and history. Shows counts about the coach's clients, never the clients. */
export default function AdminCoachProfilePage() {
  const { t, i18n } = useTranslation();
  const navigate = useNavigate();
  const { coachId = '' } = useParams();
  const coachQuery = useAdminCoach(coachId || null);
  const plansQuery = useAdminPlans();
  const coach = coachQuery.data;
  const plans = plansQuery.data ?? [];
  const joined = formatCoachDate(coach?.joinedAt, i18n.language);

  return (
    <div className="flex flex-col gap-5.5">
      <Link
        to={COACHES_PATH}
        className="-ml-1.5 inline-flex h-8 items-center gap-1.5 self-start rounded-lg pr-2.5 pl-1.5 text-meta font-semibold text-ink-2 outline-none hover:text-ink focus-visible:ring-3 focus-visible:ring-ring/50"
      >
        <ChevronLeft aria-hidden="true" className="size-4" />
        {t('adminCoaches.title')}
      </Link>

      {coachQuery.isPending && (
        <div className="flex flex-col gap-4">
          <Skeleton className="h-16 w-full max-w-md rounded-xl" />
          <Skeleton className="h-56 w-full rounded-2xl" />
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
          <header className="flex flex-wrap items-center gap-4.5">
            <span
              aria-hidden="true"
              className="flex size-16 shrink-0 items-center justify-center rounded-full bg-sunken text-card-title font-semibold text-ink"
            >
              {coachInitials(coach.name)}
            </span>
            <div className="flex min-w-0 flex-col gap-1.5">
              <h1 className="font-display text-display leading-tight font-semibold text-ink">
                {coach.name ?? t('adminCoaches.drawer.titleFallback')}
              </h1>
              <div className="flex flex-wrap items-center gap-x-2.5 gap-y-1.5 text-copy text-muted-foreground">
                {coach.email && <span>{coach.email}</span>}
                {joined && (
                  <>
                    <span aria-hidden="true">·</span>
                    <span>{t('adminCoaches.profile.joined', { date: joined })}</span>
                  </>
                )}
                {(coach.roles ?? []).map((role) => (
                  <span
                    key={role}
                    className={`rounded-md px-2 py-0.5 text-meta font-semibold whitespace-nowrap ${ROLE_CHIP_CLASS[role] ?? 'bg-sunken text-ink-2'}`}
                  >
                    {t(`adminCoaches.roles.${role}`, { defaultValue: role })}
                  </span>
                ))}
              </div>
            </div>
            <Button
              type="button"
              variant="outline"
              size="lg"
              className="ml-auto px-3.5 font-semibold"
              disabled
              title={t('shell.comingSoon')}
            >
              <ExternalLink aria-hidden="true" />
              {t('adminCoaches.drawer.openInStripe')}
            </Button>
          </header>

          <div className="grid grid-cols-1 items-start gap-5 lg:grid-cols-[minmax(0,1fr)_23.75rem]">
            <div className="flex min-w-0 flex-col gap-5">
              <CoachPlanCard coach={coach} />
              <CoachActions coach={coach} coachId={coachId} plans={plans} onGone={() => navigate(COACHES_PATH)} />
            </div>
            <div className="flex min-w-0 flex-col gap-5">
              <CoachHistory entries={coach.history ?? []} plans={plans} />
            </div>
          </div>
        </>
      )}
    </div>
  );
}
