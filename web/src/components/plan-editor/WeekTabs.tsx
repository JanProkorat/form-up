import { useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { ChevronLeft, ChevronRight, Lock, Plus, X } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';
import ConflictBadge from '@/components/plan-editor/sync-conflicts/ConflictBadge';
import { useSyncConflicts } from '@/components/plan-editor/sync-conflicts/SyncConflictsContext';
import { MAX_WEEKS, type WeekPillStatus } from '@/components/plan-editor/plan-editor-types';

const SCROLL_STEP_PX = 240;
const DOT_CLASS = 'size-1.75 shrink-0 rounded-full';

const DOT_STATUS_CLASS: Record<WeekPillStatus, string> = {
  published: 'bg-nutrition',
  draft: 'border-[1.5px] border-training',
  empty: 'border-[1.5px] border-dashed border-faint',
};

/** Published is a solid dot, draft a ring, empty a dashed ring; the same in the legend and on the pills. */
function StatusDot({ status, className }: { status: WeekPillStatus; className?: string }) {
  return <span aria-hidden="true" className={cn(DOT_CLASS, DOT_STATUS_CLASS[status], className)} />;
}

interface Props {
  weekCount: number;
  current: number;
  readOnly: boolean;
  /** Publication state per week; omit for no dots and no legend. Return undefined for a week with no status. */
  weekStatus?: (weekIndex: number) => WeekPillStatus | undefined;
  onSelect: (weekIndex: number) => void;
  onAddWeek: () => void;
  /** Shows a lock on the add-week button: the host answers the click with a paywall. */
  addLocked?: boolean;
  /**
   * Shows a remove button on the last week while it is not published and another week remains: on hover,
   * and on keyboard focus of the chip or the button. Omit to never offer removal.
   */
  onRemoveLast?: () => void;
}

/** Scrollable row of week pills with prev/next arrows and an add-week button. */
export default function WeekTabs({ weekCount, current, readOnly, weekStatus, onSelect, onAddWeek, addLocked = false, onRemoveLast }: Props) {
  const { t } = useTranslation();
  const conflicts = useSyncConflicts();
  const statuses = weekStatus ? Array.from({ length: weekCount }, (_, index) => weekStatus(index)) : [];
  const published = statuses.filter((status) => status === 'published').length;
  const draft = statuses.filter((status) => status === 'draft').length;
  const empty = statuses.filter((status) => status === 'empty').length;
  const listRef = useRef<HTMLDivElement>(null);
  const lastIndex = weekCount - 1;
  const lastStatus = statuses[lastIndex];
  const removable = onRemoveLast !== undefined && !readOnly && weekCount > 1 && lastStatus !== undefined && lastStatus !== 'published';

  useEffect(() => {
    listRef.current
      ?.querySelector<HTMLElement>('[aria-selected="true"]')
      ?.scrollIntoView({ block: 'nearest', inline: 'nearest' });
  }, [current]);

  function scrollBy(direction: -1 | 1) {
    listRef.current?.scrollBy({ left: direction * SCROLL_STEP_PX, behavior: 'smooth' });
  }

  return (
    <div className="flex items-center gap-2">
      <Button
        type="button"
        variant="outline"
        size="icon-sm"
        className="shrink-0 rounded-full"
        aria-label={t('planEditor.weeks.previous')}
        onClick={() => scrollBy(-1)}
      >
        <ChevronLeft aria-hidden="true" />
      </Button>
      <div ref={listRef} className="group/weeks flex min-w-0 flex-1 overflow-x-auto [scrollbar-width:none]">
        <div role="tablist" aria-label={t('planEditor.weeks.label')} className="flex shrink-0 gap-2">
          {Array.from({ length: weekCount }, (_, index) => {
            const status = statuses[index];
            const isRemovable = removable && index === lastIndex;
            return (
              <button
                key={index}
                type="button"
                role="tab"
                aria-selected={index === current}
                data-last-week={isRemovable ? '' : undefined}
                onClick={() => onSelect(index)}
                className={cn(
                  'inline-flex shrink-0 items-center gap-1.5 rounded-full border px-2.5 text-xs font-semibold whitespace-nowrap outline-none transition-colors focus-visible:ring-3 focus-visible:ring-ring/50',
                  'h-7',
                  isRemovable && 'pr-7',
                  index === current
                    ? 'border-ink bg-ink text-primary-foreground'
                    : 'border-line bg-card text-ink hover:bg-muted',
                )}
              >
                {status && <StatusDot status={status} />}
                {t('planEditor.weeks.week', { number: index + 1 })}
                {status && <span className="sr-only">{t(`planEditor.weeks.status_${status}`)}</span>}
                {conflicts && conflicts.countInWeek(index) > 0 && <ConflictBadge />}
              </button>
            );
          })}
        </div>
        {removable && (
          <span className="relative w-0 shrink-0 self-center">
            <button
              type="button"
              aria-label={t('planEditor.weeks.remove', { number: weekCount })}
              title={t('planEditor.weeks.remove', { number: weekCount })}
              onClick={onRemoveLast}
              className={cn(
                'absolute top-1/2 right-1 inline-flex size-5 -translate-y-1/2 items-center justify-center rounded-full outline-none',
                'pointer-events-none opacity-0 transition-opacity hover:pointer-events-auto hover:opacity-100 focus-visible:pointer-events-auto focus-visible:opacity-100 focus-visible:ring-3 focus-visible:ring-ring/50',
                'group-has-[[data-last-week]:hover]/weeks:pointer-events-auto group-has-[[data-last-week]:hover]/weeks:opacity-100',
                'group-has-[[data-last-week]:focus-visible]/weeks:pointer-events-auto group-has-[[data-last-week]:focus-visible]/weeks:opacity-100',
                current === lastIndex
                  ? 'text-primary-foreground hover:bg-primary-foreground/20'
                  : 'text-muted-foreground hover:bg-line hover:text-ink',
              )}
            >
              <X className="size-3" aria-hidden="true" />
            </button>
          </span>
        )}
      </div>
      <Button
        type="button"
        variant="outline"
        size="icon-sm"
        className="shrink-0 rounded-full"
        aria-label={t('planEditor.weeks.next')}
        onClick={() => scrollBy(1)}
      >
        <ChevronRight aria-hidden="true" />
      </Button>
      {!readOnly && (
        <Button
          type="button"
          variant="outline"
          size="icon-sm"
          className="shrink-0 rounded-full border-dashed"
          aria-label={t('planEditor.weeks.add')}
          disabled={weekCount >= MAX_WEEKS}
          onClick={onAddWeek}
        >
          {addLocked ? <Lock aria-hidden="true" /> : <Plus aria-hidden="true" />}
        </Button>
      )}
      {weekStatus && weekCount > 0 && (
        <div className="flex shrink-0 items-center gap-3 text-meta whitespace-nowrap text-muted-foreground">
          {empty > 0 && (
            <span className="inline-flex items-center gap-1.5">
              <StatusDot status="empty" />
              {t('planEditor.weeks.legendEmpty', { count: empty })}
            </span>
          )}
          {published > 0 ? (
            <span className="inline-flex items-center gap-1.5">
              <StatusDot status="published" />
              {t('planEditor.weeks.legendPublished', { count: published })}
            </span>
          ) : (
            <span className="inline-flex items-center gap-1.5">
              {empty > 0 && <span aria-hidden="true">·</span>}
              {t('planEditor.weeks.legendNothingPublished')}
            </span>
          )}
          {draft > 0 && (
            <span className="inline-flex items-center gap-1.5">
              <StatusDot status="draft" />
              {t('planEditor.weeks.legendDraft', { count: draft })}
            </span>
          )}
        </div>
      )}
    </div>
  );
}
