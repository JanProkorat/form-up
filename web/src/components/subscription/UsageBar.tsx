import { cn } from '@/lib/utils';

interface Props {
  used: number;
  /** Null means unlimited: the bar stays empty. */
  limit: number | null;
  /** Colour of the unfilled track. */
  trackClassName?: string;
  /** Height and radius of the bar. */
  sizeClassName?: string;
  /** Forces the failure colour, e.g. for a payment problem. */
  danger?: boolean;
  className?: string;
}

/** Progress bar of active clients against the plan's cap; turns red when the cap is full. */
export default function UsageBar({
  used,
  limit,
  trackClassName = 'bg-sunken',
  sizeClassName = 'h-2 rounded-full',
  danger = false,
  className,
}: Props) {
  const percent = limit && limit > 0 ? Math.min(100, Math.round((used / limit) * 100)) : 0;
  const full = limit !== null && limit > 0 && used >= limit;

  return (
    <span aria-hidden="true" className={cn('block overflow-hidden', sizeClassName, trackClassName, className)}>
      <span
        className={cn('block h-full rounded-[inherit]', danger || full ? 'bg-error' : 'bg-training-bright')}
        style={{ width: `${percent}%` }}
      />
    </span>
  );
}
