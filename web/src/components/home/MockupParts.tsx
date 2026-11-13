// Pixel sizes and positions in this file are illustration drawing copied from the board, not layout tokens.
import type { CSSProperties, ReactNode } from 'react';
import '@/components/home/home-animations.css';
import { IconApple, IconCheck, IconPlay } from '@/components/home/HomeIcons';
import { useInView } from '@/components/home/useInView';

/** Wrapper for anything that animates: its loops and entrances pause while it is off screen. */
export function Loop({ className = '', children }: { className?: string; children: ReactNode }) {
  const [ref, inView] = useInView<HTMLDivElement>();
  return (
    <div ref={ref} data-in-view={inView} className={`hm-loop ${className}`}>
      {children}
    </div>
  );
}

type CellTone = 'filled' | 'outline' | 'accent';

const CELL_TONE: Record<CellTone, string> = {
  filled: 'bg-sunken',
  outline: 'border border-border bg-transparent',
  accent: 'bg-training-soft',
};

interface CellProps {
  name: string;
  kcal: string;
  tone?: CellTone;
  className?: string;
  style?: CSSProperties;
}

/** Meal tile of the plan grids: name and energy. */
export function Cell({ name, kcal, tone = 'filled', className = '', style }: CellProps) {
  return (
    <div
      style={style}
      className={`box-border flex min-w-0 flex-col gap-[3px] rounded-field px-2.5 py-[9px] ${CELL_TONE[tone]} ${className}`}
    >
      <span className="truncate text-meta font-semibold text-ink">{name}</span>
      <span className="text-caption text-muted-foreground">{kcal}</span>
    </div>
  );
}

/** Row or column label of a plan grid. */
export function GridLabel({ children, className = '' }: { children?: ReactNode; className?: string }) {
  return (
    <span className={`self-center text-caption font-semibold text-muted-foreground ${className}`}>
      {children}
    </span>
  );
}

interface RingProps {
  /** Shows the green tick. */
  ticked?: boolean;
  /** Animation class of the tick (keeps its plain style = ticked). */
  tickClassName?: string;
  tickStyle?: CSSProperties;
}

/** Round checkbox of a meal or set row. */
export function Ring({ ticked = false, tickClassName = '', tickStyle }: RingProps) {
  return (
    <span className="relative box-border size-5 shrink-0 rounded-full border-2 border-border">
      {ticked && (
        <span
          style={tickStyle}
          className={`absolute -inset-0.5 flex items-center justify-center rounded-full bg-nutrition text-on-nutrition ${tickClassName}`}
        >
          <IconCheck size={11} strokeWidth={3.4} />
        </span>
      )}
    </span>
  );
}

type RowTone = 'subtle' | 'accent' | 'dark' | 'active';

const ROW_TONE: Record<RowTone, string> = {
  subtle: 'bg-sunken',
  accent: 'bg-training-soft',
  dark: 'bg-primary text-primary-foreground',
  active: 'border-[1.5px] border-marker bg-surface',
};

/** One list row inside a mockup card or phone. */
export function Row({
  tone = 'subtle',
  className = '',
  children,
}: {
  tone?: RowTone;
  className?: string;
  children: ReactNode;
}) {
  return (
    <div
      className={`box-border flex items-center gap-2.5 rounded-xl px-3 py-[11px] ${ROW_TONE[tone]} ${className}`}
    >
      {children}
    </div>
  );
}

/** Two-line text block of a row: title and a small line, which sits above the title with `subFirst`. */
export function RowText({ title, sub, subFirst = false }: { title: string; sub: string; subFirst?: boolean }) {
  return (
    <span className={`flex min-w-0 flex-1 gap-0.5 ${subFirst ? 'flex-col-reverse' : 'flex-col'}`}>
      <span className="text-body font-semibold text-ink">{title}</span>
      <span className="text-caption text-muted-foreground">{sub}</span>
    </span>
  );
}

interface PhoneShellProps {
  /** `hero` is the 260 x 540 device of the hero collage, `stage` the 250 x 510 one. */
  size?: 'hero' | 'stage';
  className?: string;
  children: ReactNode;
}

/** Decorative phone body; the screen content goes in `children`. */
export function PhoneShell({ size = 'stage', className = '', children }: PhoneShellProps) {
  const dimensions = size === 'hero' ? 'h-[540px] w-[260px]' : 'h-[510px] w-[250px]';
  const screenTop = size === 'hero' ? 'pt-[46px]' : 'pt-[42px]';
  return (
    <div
      className={`box-border shrink-0 rounded-[42px] bg-phone-bezel p-2.5 shadow-card ${dimensions} ${className}`}
    >
      <div
        className={`relative box-border flex h-full flex-col gap-2 overflow-hidden rounded-[33px] bg-surface px-3.5 pb-3.5 ${screenTop}`}
      >
        {children}
      </div>
    </div>
  );
}

interface PointItemProps {
  icon: ReactNode;
  /** Tailwind classes for the icon tile background and glyph colour. */
  tone: string;
  children: ReactNode;
}

/** Icon tile + one short point in a section's list. */
export function PointItem({ icon, tone, children }: PointItemProps) {
  return (
    <span className="flex items-center gap-3.5 text-home-feature-title font-semibold text-ink">
      <span
        className={`flex size-9.5 @min-[1340px]:size-10 @min-[1472px]:size-10.5 @min-[1592px]:size-11.5 @min-[1760px]:size-12.5 @min-[1940px]:size-14 shrink-0 items-center justify-center rounded-xl ${tone}`}
      >
        {icon}
      </span>
      {children}
    </span>
  );
}

interface StoreLinksProps {
  appStore: string;
  googlePlay: string;
  size?: 'md' | 'sm';
  /** `solid` is the dark primary button of the mobile section. */
  tone?: 'outline' | 'solid';
}

/** App Store / Google Play placeholders; real store URLs land with the app release. */
export function StoreLinks({ appStore, googlePlay, size = 'md', tone = 'outline' }: StoreLinksProps) {
  const sizing =
    size === 'md'
      ? 'h-10 @min-[1340px]:h-10.5 @min-[1472px]:h-11 @min-[1592px]:h-12 @min-[1760px]:h-13 @min-[1940px]:h-14.5 px-3.5 text-home-body'
      : 'h-8.5 px-3.5 text-body';
  const look =
    tone === 'solid'
      ? 'rounded-xl bg-primary font-bold text-primary-foreground hover:opacity-90'
      : 'rounded-field border border-border bg-surface font-semibold text-ink hover:bg-sunken';
  const base = `inline-flex items-center gap-1.75 whitespace-nowrap ${look} ${sizing}`;
  return (
    <>
      <a href="#" className={base}>
        <IconApple size={16} />
        {appStore}
      </a>
      <a href="#" className={base}>
        <IconPlay size={16} />
        {googlePlay}
      </a>
    </>
  );
}
