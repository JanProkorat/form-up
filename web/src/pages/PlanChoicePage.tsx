import { useState } from 'react';
import { Link, Navigate, useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { Info, LogOut } from 'lucide-react';
import { CheckoutFlow, type CoachPlanDto } from '@/api/generated';
import Wordmark from '@/components/brand/Wordmark';
import KeepClientsTable from '@/components/subscription/KeepClientsTable';
import PlanCard from '@/components/subscription/PlanCard';
import { coachPlanName } from '@/components/subscription/planNames';
import { planDisplayPrice, secondRoleAddOnOf } from '@/components/subscription/planPricing';
import TaxNote from '@/components/subscription/TaxNote';
import { usePlanSelection } from '@/components/subscription/usePlanSelection';
import { useActiveRoles } from '@/hooks/useActiveRoles';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import {
  useChooseFreePlan,
  useCoachPlans,
  useInvalidateSubscription,
  useKeepClients,
  useSetKeepClients,
  useSubscription,
} from '@/hooks/useSubscriptionQueries';
import { showApiError } from '@/lib/api-errors';
import { useAuthStore } from '@/stores/auth';

const FREE_PLAN_CODE = 'free';

/**
 * Blocking plan decision screen for a coach whose plan changed: an admin-given plan ended, a paid plan lapsed or
 * was ended, or an admin moved them to a smaller plan. Full screen, no sidebar: the only exits are keeping the
 * current plan (picking which clients stay when over its limit; paid plans wait for billing) or the few account
 * links the backend still allows.
 */
export default function PlanChoicePage() {
  const { t, i18n } = useTranslation();
  const navigate = useNavigate();
  const logout = useAuthStore((s) => s.logout);
  const subscription = useSubscription();
  const invalidateSubscription = useInvalidateSubscription();
  const decisionRequired = subscription.isCoach && subscription.planDecisionRequired;
  const plansQuery = useCoachPlans(decisionRequired);
  const keepQuery = useKeepClients(decisionRequired);
  const chooseMutation = useChooseFreePlan();
  const keepMutation = useSetKeepClients();
  const selection = usePlanSelection(CheckoutFlow.Settings);
  const { hasRole } = useActiveRoles();
  // Prices include the second-role add-on for a coach who holds both roles.
  const bothRoles = hasRole('Trainer') && hasRole('Nutritionist');
  const saving = chooseMutation.isPending || keepMutation.isPending;
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
  if (subscription.data && !subscription.query.isFetching && !subscription.planDecisionRequired) {
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

  const callbacks = {
    onSuccess: () => navigate('/clients', { replace: true }),
    onError: (error: unknown) => {
      showApiError(error, 'subscription.planChoice.chooseError');
      // The decision may no longer be needed, or a client may have left: re-read both.
      void invalidateSubscription();
      void keepQuery.refetch();
    },
  };

  // A given plan that ended goes through choose-free; every other decision saves the keep choice.
  const submit = (keepClientIds: string[]) => {
    if (subscription.planChoiceRequired) {
      chooseMutation.mutate(keepClientIds, callbacks);
    } else {
      keepMutation.mutate(keepClientIds, callbacks);
    }
  };

  const keepCurrent = () => {
    if (keepQuery.isError) {
      showApiError(keepQuery.error, 'subscription.lapse.loadError');
      void keepQuery.refetch();
      return;
    }
    if (overCap || !subscription.planChoiceRequired) {
      setStep('keep');
      return;
    }
    submit([]);
  };

  const freeCurrent = subscription.planChoiceRequired || subscription.data?.planCode === FREE_PLAN_CODE;
  const currentCode = freeCurrent ? FREE_PLAN_CODE : (subscription.data?.planCode ?? '');
  const plans = plansQuery.data?.plans ?? [];
  const currentPlan = plans.find((item) => item.code === currentCode);
  const currentName = currentPlan ? coachPlanName(currentPlan, i18n.language) : subscription.planName;
  const limit = cap ?? subscription.clientLimit ?? 0;
  const have = keepQuery.data ? clients.length : subscription.activeClientCount;
  const freeUntil = subscription.data?.freeUntil;
  const endedOn = freeUntil ? new Intl.DateTimeFormat(i18n.language, { dateStyle: 'medium' }).format(new Date(freeUntil)) : null;
  const endedPlan = subscription.endedPlanName || subscription.planName;
  const lapsedPlan = subscription.lapsedPlanName;
  const subtitle = subscription.planChoiceRequired
    ? endedOn
      ? t('subscription.planChoice.bannerTitle', { plan: endedPlan, date: endedOn })
      : t('subscription.planChoice.bannerTitleNoDate', { plan: endedPlan })
    : !freeCurrent
      ? t('subscription.planChoice.decision.smaller', { plan: currentName, count: limit, have })
      : lapsedPlan
        ? t('subscription.planChoice.decision.lapse', { plan: lapsedPlan, count: limit, have })
        : t('subscription.planChoice.decision.free', { count: limit, have });

  const actionFor = (
    item: CoachPlanDto,
  ): { label: string; primary: boolean; current: boolean; onClick: () => void } => {
    const name = coachPlanName(item, i18n.language);
    if (item.code === currentCode) {
      return {
        label: overCap || !subscription.planChoiceRequired
          ? t('subscription.planChoice.card.keep', { plan: name, count: required })
          : t('subscription.planChoice.card.keepAll', { plan: name }),
        primary: true,
        current: true,
        onClick: keepCurrent,
      };
    }
    // Free ends a paid plan at the end of the period; a paid plan goes through Stripe Checkout.
    if (item.code === FREE_PLAN_CODE) {
      return {
        label: t('subscription.planChoice.card.switchFree'),
        primary: false,
        current: false,
        onClick: selection.switchToFree,
      };
    }
    return {
      label: t('subscription.planChoice.card.upgrade', { plan: name }),
      primary: true,
      current: false,
      onClick: () => selection.choosePaid(item, bothRoles && secondRoleAddOnOf(item) !== null),
    };
  };

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
        {step === 'keep' ? (
          <>
            <div className="flex flex-col gap-1.5">
              <h1 className="text-display font-semibold text-ink">{t('subscription.lapse.title', { count: required })}</h1>
              <p className="text-copy text-muted-foreground">{t('subscription.lapse.subtitle')}</p>
              {keepQuery.data?.isChoiceLocked !== true && clients.length > 0 && (
                <p className="flex w-fit items-center gap-2 rounded-lg bg-sunken px-3 py-2 text-body text-muted-foreground">
                  <Info className="size-4 shrink-0" aria-hidden="true" />
                  {t('subscription.lapse.preselectedHint', { count: required })}
                </p>
              )}
            </div>

            <div className="flex min-h-0 flex-col overflow-hidden rounded-2xl border border-line bg-card shadow-panel">
              <div className="min-h-0 overflow-y-auto [&_[data-slot=table-container]]:overflow-visible">
                <KeepClientsTable
                  clients={clients}
                  selected={selected}
                  full={full}
                  disabled={saving}
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
                disabled={saving}
                onClick={() => setStep('plans')}
              >
                {t('subscription.planChoice.keep.back')}
              </Button>
              <Button
                type="button"
                size="lg"
                className="px-3.5 font-semibold"
                disabled={!ready || saving}
                onClick={() => submit([...selected])}
              >
                {subscription.planChoiceRequired
                  ? t('subscription.planChoice.keep.confirm', { count: required })
                  : t('subscription.planChoice.keep.save', { plan: currentName, count: required })}
              </Button>
            </div>
          </>
        ) : (
          <>
            <div className="flex flex-col gap-1.5">
              <h1 className="text-display font-semibold text-ink">{t('subscription.planChoice.title')}</h1>
              <p className="text-copy text-muted-foreground">{subtitle}</p>
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
              <>
                <div className="grid items-stretch gap-4 sm:grid-cols-2 xl:grid-cols-4">
                  {plans.map((item) => {
                    const action = actionFor(item);
                    return (
                      <PlanCard
                        key={item.code}
                        plan={item}
                        current={action.current}
                        actionLabel={action.label}
                        primary={action.primary}
                        actionDisabled={false}
                        busy={saving || selection.busy || (action.current && keepQuery.isPending)}
                        priceMinorUnits={planDisplayPrice(item, bothRoles)}
                        onAction={action.onClick}
                      />
                    );
                  })}
                </div>
                <TaxNote taxMode={plansQuery.data?.taxMode} />
              </>
            )}
          </>
        )}
      </main>
    </div>
  );
}
