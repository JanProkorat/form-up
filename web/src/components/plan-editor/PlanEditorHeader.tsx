import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Check, ChevronLeft, Loader2, Lock, Redo2, Undo2 } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { cn } from '@/lib/utils';
import type { EditorRange, EditorView, SaveStatus } from '@/components/plan-editor/plan-editor-types';

const MAX_NAME_LENGTH = 200;

interface SegmentedProps<T extends string> {
  compact: boolean;
  className?: string;
  label: string;
  options: readonly { value: T; label: string }[];
  value: T;
  onChange: (value: T) => void;
}

/** Two-option switch drawn like the board's toggles. */
function Segmented<T extends string>({ compact, className, label, options, value, onChange }: SegmentedProps<T>) {
  return (
    <div role="group" aria-label={label} className={cn('flex shrink-0 gap-1 rounded-xl bg-muted p-0.75', className)}>
      {options.map((option) => {
        const active = option.value === value;
        return (
          <button
            key={option.value}
            type="button"
            aria-pressed={active}
            onClick={() => onChange(option.value)}
            className={cn(
              'h-6 cursor-pointer rounded-lg text-xs font-semibold outline-none focus-visible:ring-3 focus-visible:ring-ring/50',
              compact ? 'px-2' : 'px-2.5',
              active ? 'bg-card text-ink shadow-selection-bar' : 'text-muted-foreground hover:text-ink',
            )}
          >
            {option.label}
          </button>
        );
      })}
    </div>
  );
}

interface Props {
  name: string;
  onNameChange: (name: string) => void;
  breadcrumbLabel: string;
  onBreadcrumb: () => void;
  /** Rendered after the back button, before the breadcrumb label, e.g. a client avatar. */
  breadcrumbLeading?: ReactNode;
  /** Rendered after the plan name, e.g. a status chip. */
  readOnly: boolean;
  dirty: boolean;
  saveStatus: SaveStatus;
  canUndo: boolean;
  canRedo: boolean;
  onUndo: () => void;
  onRedo: () => void;
  onSave: () => void;
  /** Replaces the Save button; `onRun` fires on click. */
  primaryAction?: { label: string; disabled: boolean; title?: string; busy: boolean; onRun: () => void };
  /** With a primary action, also shows the default Save button to its left. */
  secondarySave?: boolean;
  /** Save and the primary action stay enabled and show a lock: the host answers the click with a paywall. */
  locked?: boolean;
  /** Rendered last in the row, e.g. a plan actions menu. */
  actions?: ReactNode;
  range: EditorRange;
  view: EditorView;
  onRangeChange: (range: EditorRange) => void;
  onViewChange: (view: EditorView) => void;
}

