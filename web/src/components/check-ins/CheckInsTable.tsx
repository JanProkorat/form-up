import { Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { Check, Clock, MessageSquare } from 'lucide-react';
import type { ReactNode } from 'react';
import type { TrainerCheckInDto } from '@/api/weekly-checkins';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import {
  clientInitials,
  firstNameOf,
  formatDateTime,
  formatDay,
  toCheckInState,
  type CheckInState,
} from '@/components/check-ins/checkInsModel';

const SKELETON_ROW_COUNT = 4;
const COLUMN_COUNT = 6;
const HEAD_CLASS = 'px-4 py-3.5 text-meta font-semibold';
const CELL_CLASS = 'px-4 py-2.75';

interface Props {
  rows: TrainerCheckInDto[];
  emptyText: string;
  isPending: boolean;
  isError: boolean;
  onRetry: () => void;
  /** Id of the check-in whose Mark reviewed request is in flight. */
  markingId: string | null;
  onMarkReviewed: (id: string) => void;
}

function StatusBadge({ state }: { state: CheckInState }) {
  const { t } = useTranslation();
  const label = t(`checkIns.status.${state}`);
  switch (state) {
    case 'notReviewed':
      return (
        <span className="inline-flex items-center gap-1.5 text-meta font-bold text-error">
          <span className="size-1.75 rounded-full bg-error" aria-hidden="true" />
          {label}
        </span>
      );
    case 'missed':
      return (
        <span className="inline-flex h-6 items-center rounded-full bg-error-soft px-2.25 text-meta font-semibold text-error">
          {label}
        </span>
      );
    case 'reviewed':
      return (
        <span className="inline-flex h-6 items-center gap-1.25 rounded-full bg-success-soft px-2.25 text-meta font-semibold text-success-ink">
          <Check className="size-3.25" aria-hidden="true" />
          {label}
        </span>
      );
    default:
      return (
        <span className="inline-flex items-center gap-1.25 text-meta font-semibold text-muted-foreground">
          <Clock className="size-3.25" aria-hidden="true" />
          {label}
        </span>
      );
  }
}

function CheckInRow({
  row,
  markingId,
  onMarkReviewed,
}: {
  row: TrainerCheckInDto;
  markingId: string | null;
  onMarkReviewed: (id: string) => void;
}) {
  const { t, i18n } = useTranslation();
  const locale = i18n.language;
  const zone = row.clientTimeZone;
  const state = toCheckInState(row.status);
  const id = row.id ?? '';
  const name = row.clientName ?? '';
  const clientHref = `/clients/${row.clientPublicId ?? ''}`;

  const sent = formatDateTime(row.sentAt, zone, locale);
  const answered = formatDateTime(row.respondedAt, zone, locale);
  let subline: ReactNode = null;
  if (state === 'waiting') {
    const date = formatDay(row.dueAt, zone, locale);
    subline = date ? t('checkIns.row.canAnswerUntil', { date }) : null;
  } else if (state === 'missed') {
    const date = formatDay(row.dueAt, zone, locale);
    subline = date ? t('checkIns.row.windowEnded', { date }) : null;
  } else if (state === 'reviewed') {
    const date = formatDay(row.reviewedByTrainerAt, zone, locale);
    subline = date ? t('checkIns.row.reviewedOn', { date }) : null;
  }

  const hasAnswers = state === 'notReviewed' || state === 'reviewed';

  return (
    <TableRow className="border-line">
      <TableCell className={CELL_CLASS}>
        <div className="flex items-center gap-3">
          <span
            className="flex size-9 shrink-0 items-center justify-center rounded-full bg-sunken text-meta font-semibold text-ink-2"
            aria-hidden="true"
          >
            {clientInitials(name)}
          </span>
          <Link
            to={clientHref}
            className="text-copy font-semibold text-ink outline-none hover:underline focus-visible:ring-3 focus-visible:ring-ring/50"
          >
            {name}
          </Link>
        </div>
      </TableCell>
      <TableCell className={`${CELL_CLASS} text-body text-ink-2`}>{row.formName}</TableCell>
      <TableCell className={`${CELL_CLASS} text-body text-ink-2`}>{sent}</TableCell>
      <TableCell className={`${CELL_CLASS} text-body text-ink-2`}>
        {answered ?? <span className="text-muted-foreground">{t('checkIns.row.noAnswer')}</span>}
      </TableCell>
      <TableCell className={CELL_CLASS}>
        <div className="flex flex-col items-start gap-1">
          <StatusBadge state={state} />
          {subline && <span className="text-meta text-muted-foreground">{subline}</span>}
        </div>
      </TableCell>
      <TableCell className={CELL_CLASS}>
        <div className="flex justify-end gap-2">
          {hasAnswers && (
            <Button asChild variant="outline" size="lg" className="px-3 font-semibold">
              <Link to={`${clientHref}/forms?form=${id}`}>{t('checkIns.actions.openAnswers')}</Link>
            </Button>
          )}
          {state === 'notReviewed' && (
            <Button
              type="button"
              size="lg"
              className="gap-1.5 px-3 font-semibold"
              disabled={markingId === id}
              onClick={() => onMarkReviewed(id)}
            >
              <Check aria-hidden="true" />
              {t('checkIns.actions.markReviewed')}
            </Button>
          )}
          {state === 'missed' && (
            <Button asChild variant="outline" size="lg" className="gap-1.5 px-3 font-semibold">
              <Link to={`/inbox?client=${row.clientPublicId ?? ''}`}>
                <MessageSquare aria-hidden="true" />
                {t('checkIns.actions.writeTo', { name: firstNameOf(name) })}
              </Link>
            </Button>
          )}
        </div>
      </TableCell>
    </TableRow>
  );
}

/** The week's check-ins as one row per client, with the actions each state allows. */
export default function CheckInsTable({ rows, emptyText, isPending, isError, onRetry, markingId, onMarkReviewed }: Props) {
  const { t } = useTranslation();

  return (
    <Table className="min-w-220">
      <TableHeader className="sticky top-0 z-10 bg-card">
        <TableRow>
          <TableHead className={HEAD_CLASS}>{t('checkIns.table.client')}</TableHead>
          <TableHead className={HEAD_CLASS}>{t('checkIns.table.form')}</TableHead>
          <TableHead className={HEAD_CLASS}>{t('checkIns.table.sent')}</TableHead>
          <TableHead className={HEAD_CLASS}>{t('checkIns.table.answered')}</TableHead>
          <TableHead className={HEAD_CLASS}>{t('checkIns.table.status')}</TableHead>
          <TableHead className="px-4 py-3.5">
            <span className="sr-only">{t('checkIns.table.actions')}</span>
          </TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {isPending &&
          Array.from({ length: SKELETON_ROW_COUNT }).map((_, index) => (
            <TableRow key={index}>
              <TableCell colSpan={COLUMN_COUNT}>
                <Skeleton className="h-10 w-full" />
              </TableCell>
            </TableRow>
          ))}

        {!isPending && isError && (
          <TableRow>
            <TableCell colSpan={COLUMN_COUNT} className="py-10 text-center">
              <div className="flex flex-col items-center gap-3">
                <p className="text-body text-muted-foreground">{t('checkIns.loadError')}</p>
                <Button type="button" variant="outline" size="sm" onClick={onRetry}>
                  {t('checkIns.retry')}
                </Button>
              </div>
            </TableCell>
          </TableRow>
        )}

        {!isPending && !isError && rows.length === 0 && (
          <TableRow>
            <TableCell colSpan={COLUMN_COUNT} className="py-10 text-center text-body text-muted-foreground">
              {emptyText}
            </TableCell>
          </TableRow>
        )}

        {!isPending &&
          !isError &&
          rows.map((row) => (
            <CheckInRow key={row.id} row={row} markingId={markingId} onMarkReviewed={onMarkReviewed} />
          ))}
      </TableBody>
    </Table>
  );
}
