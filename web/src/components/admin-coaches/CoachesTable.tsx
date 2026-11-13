import { useTranslation } from 'react-i18next';
import { AdminCoachStatus, type AdminCoachRowDto } from '@/api/admin-coaches';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { CELL_CLASS, CELL_TEXT_CLASS, HEAD_CLASS } from '@/components/package-catalog/catalogParts';
import CoachStatusBadge from '@/components/admin-coaches/CoachStatusBadge';
import { coachInitials, coachPlanName, formatCoachDate } from '@/components/admin-coaches/coachFormat';

const COLUMN_COUNT = 7;

interface Props {
  coaches: AdminCoachRowDto[];
  isPending: boolean;
  isError: boolean;
  onRetry: () => void;
  onOpen: (coachId: string) => void;
}

/** The table of coaches: who, roles, plan, status, clients used of the cap, promotion and renewal. */
export default function CoachesTable({ coaches, isPending, isError, onRetry, onOpen }: Props) {
  const { t, i18n } = useTranslation();

  /** The last column: renewal date for payers, end for cancelling, gift end for given free. */
  const renewsOrEnds = (coach: AdminCoachRowDto): string => {
    const periodEnd = formatCoachDate(coach.currentPeriodEndsAt, i18n.language);
    switch (coach.status) {
      case AdminCoachStatus.Paying:
      case AdminCoachStatus.PaymentFailed:
        return periodEnd ? t('adminCoaches.table.renews', { date: periodEnd }) : '—';
      case AdminCoachStatus.Cancelling:
        return periodEnd ? t('adminCoaches.table.ends', { date: periodEnd }) : '—';
      case AdminCoachStatus.GivenFree: {
        const until = formatCoachDate(coach.givenFreeUntil, i18n.language);
        return until ? t('adminCoaches.table.until', { date: until }) : t('adminCoaches.table.noEnd');
      }
      default:
        return '—';
    }
  };

  return (
    <Table className="min-w-230">
      <TableHeader className="sticky top-0 z-10 bg-card">
        <TableRow>
          <TableHead className={HEAD_CLASS}>{t('adminCoaches.table.coach')}</TableHead>
          <TableHead className={HEAD_CLASS}>{t('adminCoaches.table.roles')}</TableHead>
          <TableHead className={HEAD_CLASS}>{t('adminCoaches.table.plan')}</TableHead>
          <TableHead className={HEAD_CLASS}>{t('adminCoaches.table.status')}</TableHead>
          <TableHead className={HEAD_CLASS}>{t('adminCoaches.table.clients')}</TableHead>
          <TableHead className={HEAD_CLASS}>{t('adminCoaches.table.promotion')}</TableHead>
          <TableHead className={HEAD_CLASS}>{t('adminCoaches.table.renewsEnds')}</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {isPending &&
          Array.from({ length: 6 }).map((_, index) => (
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
                <p className="text-body text-muted-foreground">{t('adminCoaches.loadError')}</p>
                <Button type="button" variant="outline" size="sm" onClick={onRetry}>
                  {t('adminCoaches.retry')}
                </Button>
              </div>
            </TableCell>
          </TableRow>
        )}
        {!isPending && !isError && coaches.length === 0 && (
          <TableRow>
            <TableCell colSpan={COLUMN_COUNT} className="py-10 text-center text-body text-muted-foreground">
              {t('adminCoaches.empty')}
            </TableCell>
          </TableRow>
        )}
        {!isPending &&
          !isError &&
          coaches.map((coach) => {
            const coachId = coach.coachId ?? '';
            const name = coach.name ?? '';
            const cap = coach.clientsMax == null ? '∞' : String(coach.clientsMax);
            return (
              <TableRow key={coachId} className="cursor-pointer border-line" onClick={() => onOpen(coachId)}>
                <TableCell className={CELL_CLASS}>
                  <div className="flex items-center gap-3">
                    <span
                      aria-hidden="true"
                      className="flex size-9 shrink-0 items-center justify-center rounded-full bg-sunken text-meta font-semibold text-ink-2"
                    >
                      {coachInitials(name)}
                    </span>
                    <div className="flex min-w-0 max-w-64 flex-col">
                      <button
                        type="button"
                        className="truncate text-left text-copy font-semibold text-ink outline-none hover:underline focus-visible:ring-3 focus-visible:ring-ring/50"
                        onClick={(event) => {
                          event.stopPropagation();
                          onOpen(coachId);
                        }}
                      >
                        {name}
                      </button>
                      <span className="truncate text-meta text-muted-foreground">{coach.email}</span>
                    </div>
                  </div>
                </TableCell>
                <TableCell className={CELL_CLASS}>
                  <div className="flex flex-wrap gap-1.5">
                    {(coach.roles ?? []).length === 0 && <span className="text-body text-muted-foreground">—</span>}
                    {(coach.roles ?? []).map((role) => (
                      <span key={role} className="rounded-md bg-sunken px-2.5 py-1 text-meta font-medium text-ink-2">
                        {t(`adminCoaches.roles.${role}`, { defaultValue: role })}
                      </span>
                    ))}
                  </div>
                </TableCell>
                <TableCell className={`${CELL_CLASS} text-copy font-semibold text-ink`}>
                  {coachPlanName(coach.plan, i18n.language)}
                </TableCell>
                <TableCell className={CELL_CLASS}>
                  <CoachStatusBadge status={coach.status} endsAt={coach.currentPeriodEndsAt} />
                </TableCell>
                <TableCell className={CELL_TEXT_CLASS}>
                  {t('adminCoaches.table.clientsOf', { used: coach.clientsUsed ?? 0, max: cap })}
                </TableCell>
                <TableCell className={CELL_TEXT_CLASS}>{coach.promotion ?? '—'}</TableCell>
                <TableCell className={CELL_TEXT_CLASS}>{renewsOrEnds(coach)}</TableCell>
              </TableRow>
            );
          })}
      </TableBody>
    </Table>
  );
}
