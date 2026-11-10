import { useState } from 'react';
import { Link, Navigate, useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { Info, LogOut } from 'lucide-react';
import Wordmark from '@/components/brand/Wordmark';
import KeepClientsTable from '@/components/subscription/KeepClientsTable';
import PlanCard from '@/components/subscription/PlanCard';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import {
  useChooseFreePlan,
  useCoachPlans,
  useKeepClients,
  useSubscription,
} from '@/hooks/useSubscriptionQueries';
import { showApiError } from '@/lib/api-errors';
import { useAuthStore } from '@/stores/auth';

const FREE_PLAN_CODE = 'free';

/**
 * Blocking plan picker for a coach whose admin-given free plan ended. Full screen, no sidebar: the only exits are
 * choosing Free (paid plans wait for billing) or the few account links the backend still allows. Free over its
 * client cap first asks which clients to keep, the same choice as after a lapse.
 */
export default function PlanChoicePage() {
  const { t, i18n } = useTranslation();
  const navigate = useNavigate();
  const logout = useAuthStore((s) => s.logout);
  const subscription = useSubscription();
  const plansQuery = useCoachPlans(subscription.isCoach && subscription.planChoiceRequired);
  const keepQuery = useKeepClients(subscription.isCoach && subscription.planChoiceRequired);
  const chooseMutation = useChooseFreePlan();
  const [step, setStep] = useState<'plans' | 'keep'>('plans');
  // null = untouched: the selection is whatever Free keeps by default.
  const [choice, setChoice] = useState<ReadonlySet<string> | null>(null);

  const clients = keepQuery.data?.clients ?? [];
  const cap = keepQuery.data?.cap ?? null;
  const overCap = cap !== null && clients.length > cap;
  const required = cap === null ? clients.length : Math.min(cap, clients.length);
  const defaultKept = new Set(clients.filter((client) => client.isKept).map((client) => client.clientId ?? ''));
  const selected = choice ?? defaultKept;
  const full = selected.size >= required;
  const ready = selected.size === required;

  // The choice is made (or was never needed): leave once the subscription has settled on that answer.
  if (subscription.data && !subscription.query.isFetching && !subscription.planChoiceRequired) {
    return <Navigate to="/clients" replace />;
  }

  const toggle = (clientId: string) => {
    const next = new Set(selected);
    if (next.has(clientId)) {
      next.delete(clientId);
    } else if (next.size < required) {
      next.add(clientId);
    }
    setChoice(next);
  };

  const submit = (keepClientIds: string[]) => {
    chooseMutation.mutate(keepClientIds, {
      onSuccess: () => navigate('/clients', { replace: true }),
      onError: (error) => showApiError(error, 'subscription.planChoice.chooseError'),
    });
  };

  const chooseFree = () => {
    if (keepQuery.isError) {
      showApiError(keepQuery.error, 'subscription.lapse.loadError');
      void keepQuery.refetch();
      return;
    }
    if (overCap) {
      setStep('keep');
      return;
    }
    submit([]);
  };

  const plan = subscription.endedPlanName || subscription.planName;
  const freeUntil = subscription.data?.freeUntil;
  const endedOn = freeUntil ? new Intl.DateTimeFormat(i18n.language, { dateStyle: 'medium' }).format(new Date(freeUntil)) : null;
  const plans = plansQuery.data?.plans ?? [];

  return (
    <div className="flex min-h-screen flex-col bg-background">
      <header className="flex flex-wrap items-center gap-3 border-b border-line bg-card px-6 py-3.5">
        <Wordmark size="nav" />
        <nav aria-label={t('subscription.planChoice.header.nav')} className="ml-auto flex items-center gap-1.5">
          <Button asChild variant="ghost" size="lg" className="px-3 font-semibold">
            <Link to="/inbox">{t('subscription.planChoice.header.messages')}</Link>
          </Button>
          <Button asChild variant="ghost" size="lg" className="px-3 font-semibold">
            <Link to="/settings">{t('subscription.planChoice.header.settings')}</Link>
          </Button>
          <Button type="button" variant="ghost" size="lg" className="gap-2 px-3 font-semibold" onClick={logout}>
            <LogOut className="size-4" aria-hidden="true" />
            {t('auth.logout')}
          </Button>
        </nav>
      </header>

      <main className="mx-auto flex w-full max-w-7xl flex-1 flex-col gap-5 bg-page-glow px-6 py-7">
        <div role="status" className="flex items-center gap-3.5 rounded-xl bg-training-soft px-4 py-3.5">
          <Info className="size-4.5 shrink-0 text-training-ink" aria-hidden="true" />
          <span className="text-copy font-semibold text-ink">
            {endedOn
              ? t('subscription.planChoice.bannerTitle', { plan, date: endedOn })
              : t('subscription.planChoice.bannerTitleNoDate', { plan })}
          </span>
        </div>

        {step === 'keep' ? (
          <>
            <div className="flex flex-col gap-1.5">
              <h1 className="text-display font-semibold text-ink">{t('subscription.lapse.title', { count: required })}</h1>
              <p className="text-copy text-muted-foreground">{t('subscription.lapse.subtitle')}</p>
            </div>

            <div className="flex min-h-0 flex-col overflow-hidden rounded-2xl border border-line bg-card shadow-panel">
              <div className="min-h-0 overflow-y-auto [&_[data-slot=table-container]]:overflow-visible">
                <KeepClientsTable
                  clients={clients}
                  selected={selected}
                  full={full}
                  disabled={chooseMutation.isPending}
                  onToggle={toggle}
                />
              </div>
            </div>

            <div className="flex flex-wrap items-center gap-3">
              <span className="font-semibold text-ink" aria-live="polite">
                {t('subscription.lapse.selected', { count: selected.size, cap: required })}
              </span>
              <Button
                type="button"
                variant="outline"
                size="lg"
                className="ml-auto px-3.5 font-semibold"
                disabled={chooseMutation.isPending}
                onClick={() => setStep('plans')}
              >
                {t('subscription.planChoice.keep.back')}
              </Button>
              <Button
                type="button"
                size="lg"
                className="px-3.5 font-semibold"
                disabled={!ready || chooseMutation.isPending}
                onClick={() => submit([...selected])}
              >
                {t('subscription.planChoice.keep.confirm', { count: required })}
              </Button>
            </div>
          </>
        ) : (
          <>
            <div className="flex flex-col gap-1.5">
              <h1 className="text-display font-semibold text-ink">{t('subscription.planChoice.title')}</h1>
              <p className="text-copy text-muted-foreground">{t('subscription.planChoice.subtitle')}</p>
            </div>

            {plansQuery.isError ? (
              <div role="alert" className="flex flex-col items-start gap-3">
                <p className="text-copy text-error">{t('subscription.planChoice.loadError')}</p>
                <Button
                  type="button"
                  variant="outline"
                  size="lg"
                  className="px-3.5 font-semibold"
                  onClick={() => void plansQuery.refetch()}
                >
                  {t('subscription.lapse.retry')}
                </Button>
              </div>
            ) : plansQuery.isPending ? (
              <Skeleton className="h-96 w-full rounded-2xl" />
            ) : (
              <div className="grid items-stretch gap-4 sm:grid-cols-2 xl:grid-cols-4">
                {plans.map((item) => {
                  const isFree = item.code === FREE_PLAN_CODE;
                  return (
                    <PlanCard
                      key={item.code}
                      plan={item}
                      selectable={isFree}
                      busy={chooseMutation.isPending || (isFree && keepQuery.isPending)}
                      onChoose={chooseFree}
                    />
                  );
                })}
              </div>
            )}
          </>
        )}
      </main>
    </div>
  );
}
