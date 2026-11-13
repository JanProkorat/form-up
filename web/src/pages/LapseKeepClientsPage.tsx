import { useState } from 'react';
import { Link, Navigate, useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { Info } from 'lucide-react';
import KeepClientsTable from '@/components/subscription/KeepClientsTable';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { useKeepClients, useSetKeepClients, useSubscription } from '@/hooks/useSubscriptionQueries';
import { showApiError, showSuccess } from '@/lib/api-errors';
import { isKeepClientsChoiceLocked, isSubscriptionNotLapsed } from '@/lib/subscriptionErrors';

/** Lapsed coach: pick which clients the Free plan keeps; the others keep their published weeks, then go read-only. */
export default function LapseKeepClientsPage() {
  const { t, i18n } = useTranslation();
  const subscription = useSubscription();
  const keepQuery = useKeepClients(subscription.isCoach);
  const saveMutation = useSetKeepClients();
  const navigate = useNavigate();
  // null = untouched: the selection is whatever the server currently keeps.
  const [choice, setChoice] = useState<ReadonlySet<string> | null>(null);

  const clients = keepQuery.data?.clients ?? [];
  const cap = keepQuery.data?.cap ?? null;
  const serverKept = new Set(clients.filter((client) => client.isKept).map((client) => client.clientId ?? ''));
  // A locked choice keeps every kept client; only restricted ones can fill the free slots.
  const locked = keepQuery.data?.isChoiceLocked === true;
  const freeSlots = keepQuery.data?.freeSlotCount ?? 0;
  const restrictedCount = clients.length - serverKept.size;
  // Fewer clients than the cap means everyone is kept; no cap means the same.
  const required = locked
    ? serverKept.size + Math.min(freeSlots, restrictedCount)
    : cap === null
      ? clients.length
      : Math.min(cap, clients.length);
  const selected = choice ?? serverKept;
  const full = selected.size >= required;
  const ready = selected.size === required;
  // What the counter and title show: the free slots when locked, else the whole selection.
  const shownRequired = locked ? required - serverKept.size : required;
  const shownSelected = locked ? selected.size - serverKept.size : selected.size;

  const toggle = (clientId: string) => {
    if (locked && serverKept.has(clientId)) {
      return;
    }
    const next = new Set(selected);
    if (next.has(clientId)) {
      next.delete(clientId);
    } else if (next.size < required) {
      next.add(clientId);
    }
    setChoice(next);
  };

  const save = () => {
    saveMutation.mutate([...selected], {
      onSuccess: () => {
        setChoice(null);
        showSuccess('subscription.lapse.saveSuccess');
        navigate('/clients');
      },
      onError: (error) =>
        showApiError(
          error,
          isKeepClientsChoiceLocked(error) ? 'subscription.lapse.choiceLockedError' : 'subscription.lapse.saveError',
        ),
    });
  };

  const plan = subscription.lapsedPlanName || subscription.planName;
  const endedAt = subscription.data?.endedAt;
  const endedOn = endedAt ? new Intl.DateTimeFormat(i18n.language, { dateStyle: 'medium' }).format(new Date(endedAt)) : null;
  const notLapsed = keepQuery.isError && isSubscriptionNotLapsed(keepQuery.error);
  const showBanner = subscription.isLapsed && plan !== '';
  const showOverCapBanner = !subscription.isLapsed && subscription.overCapClientCount > 0 && cap !== null;

  // A confirmed choice with no freed slot leaves nothing to pick here (e.g. an old notification link).
  if (keepQuery.isSuccess && locked && required === serverKept.size && !saveMutation.isPending) {
    return <Navigate to="/clients" replace />;
  }

  return (
    <div className="flex h-full flex-col gap-4.5">
      {showBanner && (
        <div role="status" className="flex flex-wrap items-center gap-3.5 rounded-xl bg-training-soft px-4 py-3.5">
          <Info className="size-4.5 shrink-0 text-training-ink" aria-hidden="true" />
          <div className="flex min-w-0 flex-col gap-0.5">
            <span className="text-copy font-semibold text-ink">
              {endedOn
                ? t('subscription.lapse.bannerTitle', { plan, date: endedOn })
                : t('subscription.lapse.bannerTitleNoDate', { plan })}
            </span>
            {cap !== null && (
              <span className="text-body text-ink-2">{t('subscription.lapse.bannerBody', { count: cap })}</span>
            )}
          </div>
          {/* Resubscribing needs the billing integration, which does not exist yet. */}
          <Button type="button" variant="outline" size="lg" className="ml-auto px-3.5 font-semibold" disabled>
            {t('subscription.lapse.resubscribe')}
          </Button>
        </div>
      )}

      {showOverCapBanner && (
        <div role="status" className="flex items-center gap-3.5 rounded-xl bg-training-soft px-4 py-3.5">
          <Info className="size-4.5 shrink-0 text-training-ink" aria-hidden="true" />
          <span className="text-copy font-semibold text-ink">
            {t('subscription.lapse.overCapBanner', { count: cap })}
          </span>
        </div>
      )}

      {notLapsed ? (
        <div className="flex flex-col items-start gap-3">
          <p className="text-copy text-muted-foreground">{t('subscription.lapse.notLapsed')}</p>
          <Button asChild variant="outline" size="lg" className="px-3.5 font-semibold">
            <Link to="/clients">{t('subscription.lapse.backToClients')}</Link>
          </Button>
        </div>
      ) : keepQuery.isError ? (
        <div role="alert" className="flex flex-col items-start gap-3">
          <p className="text-copy text-error">{t('subscription.lapse.loadError')}</p>
          <Button type="button" variant="outline" size="lg" className="px-3.5 font-semibold" onClick={() => void keepQuery.refetch()}>
            {t('subscription.lapse.retry')}
          </Button>
        </div>
      ) : keepQuery.isPending ? (
        <Skeleton className="h-64 w-full rounded-2xl" />
      ) : (
        <>
          <div className="flex flex-col gap-1.5">
            <h1 className="text-display font-semibold text-ink">
              {locked
                ? t('subscription.lapse.lockedTitle', { count: shownRequired })
                : t('subscription.lapse.title', { count: required })}
            </h1>
            <p className="text-copy text-muted-foreground">
              {t(locked ? 'subscription.lapse.lockedSubtitle' : 'subscription.lapse.subtitle')}
            </p>
            {!locked && clients.length > 0 && (
              <p className="flex w-fit items-center gap-2 rounded-lg bg-sunken px-3 py-2 text-body text-muted-foreground">
                <Info className="size-4 shrink-0" aria-hidden="true" />
                {t('subscription.lapse.preselectedHint', { count: required })}
              </p>
            )}
          </div>

          {clients.length === 0 ? (
            <p className="text-copy text-muted-foreground">{t('subscription.lapse.noClients')}</p>
          ) : (
            <>
              <div className="flex min-h-0 flex-col overflow-hidden rounded-2xl border border-line bg-card shadow-panel">
                <div className="min-h-0 overflow-y-auto [&_[data-slot=table-container]]:overflow-visible">
                  <KeepClientsTable
                    clients={clients}
                    selected={selected}
                    full={full}
                    disabled={saveMutation.isPending}
                    locked={locked}
                    onToggle={toggle}
                  />
                </div>
              </div>

              <div className="flex flex-wrap items-center gap-3">
                <span className="font-semibold text-ink" aria-live="polite">
                  {t('subscription.lapse.selected', { count: shownSelected, cap: shownRequired })}
                </span>
                {!locked && <span className="text-body text-muted-foreground">{t('subscription.lapse.changeLater')}</span>}
                <Button
                  type="button"
                  size="lg"
                  className="ml-auto px-3.5 font-semibold"
                  disabled={!ready || saveMutation.isPending}
                  onClick={save}
                >
                  {locked ? t('subscription.lapse.lockedSave') : t('subscription.lapse.save', { count: required })}
                </Button>
              </div>
            </>
          )}
        </>
      )}
    </div>
  );
}
