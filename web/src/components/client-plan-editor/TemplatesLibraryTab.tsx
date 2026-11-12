import { useContext, useEffect, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useQueryClient } from '@tanstack/react-query';
import { GripVertical, Search } from 'lucide-react';
import type { OnboardingDataDto } from '@/api/generated';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Popover, PopoverAnchor } from '@/components/ui/popover';
import { Skeleton } from '@/components/ui/skeleton';
import GoalDot from '@/components/plan-templates/GoalDot';
import { cn } from '@/lib/utils';
import { applyTemplate, needsConfirmation } from '@/components/client-plan-editor/applyTemplate';
import ReplaceWithTemplateDialog from '@/components/client-plan-editor/ReplaceWithTemplateDialog';
import TemplateInfoPopover from '@/components/client-plan-editor/TemplateInfoPopover';
import {
  criteriaFromOnboarding,
  rankTemplates,
  type RankedTemplate,
} from '@/components/client-plan-editor/templateMatching';
import { LibraryFiltersTrigger, TemplateFiltersContent } from '@/components/plan-editor/LibraryFilters';
import type { InfoPanelContext } from '@/components/plan-editor/PlanEditor';
import {
  countTemplateFilters,
  EMPTY_TEMPLATE_FILTERS,
  filterRankedLists,
  TemplateTabStateContext,
  type TemplateFilterState,
} from '@/components/plan-editor/plan-editor-library-filters';
import { toEditorWeeks } from '@/components/plan-editor/plan-editor-mapping';
import { weekHasItems } from '@/components/plan-editor/plan-editor-ops';
import type { EditorWeek, WeekStatus } from '@/components/plan-editor/plan-editor-types';
import { useClientMatchedTemplates, useLoadPlanTemplate } from '@/hooks/usePlanTemplatesQueries';
import { getErrorStatus, showApiError, showError } from '@/lib/api-errors';

interface Props {
  context: InfoPanelContext;
  /** Publication state per week index, from the host. */
  weekStatus: (weekIndex: number) => WeekStatus;
  /** From the client dashboard; undefined until the client has onboarding answers. */
  onboarding?: OnboardingDataDto;
  clientFirstName: string;
  /** Called once a template has been put into the document, so the host can mark the next save. */
  onTemplateApplied?: () => void;
  /** Set for a client outside the coach's plan: Use opens the paywall instead of loading the template. */
  onBlocked?: () => void;
}

interface PendingApply {
  name: string;
  weeks: EditorWeek[];
}

function publishedWeekNumbers(context: InfoPanelContext, weekStatus: Props['weekStatus']): number[] {
  return context.doc.weeks.flatMap((_, index) => (weekStatus(index) === 'published' ? [index + 1] : []));
}

/** Lower-cases and strips diacritics so "cukrovinky" finds "Cukrovinky" and "Müsli" finds "musli". */
function foldText(text: string): string {
  return text.normalize('NFD').replace(/\p{M}/gu, '').toLowerCase();
}

function formatPercent(percent: number): string {
  if (percent === 0) {
    return '0';
  }
  return `${percent > 0 ? '+' : '−'}${Math.abs(percent)}`;
}

interface CardProps {
  item: RankedTemplate;
  best: boolean;
  hasGoalCriterion: boolean;
  busy: boolean;
  disabled: boolean;
  language: string;
  onUse: (item: RankedTemplate) => void;
}