/** Breadcrumb, editable title, save status, view toggles, undo and Save. */
export default function PlanEditorHeader({
  name,
  onNameChange,
  breadcrumbLabel,
  onBreadcrumb,
  breadcrumbLeading,
  readOnly,
  dirty,
  saveStatus,
  canUndo,
  canRedo,
  onUndo,
  onRedo,
  onSave,
  primaryAction,
  secondarySave,
  locked = false,
  actions,
  range,
  view,
  onRangeChange,
  onViewChange,
}: Props) {
  const { t } = useTranslation();
  const nameInvalid = name.trim() === '';
  const saving = saveStatus === 'saving';
  // With a primary action there are two more buttons than the template editor has, so the row is tighter.
  const compact = primaryAction !== undefined;
  const buttonPadding = compact ? 'px-2.5' : 'px-3';
  // Narrow: row 1 holds the left cluster and Publish, row 2 the other controls. Wide: DOM order, one row.
  const controlOrder = compact ? 'order-3 @min-[52rem]:order-0' : '';
  const publishOrder = compact ? 'order-2 @min-[52rem]:order-0' : '';

  return (
    <header className="@container border-b border-line">
    <div
      className={cn(
        'flex flex-wrap items-center gap-y-2 py-2.5',
        compact ? 'gap-x-1.5 px-4 @min-[52rem]:flex-nowrap' : 'gap-x-3 px-6',
      )}
    >
      <div
        className={cn(
          'flex items-center gap-2',
          // Narrow: shares row 1 with Publish. Wide: shares the one row, and the breadcrumb name gives way first.
          compact ? 'order-1 min-w-64 flex-1 @min-[52rem]:order-0 @min-[52rem]:min-w-0' : 'min-w-80 flex-1',
        )}
      >
        <Button
          type="button"
          variant="outline"
          className="size-7.5 shrink-0"
          aria-label={t('planEditor.back', { label: breadcrumbLabel })}
          title={t('planEditor.back', { label: breadcrumbLabel })}
          onClick={onBreadcrumb}
        >
          <ChevronLeft className="size-4" aria-hidden="true" />
        </Button>
        {breadcrumbLeading}
        <button
          type="button"
          onClick={onBreadcrumb}
          className={cn(
            'text-copy text-muted-foreground outline-none hover:text-ink focus-visible:ring-3 focus-visible:ring-ring/50',
            compact && 'max-w-28 min-w-0 truncate',
          )}
          title={breadcrumbLabel}
        >
          {breadcrumbLabel}
        </button>
        <span className="text-copy text-muted-foreground" aria-hidden="true">
          /
        </span>
        <h1 className="sr-only">{name}</h1>
        {readOnly ? (
          <span
            className="min-w-0 truncate font-display text-auth-title font-semibold text-ink"
            title={name}
            aria-hidden="true"
          >
            {name}
          </span>
        ) : (
          <Input
            value={name}
            maxLength={MAX_NAME_LENGTH}
            onChange={(event) => onNameChange(event.target.value)}
            aria-label={t('planEditor.nameLabel')}
            aria-invalid={nameInvalid}
            title={name}
            className={cn(
              'h-8 flex-1 truncate border-transparent bg-transparent px-2 font-display text-auth-title font-semibold text-ink shadow-none hover:border-line',
              compact ? 'min-w-26' : 'min-w-32',
            )}
          />
        )}
        {!readOnly && (
          <span
            role="status"
            data-testid="save-status"
            className="flex shrink-0 items-center gap-1.5 text-body text-muted-foreground"
          >
            {saving ? (
              <>
                <Loader2 className="size-4 animate-spin" aria-hidden="true" />
                {t('planEditor.status.saving')}
              </>
            ) : dirty ? (
              t('planEditor.status.unsaved')
            ) : (
              <>
                <Check className="size-4" aria-hidden="true" />
                {t('planEditor.status.saved')}
              </>
            )}
          </span>
        )}
      </div>

      <Segmented<EditorRange>
        compact={compact}
        className={controlOrder}
        label={t('planEditor.toggles.range')}
        value={range}
        onChange={onRangeChange}
        options={[
          { value: 'week', label: t('planEditor.toggles.week') },
          { value: 'day', label: t('planEditor.toggles.day') },
        ]}
      />
      <Segmented<EditorView>
        compact={compact}
        className={controlOrder}
        label={t('planEditor.toggles.view')}
        value={view}
        onChange={onViewChange}
        options={[
          { value: 'meals', label: t('planEditor.toggles.meals') },
          { value: 'nutrition', label: t('planEditor.toggles.nutrition') },
        ]}
      />
      {!readOnly && (
        <>
          <Button
            type="button"
            variant="outline"
            className={cn('size-7.5 shrink-0', controlOrder)}
            disabled={!canUndo}
            aria-label={t('planEditor.undo')}
            title={t('planEditor.undo')}
            onClick={onUndo}
          >
            <Undo2 className="size-4" aria-hidden="true" />
          </Button>
          <Button
            type="button"
            variant="outline"
            className={cn('size-7.5 shrink-0', controlOrder)}
            disabled={!canRedo}
            aria-label={t('planEditor.redo')}
            title={t('planEditor.redo')}
            onClick={onRedo}
          >
            <Redo2 className="size-4" aria-hidden="true" />
          </Button>
          {primaryAction ? (
            <>
              {secondarySave && (
                <Button
                  type="button"
                  variant="outline"
                  className={cn('h-7.5 shrink-0', buttonPadding, controlOrder)}
                  disabled={(!dirty && !locked) || nameInvalid || saving || primaryAction.busy}
                  onClick={onSave}
                >
                  {locked ? <Lock aria-hidden="true" /> : <Check aria-hidden="true" />}
                  {t('planEditor.save')}
                </Button>
              )}
              <Button
                type="button"
                className={cn('h-7.5 shrink-0', buttonPadding, publishOrder)}
                disabled={primaryAction.disabled || nameInvalid || saving || primaryAction.busy}
                title={primaryAction.title}
                onClick={primaryAction.onRun}
              >
                {primaryAction.busy ? (
                  <Loader2 className="animate-spin" aria-hidden="true" />
                ) : locked ? (
                  <Lock aria-hidden="true" />
                ) : (
                  <Check aria-hidden="true" />
                )}
                {primaryAction.label}
              </Button>
            </>
          ) : (
            <Button type="button" className="h-7.5 px-3" disabled={(!dirty && !locked) || nameInvalid || saving} onClick={onSave}>
              {locked ? <Lock aria-hidden="true" /> : <Check aria-hidden="true" />}
              {t('planEditor.save')}
            </Button>
          )}
        </>
      )}
      {actions && <div className={cn('shrink-0', publishOrder)}>{actions}</div>}
    </div>
    </header>
  );
}
