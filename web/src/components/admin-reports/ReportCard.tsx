import type { ReactNode } from 'react';
import { cn } from '@/lib/utils';

export type ChipTone = 'good' | 'bad' | 'neutral';

const CHIP_TONE: Record<ChipTone, string> = {
  good: 'bg-success-soft text-success-ink',
  bad: 'bg-error-soft text-error',
  neutral: 'bg-sunken text-ink-2',
};

/** The change chip next to a figure. */
export function DeltaChip({ text, tone }: { text: string; tone: ChipTone }) {
  return (
    <span
      className={`inline-flex h-5 items-center rounded-full px-2 text-caption font-semibold whitespace-nowrap ${CHIP_TONE[tone]}`}
    >
      {text}
    </span>
  );
}

/** One figure line of a card: the value, optionally in the error colour, with an optional change chip. */
export interface FigureLine {
  key: string;
  value: string;
  chip?: { text: string; tone: ChipTone };
  danger?: boolean;
}

interface CardProps {
  label: string;
  /** One line per currency when the figure is money; a single line otherwise. */
  lines: readonly FigureLine[];
  note?: ReactNode;
}

/** A figure card: label, one big value line per currency, and a note underneath. */
export function ReportCard({ label, lines, note }: CardProps) {
  return (
    <div className="flex min-w-0 flex-col gap-1.5 rounded-2xl border border-line bg-card px-4.5 py-4 shadow-panel">
      <span className="text-body font-semibold text-ink-2">{label}</span>
      {lines.map((line) => (
        <span key={line.key} className="flex flex-wrap items-center gap-x-2.5 gap-y-1">
          <span className={`font-display text-stat font-semibold ${line.danger ? 'text-error' : 'text-ink'}`}>
            {line.value}
          </span>
          {line.chip && <DeltaChip text={line.chip.text} tone={line.chip.tone} />}
        </span>
      ))}
      {note && <span className="text-meta leading-snug text-muted-foreground">{note}</span>}
    </div>
  );
}

interface PanelProps {
  title: string;
  subtitle?: string;
  /** Right-hand slot of the header, e.g. a chart legend. */
  aside?: ReactNode;
  className?: string;
  children: ReactNode;
}

/** A titled panel card used by the chart, plans, funnel and plan-changes blocks. */
export function ReportPanel({ title, subtitle, aside, className, children }: PanelProps) {
  return (
    <section
      className={cn(
        'flex min-w-0 flex-col gap-3.5 rounded-2xl border border-line bg-card px-5 pt-4.5 pb-5 shadow-panel',
        className,
      )}
    >
      <div className="flex flex-wrap items-start gap-x-3 gap-y-2">
        <div className="flex flex-col gap-1">
          <h3 className="text-copy font-semibold text-ink">{title}</h3>
          {subtitle && <span className="text-meta text-muted-foreground">{subtitle}</span>}
        </div>
        {aside && <div className="ml-auto">{aside}</div>}
      </div>
      {children}
    </section>
  );
}

/** A section heading with its one-line description. */
export function SectionHeader({ title, subtitle }: { title: string; subtitle?: string }) {
  return (
    <div className="mt-1.5 flex flex-wrap items-baseline gap-x-3 gap-y-1">
      <h2 className="font-display text-card-title font-semibold text-ink">{title}</h2>
      {subtitle && <span className="text-meta text-muted-foreground">{subtitle}</span>}
    </div>
  );
}