function TemplateCard({ item, best, hasGoalCriterion, busy, disabled, language, onUse }: CardProps) {
  const { t } = useTranslation();
  const { template } = item;
  const [infoOpen, setInfoOpen] = useState(false);
  const infoButtonRef = useRef<HTMLButtonElement>(null);
  const name = template.name ?? '';
  const hasMeals = template.mealsPerDay !== undefined && template.mealsPerDay !== null;
  const goalLabel = template.goal ? t(`clients.values.${template.goal}`, { defaultValue: template.goal }) : undefined;
  const meta = [
    t('clientPlanEditor.templates.weeksShort', { count: template.weekCount ?? 0 }),
    template.avgKcalPerDay !== undefined && template.avgKcalPerDay !== null
      ? t('planEditor.cell.kcal', { kcal: Math.round(template.avgKcalPerDay).toLocaleString(language) })
      : null,
  ].filter((part): part is string => part !== null);

  const chips: string[] = [];
  if (hasGoalCriterion) {
    chips.push(item.goalMatch ? t('clientPlanEditor.templates.match.goal') : t('clientPlanEditor.templates.match.goalDiffers'));
  }
  if (item.dietMatch) {
    chips.push(t('clientPlanEditor.templates.match.diet'));
  }
  if (item.mealsMatch) {
    chips.push(t('clientPlanEditor.templates.match.meals'));
  }
  if (item.kcal) {
    chips.push(
      item.kcal.kind === 'close'
        ? t('clientPlanEditor.templates.match.kcalClose')
        : t('clientPlanEditor.templates.match.kcalNear', { percent: formatPercent(item.kcal.percent) }),
    );
  }

  return (
    <Popover open={infoOpen} onOpenChange={setInfoOpen} modal={false}>
      <PopoverAnchor asChild>
        <li
          data-testid="template-card"
          className={cn(
            'flex flex-col gap-1.5 rounded-xl bg-card shadow-panel',
            best ? 'border-2 border-nutrition p-2.75' : 'border border-line p-3',
            infoOpen && !best && 'border-ink',
          )}
        >
          <button
            ref={infoButtonRef}
            type="button"
            aria-label={t('clientPlanEditor.templates.info.open', { name })}
            aria-expanded={infoOpen}
            onClick={() => setInfoOpen((open) => !open)}
            className="flex min-w-0 cursor-pointer flex-col gap-1.5 rounded-lg text-left outline-none focus-visible:ring-3 focus-visible:ring-ring/50"
          >
            <span className="flex min-w-0 items-center gap-2">
              <GripVertical className="size-3.5 shrink-0 text-faint" aria-hidden="true" />
              <span className="min-w-0 flex-1 truncate text-copy font-semibold text-ink">{name}</span>
              {best && (
                <span
                  data-testid="template-best-match"
                  className="shrink-0 rounded-full bg-nutrition-soft px-2 py-0.5 text-label font-semibold tracking-label text-nutrition-ink uppercase"
                >
                  {t('clientPlanEditor.templates.bestMatch')}
                </span>
              )}
            </span>
            <span className="flex min-w-0 items-center gap-1.5 text-meta text-muted-foreground">
              {template.goal && <GoalDot goal={template.goal} />}
              {goalLabel && <span className="shrink-0 font-semibold text-ink">{goalLabel}</span>}
              <span className="truncate">{meta.join(' · ')}</span>
            </span>
          </button>
          <div className="flex min-w-0 items-center justify-between gap-2">
            <span className="min-w-0 flex-1 text-meta text-muted-foreground">{chips.join(' · ')}</span>
            <Button
              type="button"
              variant="outline"
              size="sm"
              className="shrink-0"
              disabled={disabled || busy || !hasMeals}
              title={hasMeals ? undefined : t('clientPlanEditor.templates.noMeals')}
              aria-label={t('clientPlanEditor.templates.useNamed', { name })}
              onClick={() => onUse(item)}
            >
              {t('clientPlanEditor.templates.use')}
            </Button>
          </div>
        </li>
      </PopoverAnchor>
      {infoOpen && template.templateId && (
        <TemplateInfoPopover
          templateId={template.templateId}
          name={name}
          language={language}
          onClose={() => setInfoOpen(false)}
          onInteractOutside={(event) => {
            // A press on this card's own button is handled by its click (it toggles); don't dismiss first and reopen.
            if (event.target instanceof Node && infoButtonRef.current?.contains(event.target)) {
              event.preventDefault();
            }
          }}
          onCloseAutoFocus={(event) => {
            event.preventDefault();
            infoButtonRef.current?.focus({ preventScroll: true });
          }}
        />
      )}
    </Popover>
  );
}

