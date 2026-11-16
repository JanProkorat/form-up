import { useTranslation } from 'react-i18next';
import { ArrowRight } from 'lucide-react';
import { Button } from '@/components/ui/button';

interface Props {
  /** Plan name and monthly price, already translated. */
  title: string;
  /** Promotion and/or second-role line under the title, already translated. */
  note?: string;
  /** Short line next to the button, already translated. */
  hint: string;
  /** Optional second line under the hint, same style, already translated. */
  extraHint?: string;
  /** Confirm button text, already translated. */
  confirmLabel: string;
  busy: boolean;
  onConfirm: () => void;
  /**
   * Bleed past the padding of the scrolling main area (AppShell `p-6`): full width and flush with the viewport bottom.
   * Sticky offsets are measured from the content box, so the bottom offset is negative to cancel the padding.
   * The page column must also reach the bottom padding edge (`min-h-[calc(100%+--spacing(6))] -mb-6`).
   */
  bleed?: boolean;
  /** Extra classes for the placement on the page. */
  className?: string;
}

/** Sticky bottom bar of the plan screens: the selected plan on the left, the confirm button on the right. */
export default function PlanConfirmBar({ title, note, hint, extraHint, confirmLabel, busy, onConfirm, bleed = false, className = '' }: Props) {
  const { t } = useTranslation();

  return (
    <section
      aria-label={t('subscription.billing.select.region')}
      className={`sticky ${bleed ? '-bottom-6 -mx-6 mt-auto' : 'bottom-0'} z-10 flex flex-wrap items-center gap-x-4 gap-y-2 border-t border-line bg-card px-4 py-3 sm:px-8 ${className}`}
    >
      <div className="flex min-w-0 flex-col gap-0.75">
        <span className="text-subhead font-semibold text-ink">{title}</span>
        {note && <span className="text-meta text-muted-foreground">{note}</span>}
      </div>
      <div className="flex min-w-0 flex-col gap-0.75 text-body text-muted-foreground sm:ml-auto sm:text-right" aria-live="polite">
        <span>{hint}</span>
        {extraHint && <span>{extraHint}</span>}
      </div>
      <Button type="button" size="lg" className="h-11 gap-2 px-4.5 font-semibold" disabled={busy} onClick={onConfirm}>
        {confirmLabel}
        <ArrowRight aria-hidden="true" />
      </Button>
    </section>
  );
}
