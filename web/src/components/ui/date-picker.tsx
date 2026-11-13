import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { DayPicker } from 'react-day-picker';
import { cs, de, enGB, type Locale } from 'date-fns/locale';
import { CalendarDays, ChevronLeft, ChevronRight, X } from 'lucide-react';
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover';
import { cn } from '@/lib/utils';

const LOCALES: Record<string, Locale> = { cs, de, en: enGB };

interface Props {
  /** Local calendar date as `yyyy-mm-dd`, or an empty string. */
  value: string;
  onChange: (value: string) => void;
  /** Earliest selectable date, `yyyy-mm-dd`. */
  min?: string;
  disabled?: boolean;
  id?: string;
  className?: string;
  'aria-describedby'?: string;
  'aria-invalid'?: boolean;
}

function parseIsoDate(value: string | undefined): Date | undefined {
  const match = value ? /^(\d{4})-(\d{2})-(\d{2})$/.exec(value) : null;
  return match ? new Date(Number(match[1]), Number(match[2]) - 1, Number(match[3])) : undefined;
}

function toIsoDate(date: Date): string {
  const pad = (part: number) => String(part).padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

/** Date field that opens a month calendar in a popover; holds the same `yyyy-mm-dd` string as a native date input. */
export function DatePicker({ value, onChange, min, disabled, id, className, ...aria }: Props) {
  const { t, i18n } = useTranslation();
  const [open, setOpen] = useState(false);
  const selected = parseIsoDate(value);
  const minDate = parseIsoDate(min);
  const language = i18n.language.slice(0, 2);
  const locale = LOCALES[language] ?? enGB;

  const canClear = Boolean(selected) && !disabled;

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <div className={cn('relative w-full', className)}>
        <PopoverTrigger asChild>
          <button
            id={id}
            type="button"
            disabled={disabled}
            {...aria}
            className={cn(
              'flex h-10 w-full cursor-pointer items-center gap-2 rounded-field border border-input bg-background px-3 text-left text-body text-foreground outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 disabled:cursor-not-allowed disabled:opacity-50 aria-invalid:border-destructive',
              canClear && 'pr-17',
            )}
          >
            <span className={cn('min-w-0 flex-1 truncate', !selected && 'text-muted-foreground')}>
              {selected
                ? new Intl.DateTimeFormat(i18n.language, { day: 'numeric', month: 'long', year: 'numeric' }).format(selected)
                : t('common.datePicker.placeholder')}
            </span>
            <CalendarDays
              className={cn('size-4 shrink-0 text-muted-foreground', canClear && 'absolute right-3')}
              aria-hidden="true"
            />
          </button>
        </PopoverTrigger>
        {/* Sibling of the trigger, not inside it: a button may not contain another button. */}
        {canClear && (
          <button
            type="button"
            aria-label={t('common.datePicker.clear')}
            title={t('common.datePicker.clear')}
            onClick={() => onChange('')}
            className="absolute top-1/2 right-9 flex size-6 -translate-y-1/2 cursor-pointer items-center justify-center rounded-full text-muted-foreground outline-none hover:bg-muted hover:text-ink focus-visible:ring-3 focus-visible:ring-ring/50"
          >
            <X className="size-3.5" aria-hidden="true" />
          </button>
        )}
      </div>
      <PopoverContent align="start" sideOffset={6} className="w-auto rounded-xl border-line bg-card p-3">
        <DayPicker
          mode="single"
          locale={locale}
          weekStartsOn={1}
          selected={selected}
          defaultMonth={selected ?? minDate}
          startMonth={minDate}
          disabled={minDate ? { before: minDate } : undefined}
          onSelect={(date) => {
            if (date) {
              onChange(toIsoDate(date));
              setOpen(false);
            }
          }}
          showOutsideDays
          autoFocus
          labels={{
            labelPrevious: () => t('common.datePicker.previousMonth'),
            labelNext: () => t('common.datePicker.nextMonth'),
          }}
          components={{
            Chevron: ({ orientation }) =>
              orientation === 'left' ? <ChevronLeft className="size-4" /> : <ChevronRight className="size-4" />,
          }}
          classNames={{
            root: 'relative',
            months: 'flex flex-col',
            month: 'flex flex-col gap-3',
            month_caption: 'flex h-8 items-center px-1',
            caption_label: 'text-body font-semibold text-ink first-letter:uppercase',
            nav: 'absolute top-0 right-0 flex items-center gap-1',
            button_previous:
              'flex size-8 cursor-pointer items-center justify-center rounded-full text-ink-2 outline-none hover:bg-muted focus-visible:ring-3 focus-visible:ring-ring/50 disabled:pointer-events-none disabled:opacity-40',
            button_next:
              'flex size-8 cursor-pointer items-center justify-center rounded-full text-ink-2 outline-none hover:bg-muted focus-visible:ring-3 focus-visible:ring-ring/50 disabled:pointer-events-none disabled:opacity-40',
            month_grid: 'border-collapse',
            weekdays: 'flex',
            weekday: 'flex size-9 items-center justify-center text-meta font-medium text-muted-foreground',
            week: 'mt-1 flex',
            day: 'size-9 p-0 text-center',
            day_button:
              'flex size-9 cursor-pointer items-center justify-center rounded-full text-body text-ink outline-none transition-colors hover:bg-muted focus-visible:ring-3 focus-visible:ring-ring/50',
            selected: '[&>button]:bg-primary [&>button]:font-semibold [&>button]:text-primary-foreground [&>button]:hover:bg-primary',
            today: '[&>button]:font-semibold [&>button]:ring-1 [&>button]:ring-line',
            outside: '[&>button]:text-faint',
            disabled: '[&>button]:cursor-not-allowed [&>button]:text-faint [&>button]:opacity-50 [&>button]:hover:bg-transparent',
          }}
        />
      </PopoverContent>
    </Popover>
  );
}
