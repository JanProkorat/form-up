import type { ReactNode } from 'react';
import { cn } from '@/lib/utils';

interface Props {
  /** Optional DOM id, e.g. a stable hook for e2e scoping or an anchor link. */
  id?: string;
  title: string;
  description: string;
  /** Right-aligned control on the header row. */
  action?: ReactNode;
  danger?: boolean;
  children: ReactNode;
}

/** Card with a title, one-line description and optional header action wrapping one settings group. */
export default function SettingsCard({ id, title, description, action, danger, children }: Props) {
  return (
    <section
      id={id}
      className={cn(
        'flex flex-col gap-4.5 rounded-lg border border-border bg-surface px-6 pt-5.5 pb-6 shadow-panel',
        danger && 'border-error/30',
      )}
    >
      <header className="flex items-start gap-4">
        <div className="flex min-w-0 flex-col gap-1">
          <h2 className={cn('text-panel-title font-semibold text-ink', danger && 'text-error')}>{title}</h2>
          <p className="text-body text-muted-foreground">{description}</p>
        </div>
        {action && <div className="ml-auto shrink-0">{action}</div>}
      </header>
      {children}
    </section>
  );
}
