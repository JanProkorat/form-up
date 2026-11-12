import { useEffect, useRef, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { useSearchParams } from 'react-router-dom';
import { ChevronRight, FileText, Send } from 'lucide-react';
import { QuestionnaireResponseKind, type ClientFormItem } from '@/api/questionnaires';
import type { GetClientDashboardResponse } from '@/api/generated';
import FormDetail from '@/components/client-detail/forms/FormDetail';
import FormListCard from '@/components/client-detail/forms/FormListCard';
import SendFormDialog from '@/components/client-detail/forms/SendFormDialog';
import PaywallCard from '@/components/subscription/PaywallCard';
import { useSubscription } from '@/hooks/useSubscriptionQueries';
import {
  FORMS_FILTERS,
  countForms,
  defaultSelection,
  isAnswered,
  matchesFilter,
  pinOnboarding,
  type FormsFilter,
} from '@/components/client-detail/forms/clientFormsModel';
import { useClientForms, useMarkClientFormSeen } from '@/components/client-forms/useClientForms';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { getErrorStatus, showApiError } from '@/lib/api-errors';
import { cn } from '@/lib/utils';

interface Props {
  clientId: string;
  dashboard: GetClientDashboardResponse;
}

function previousYear(items: readonly ClientFormItem[], language: string): string {
  const source = items[0]?.submittedAt ?? items[0]?.sentAt;
  const date = source ? new Date(source) : null;
  return date && !Number.isNaN(date.getTime()) ? date.toLocaleDateString(language, { year: 'numeric' }) : '';
}

/**
 * Forms tab of the client detail page: the client's forms and check-ins on the left, the selected answers on the right.
 * A new questionnaire or onboarding answer is marked seen when it opens; a check-in only by "Mark reviewed".
 */
export default function ClientFormsTab({ clientId, dashboard }: Props) {
  const { t, i18n } = useTranslation();
  const [searchParams] = useSearchParams();
  const requestedId = searchParams.get('form');
  const formsLocked = !useSubscription().isEntitled('canSendQuestionnaires');
  const forms = useClientForms(clientId);
  const markSeen = useMarkClientFormSeen(clientId);
  const markSeenMutate = markSeen.mutate;
  const [filter, setFilter] = useState<FormsFilter>('all');
  const [pickedId, setPickedId] = useState<string | null>(null);
  const [previousOpen, setPreviousOpen] = useState<boolean | null>(null);
  const [sendOpen, setSendOpen] = useState(false);
  const autoMarked = useRef(new Set<string>());

  const clientName = dashboard.firstName?.trim() || t('clientDetail.photos.clientFallback');
  const current = forms.data?.current ?? [];
  const previous = forms.data?.previous ?? [];
  const visibleCurrent = pinOnboarding(current.filter((item) => matchesFilter(item, filter)));
  const visiblePrevious = previous.filter((item) => matchesFilter(item, filter));

  const wantedId = pickedId ?? requestedId;
  const wanted = [...visibleCurrent, ...visiblePrevious].find(
    (item) => item.responsePublicId === wantedId && isAnswered(item),
  );
  const selected = wanted ?? defaultSelection(visibleCurrent);
  const selectedIsPrevious = selected !== undefined && previous.some((item) => item.responsePublicId === selected.responsePublicId);
  const previousExpanded = previousOpen ?? selectedIsPrevious;

  // Opening a new answer clears its dot. Check-ins wait for "Mark reviewed"; earlier collaborations are read-only.
  const selectedId = selected?.responsePublicId;
  const markOnOpen =
    selected !== undefined && selected.isNew === true && !selectedIsPrevious && selected.kind !== QuestionnaireResponseKind.CheckIn;
  useEffect(() => {
    if (markOnOpen && selectedId && !autoMarked.current.has(selectedId)) {
      autoMarked.current.add(selectedId);
      markSeenMutate(selectedId);
    }
  }, [markOnOpen, selectedId, markSeenMutate]);

  function markReviewed(item: ClientFormItem) {
    if (!item.responsePublicId) {
      return;
    }
    markSeenMutate(item.responsePublicId, {
      // A 409 means it is not submitted yet; nothing to review.
      onError: (error) => {
        if (getErrorStatus(error) !== 409) {
          showApiError(error, 'clientForms.page.markError');
        }
      },
    });
  }

  // Forms are not on the coach's plan: the same paywall as the Forms page.
  if (formsLocked) {
    return (
      <div className="flex justify-center pt-6">
        <PaywallCard feature="forms" />
      </div>
    );
  }

  if (forms.isPending) {
    return (
      <div className="flex flex-col gap-4" data-testid="forms-loading">
        <Skeleton className="h-10 w-64" />
        <Skeleton className="h-96 w-full" />
      </div>
    );
  }

  if (forms.isError) {
    const ended = getErrorStatus(forms.error) === 404;
    return (
      <div role="alert" data-testid="forms-error" className="flex flex-col items-center gap-3 py-16">
        <p className="text-body text-muted-foreground">
          {ended ? t('clientForms.page.noLink', { name: clientName }) : t('clientForms.loadError')}
        </p>
        {!ended && (
          <Button type="button" variant="outline" onClick={() => void forms.refetch()}>
            {t('clientForms.retry')}
          </Button>
        )}
      </div>
    );
  }

  const counts = countForms(current);
  const hasAnswers = [...current, ...previous].some(isAnswered);
  const sendDialog = (
    <SendFormDialog clientId={clientId} clientName={clientName} open={sendOpen} onOpenChange={setSendOpen} />
  );

  if (!hasAnswers) {
    return (
      <div className="flex flex-col gap-4">
        <FormsHeading counts={counts} />
        <div
          data-testid="forms-empty"
          className="flex min-h-96 flex-col items-center justify-center gap-5 rounded-2xl border border-line bg-card px-6 py-12 shadow-panel"
        >
          <span className="flex size-16 items-center justify-center rounded-2xl bg-muted text-ink-2">
            <FileText className="size-7" aria-hidden="true" />
          </span>
          <div className="flex max-w-lg flex-col items-center gap-2 text-center">
            <h3 className="font-display text-title font-semibold text-ink">
              {t('clientForms.page.emptyTitle', { name: clientName })}
            </h3>
            <p className="text-copy text-muted-foreground">{t('clientForms.page.emptyBody', { name: clientName })}</p>
          </div>
          {current.length > 0 && (
            <ul className="flex w-full max-w-lg flex-col gap-2.5">
              {current.map((item) => (
                <FormListCard key={item.responsePublicId} item={item} selected={false} onOpen={() => undefined} />
              ))}
            </ul>
          )}
          <Button type="button" variant="outline" size="lg" className="gap-1.75 px-3.5 font-semibold" onClick={() => setSendOpen(true)}>
            <Send aria-hidden="true" />
            {current.length > 0 ? t('clientForms.page.sendAnother') : t('clientForms.page.send')}
          </Button>
        </div>
        {sendDialog}
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-4">
      <FormsHeading counts={counts}>
        <Button type="button" variant="outline" size="lg" className="gap-1.75 px-3.5 font-semibold" onClick={() => setSendOpen(true)}>
          <Send aria-hidden="true" />
          {t('clientForms.page.send')}
        </Button>
      </FormsHeading>

      <div className="grid items-start gap-5 lg:grid-cols-[minmax(0,1fr)_minmax(0,2.3fr)]">
        <div className="flex min-w-0 flex-col gap-4">
          <div role="group" aria-label={t('clientForms.page.filterLabel')} className="flex flex-wrap gap-2">
            {FORMS_FILTERS.map((key) => (
              <button
                key={key}
                type="button"
                data-testid={`forms-filter-${key}`}
                aria-pressed={filter === key}
                onClick={() => setFilter(key)}
                className={cn(
                  'inline-flex h-9 items-center rounded-full border px-4 text-body font-semibold outline-none focus-visible:ring-3 focus-visible:ring-ring/50',
                  filter === key
                    ? 'border-primary bg-primary text-primary-foreground'
                    : 'border-border bg-card text-ink hover:bg-muted',
                )}
              >
                {t(`clientForms.page.filters.${key}`)}
              </button>
            ))}
          </div>

          <section aria-label={t('clientForms.page.thisCollaboration')} className="flex flex-col gap-2.5">
            <h3 className="text-meta font-bold tracking-wide text-muted-foreground uppercase">
              {t('clientForms.page.thisCollaboration')}
            </h3>
            {visibleCurrent.length === 0 ? (
              <p className="text-body text-muted-foreground">{t('clientForms.page.noMatch')}</p>
            ) : (
              <ul className="flex flex-col gap-2.5">
                {visibleCurrent.map((item) => (
                  <FormListCard
                    key={item.responsePublicId}
                    item={item}
                    selected={item.responsePublicId === selected?.responsePublicId}
                    onOpen={(opened) => setPickedId(opened.responsePublicId ?? null)}
                  />
                ))}
              </ul>
            )}
          </section>

          {visiblePrevious.length > 0 && (
            <section className="flex flex-col gap-2.5">
              <button
                type="button"
                data-testid="previous-toggle"
                aria-expanded={previousExpanded}
                onClick={() => setPreviousOpen(!previousExpanded)}
                className="inline-flex items-center gap-2 rounded-xs text-body font-semibold text-ink-2 outline-none focus-visible:ring-3 focus-visible:ring-ring/50"
              >
                <ChevronRight className={cn('size-4 transition-transform', previousExpanded && 'rotate-90')} aria-hidden="true" />
                {t('clientForms.page.previous', {
                  year: previousYear(visiblePrevious, i18n.language),
                  count: visiblePrevious.length,
                })}
              </button>
              {previousExpanded && (
                <ul className="flex flex-col gap-2.5" data-testid="previous-list">
                  {visiblePrevious.map((item) => (
                    <FormListCard
                      key={item.responsePublicId}
                      item={item}
                      selected={item.responsePublicId === selected?.responsePublicId}
                      onOpen={(opened) => setPickedId(opened.responsePublicId ?? null)}
                    />
                  ))}
                </ul>
              )}
            </section>
          )}
        </div>

        {selected ? (
          <FormDetail
            key={selected.responsePublicId}
            item={selected}
            current={current}
            clientPublicId={dashboard.clientPublicId ?? ''}
            readOnly={selectedIsPrevious}
            isMarking={markSeen.isPending}
            onSelect={(item) => setPickedId(item.responsePublicId ?? null)}
            onMarkReviewed={markReviewed}
          />
        ) : (
          <p className="rounded-2xl border border-line bg-card p-6 text-body text-muted-foreground shadow-panel">
            {t('clientForms.page.selectAForm')}
          </p>
        )}
      </div>
      {sendDialog}
    </div>
  );
}

function FormsHeading({ counts, children }: { counts: { answered: number; waiting: number }; children?: ReactNode }) {
  const { t } = useTranslation();
  return (
    <div className="flex flex-wrap items-center justify-between gap-3">
      <div className="flex flex-wrap items-baseline gap-x-3">
        <h2 className="font-display text-title font-semibold text-ink">{t('clientForms.page.title')}</h2>
        <span className="text-copy text-muted-foreground">{t('clientForms.page.counts', counts)}</span>
      </div>
      {children}
    </div>
  );
}
