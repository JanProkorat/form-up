import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { ChevronLeft, ChevronRight } from 'lucide-react';
import AdminPlanCard from '@/components/admin-coaches/AdminPlanCard';
import type { GiveFreeOption } from '@/components/admin-coaches/giveFreePlans';
import { cn } from '@/lib/utils';

interface Props {
  options: GiveFreeOption[];
  value: string;
  onChange: (code: string) => void;
  /** Radio group name. */
  name: string;
  /** Shown under the cards when no plan is chosen yet. */
  error?: string;
  /** Code of the coach's current plan; its card is highlighted and cannot be picked. */
  currentCode?: string;
}

const EDGE_TOLERANCE_PX = 4;

interface View {
  /** 1-based index of the first fully visible card. */
  from: number;
  /** 1-based index of the last fully visible card. */
  to: number;
  canPrev: boolean;
  canNext: boolean;
}

/** Plans as radio cards in one horizontally scrolling, snapping row, with arrows, a counter and a right-edge fade. */
export default function PlanCarousel({ options, value, onChange, name, error, currentCode }: Props) {
  const { t } = useTranslation();
  const scroller = useRef<HTMLDivElement>(null);
  const [view, setView] = useState<View>({ from: 1, to: Math.min(options.length, 1), canPrev: false, canNext: false });

  const measure = useCallback(() => {
    const el = scroller.current;
    if (!el) {
      return;
    }
    const box = el.getBoundingClientRect();
    const cards = Array.from(el.querySelectorAll<HTMLElement>('[data-plan-card]'));
    const visible = cards
      .map((card, index) => ({ index, rect: card.getBoundingClientRect() }))
      .filter(
        ({ rect }) => rect.left >= box.left - EDGE_TOLERANCE_PX && rect.right <= box.right + EDGE_TOLERANCE_PX,
      );
    const first = visible.length > 0 ? visible[0].index : 0;
    const last = visible.length > 0 ? visible[visible.length - 1].index : 0;
    const next: View = {
      from: first + 1,
      to: last + 1,
      canPrev: el.scrollLeft > EDGE_TOLERANCE_PX,
      canNext: el.scrollLeft + el.clientWidth < el.scrollWidth - EDGE_TOLERANCE_PX,
    };
    setView((current) =>
      current.from === next.from &&
      current.to === next.to &&
      current.canPrev === next.canPrev &&
      current.canNext === next.canNext
        ? current
        : next,
    );
  }, []);

  useEffect(() => {
    measure();
    const el = scroller.current;
    if (!el || typeof ResizeObserver === 'undefined') {
      return;
    }
    const observer = new ResizeObserver(measure);
    observer.observe(el);
    return () => observer.disconnect();
  }, [measure, options.length]);

  const page = (direction: 1 | -1) => {
    const el = scroller.current;
    if (!el) {
      return;
    }
    const reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    el.scrollBy({ left: direction * el.clientWidth * 0.8, behavior: reduced ? 'auto' : 'smooth' });
  };

  return (
    <fieldset className="m-0 flex min-w-0 flex-col gap-2.5 border-none p-0">
      {/* A legend renders outside the fieldset's content box, so the visible label shares a normal row with the arrows. */}
      <legend className="sr-only">{t('adminCoaches.giveFree.plan')}</legend>
      <div className="flex items-center justify-between gap-3">
        <span aria-hidden="true" className="text-meta font-semibold text-ink-2">
          {t('adminCoaches.giveFree.plan')}
        </span>
        <div className="flex items-center gap-2">
          <span className="text-meta text-muted-foreground" aria-live="polite">
            {t('adminCoaches.giveFree.counter', { from: view.from, to: view.to, total: options.length })}
          </span>
          <CarouselArrow label={t('adminCoaches.giveFree.previous')} disabled={!view.canPrev} onClick={() => page(-1)}>
            <ChevronLeft className="size-4" />
          </CarouselArrow>
          <CarouselArrow label={t('adminCoaches.giveFree.next')} disabled={!view.canNext} onClick={() => page(1)}>
            <ChevronRight className="size-4" />
          </CarouselArrow>
        </div>
      </div>
      <div className="relative">
        <div
          ref={scroller}
          onScroll={measure}
          className="-mx-0.5 -mt-1 -mb-3.5 flex snap-x snap-mandatory gap-3 overflow-x-auto px-0.5 pt-1 pb-5"
        >
          {options.map(({ plan, color }) => (
            <AdminPlanCard
              key={plan.code}
              plan={plan}
              color={color}
              selected={plan.code === value}
              current={currentCode !== undefined && plan.code === currentCode}
              name={name}
              onSelect={onChange}
            />
          ))}
        </div>
        <span
          aria-hidden="true"
          className={cn(
            'pointer-events-none absolute -right-0.5 bottom-1.5 top-0 w-18 bg-linear-to-r from-transparent to-card to-85% transition-opacity',
            view.canNext ? 'opacity-100' : 'opacity-0',
          )}
        />
      </div>
      {error && (
        <p role="alert" className="text-meta text-error">
          {error}
        </p>
      )}
    </fieldset>
  );
}

function CarouselArrow({
  label,
  disabled,
  onClick,
  children,
}: {
  label: string;
  disabled: boolean;
  onClick: () => void;
  children: ReactNode;
}) {
  return (
    <button
      type="button"
      aria-label={label}
      disabled={disabled}
      onClick={onClick}
      className="flex size-8 items-center justify-center rounded-full border border-line bg-card text-ink-2 outline-none hover:bg-muted focus-visible:ring-3 focus-visible:ring-ring/50 disabled:pointer-events-none disabled:opacity-50"
    >
      {children}
    </button>
  );
}