/** The client plan editor's "Templates" library tab: templates matched to the client, each with Use. */
export default function TemplatesLibraryTab({
  context,
  weekStatus,
  onboarding,
  clientFirstName,
  onTemplateApplied,
  onBlocked,
}: Props) {
  const { t, i18n } = useTranslation();
  const queryClient = useQueryClient();
  const [pending, setPending] = useState<PendingApply | null>(null);
  // The library panel keeps the search and filters while another tab is shown; standalone, they stay local.
  const shared = useContext(TemplateTabStateContext);
  const [localSearch, setLocalSearch] = useState('');
  const [localFilters, setLocalFilters] = useState<TemplateFilterState>(EMPTY_TEMPLATE_FILTERS);
  const searchInput = shared?.searchInput ?? localSearch;
  const setSearchInput = shared?.setSearchInput ?? setLocalSearch;
  const filters = shared?.filters ?? localFilters;
  const setFilters = shared?.setFilters ?? setLocalFilters;
  const filterCount = countTemplateFilters(filters);
  const criteria = useMemo(
    () => criteriaFromOnboarding(onboarding, context.doc.targets.kcal),
    [onboarding, context.doc.targets.kcal],
  );
  const { goalQuery, allQuery } = useClientMatchedTemplates(criteria.goal, true);
  const load = useLoadPlanTemplate();
  const latest = useRef({ context, weekStatus });
  useEffect(() => {
    latest.current = { context, weekStatus };
  });

  const lists = useMemo(
    () => rankTemplates(goalQuery.data?.templates ?? [], allQuery.data?.templates ?? [], criteria),
    [goalQuery.data, allQuery.data, criteria],
  );
  const query = foldText(searchInput.trim());
  const visible = useMemo(() => {
    const filtered = filterRankedLists(lists, filters);
    if (query === '') {
      return filtered;
    }
    const matches = (item: RankedTemplate) => foldText(item.template.name ?? '').includes(query);
    return { matched: filtered.matched.filter(matches), others: filtered.others.filter(matches) };
  }, [lists, filters, query]);
  const isPending = allQuery.isPending || (criteria.goal !== undefined && goalQuery.isPending);
  const isError = allQuery.isError || (criteria.goal !== undefined && goalQuery.isError);
  const hasGoal = criteria.goal !== undefined;
  const loadingId = load.isPending ? load.variables : undefined;

  function apply(weeks: EditorWeek[], keepLength: boolean) {
    const { context: current, weekStatus: status } = latest.current;
    const published = publishedWeekNumbers(current, status);
    // One edit, computed from the document at that moment, so a single Undo takes it all back.
    current.onEdit((doc) => applyTemplate(doc, weeks, published, keepLength));
    onTemplateApplied?.();
    setPending(null);
  }

  function use(item: RankedTemplate) {
    const templateId = item.template.templateId;
    if (!templateId) {
      return;
    }
    if (onBlocked) {
      onBlocked();
      return;
    }
    load.mutate(templateId, {
      onSuccess: (detail) => {
        const weeks = toEditorWeeks(detail.weeks);
        if (!weeks.some(weekHasItems)) {
          showError('clientPlanEditor.templates.emptyTemplate');
          return;
        }
        const { context: current, weekStatus: status } = latest.current;
        if (current.readOnly) {
          return;
        }
        if (needsConfirmation(current.doc, publishedWeekNumbers(current, status))) {
          setPending({ name: detail.name ?? item.template.name ?? '', weeks });
        } else {
          apply(weeks, false);
        }
      },
      onError: (error) => {
        const status = getErrorStatus(error);
        if (status === 404 || status === 403) {
          showError('clientPlanEditor.templates.gone');
          void queryClient.invalidateQueries({ queryKey: ['plan-templates'] });
        } else {
          showApiError(error, 'clientPlanEditor.templates.loadError');
        }
      },
    });
  }

  function renderCard(item: RankedTemplate, best: boolean) {
    return (
      <TemplateCard
        key={item.template.templateId}
        item={item}
        best={best}
        hasGoalCriterion={hasGoal}
        busy={loadingId !== undefined}
        disabled={context.readOnly}
        language={i18n.language}
        onUse={use}
      />
    );
  }

  const criteriaParts = [
    criteria.goal ? t('clientPlanEditor.templates.criteria.goal', { goal: t(`clients.values.${criteria.goal}`, { defaultValue: criteria.goal }) }) : null,
    criteria.kcal ? t('clientPlanEditor.templates.criteria.kcal', { kcal: Math.round(criteria.kcal).toLocaleString(i18n.language) }) : null,
    criteria.dietaryStyle
      ? t('clientPlanEditor.templates.criteria.diet', { diet: t(`clients.values.${criteria.dietaryStyle}`, { defaultValue: criteria.dietaryStyle }) })
      : null,
  ].filter((part): part is string => part !== null);
  const isEmpty = visible.matched.length === 0 && visible.others.length === 0;

  return (
    <div className="flex min-h-0 flex-1 flex-col gap-3" data-testid="templates-tab">
      <Popover>
        <PopoverAnchor asChild>
          <div className="flex items-center gap-2">
            <div className="relative min-w-0 flex-1">
              <Search
                className="pointer-events-none absolute top-1/2 left-3 size-4 -translate-y-1/2 text-muted-foreground"
                aria-hidden="true"
              />
              <Input
                type="search"
                data-testid="templates-search"
                value={searchInput}
                onChange={(event) => setSearchInput(event.target.value)}
                placeholder={t('clientPlanEditor.templates.searchPlaceholder')}
                aria-label={t('clientPlanEditor.templates.searchPlaceholder')}
                className="h-9 pl-9"
              />
            </div>
            <LibraryFiltersTrigger count={filterCount} />
          </div>
        </PopoverAnchor>
        <TemplateFiltersContent
          filters={filters}
          onChange={setFilters}
          matchCount={isPending || isError ? undefined : visible.matched.length + visible.others.length}
        />
      </Popover>

      <p className="text-meta text-muted-foreground">
        {criteriaParts.length > 0
          ? t('clientPlanEditor.templates.introMatched', { name: clientFirstName, criteria: criteriaParts.join(', ') })
          : t('clientPlanEditor.templates.introAll')}
      </p>

      <div className="-mr-1 flex min-h-0 flex-1 flex-col gap-3 overflow-y-auto pr-1">
      {isPending && (
        <div className="flex flex-col gap-2">
          {Array.from({ length: 4 }, (_, index) => (
            <Skeleton key={index} className="h-20 w-full rounded-xl" />
          ))}
        </div>
      )}

      {isError && (
        <div className="flex flex-col items-start gap-2">
          <p className="text-body text-muted-foreground">{t('clientPlanEditor.templates.listError')}</p>
          <Button
            type="button"
            variant="outline"
            size="sm"
            onClick={() => {
              void allQuery.refetch();
              if (hasGoal) {
                void goalQuery.refetch();
              }
            }}
          >
            {t('planEditor.library.retry')}
          </Button>
        </div>
      )}

      {!isPending && !isError && isEmpty && (
        <div className="flex flex-col items-start gap-2" data-testid="templates-empty">
          <p className="text-body text-muted-foreground">
            {filterCount > 0
              ? t('clientPlanEditor.templates.filters.empty')
              : query === ''
                ? t('clientPlanEditor.templates.empty')
                : t('clientPlanEditor.templates.searchEmpty')}
          </p>
          {filterCount > 0 && (
            <Button
              type="button"
              variant="outline"
              size="sm"
              data-testid="templates-empty-clear"
              onClick={() => setFilters(EMPTY_TEMPLATE_FILTERS)}
            >
              {t('clientPlanEditor.templates.filters.clear')}
            </Button>
          )}
        </div>
      )}

      {!isPending && !isError && visible.matched.length > 0 && (
        <ul className="flex flex-col gap-2">{visible.matched.map((item, index) => renderCard(item, index === 0))}</ul>
      )}

      {!isPending && !isError && visible.others.length > 0 && (
        <section className="flex flex-col gap-2">
          {hasGoal && (
            <h3 className="text-label font-bold tracking-label text-muted-foreground uppercase">
              {t('clientPlanEditor.templates.otherGoals')}
            </h3>
          )}
          <ul className="flex flex-col gap-2">{visible.others.map((item) => renderCard(item, false))}</ul>
        </section>
      )}

      </div>

      <ReplaceWithTemplateDialog
        pending={pending ? { name: pending.name, weekCount: pending.weeks.length } : null}
        doc={context.doc}
        publishedWeeks={publishedWeekNumbers(context, weekStatus)}
        clientFirstName={clientFirstName}
        onConfirm={(keepLength) => pending && apply(pending.weeks, keepLength)}
        onCancel={() => setPending(null)}
      />
    </div>
  );
}
