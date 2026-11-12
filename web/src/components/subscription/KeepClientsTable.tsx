import { useTranslation } from 'react-i18next';
import { KeepClientAfterLapse, UserRole, type KeepClientDto } from '@/api/generated';
import { Checkbox } from '@/components/ui/checkbox';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { CELL_CLASS, CELL_TEXT_CLASS, HEAD_CLASS } from '@/components/package-catalog/catalogParts';

interface Props {
  clients: KeepClientDto[];
  selected: ReadonlySet<string>;
  /** True when the selection is at the cap: unchecked rows can no longer be ticked. */
  full: boolean;
  disabled: boolean;
  /** True when the choice is locked: kept rows stay ticked and cannot be unticked. */
  locked?: boolean;
  onToggle: (clientId: string) => void;
}

const ROLE_ORDER: readonly UserRole[] = [UserRole.Trainer, UserRole.Nutritionist];

/** Plain templates, not cn(): twMerge would drop text-meta next to a text colour class. */
const BADGE_BASE = 'inline-flex items-center gap-1.25 rounded-full px-2.5 py-1 text-meta font-semibold whitespace-nowrap';
const ROLE_TONE: Partial<Record<UserRole, string>> = {
  [UserRole.Trainer]: 'bg-training-soft text-training-ink',
  [UserRole.Nutritionist]: 'bg-nutrition-soft text-nutrition-ink',
};
const AFTER_TONE: Record<KeepClientAfterLapse, string> = {
  [KeepClientAfterLapse.FullAccess]: 'bg-success-soft text-success-ink',
  [KeepClientAfterLapse.RunsUntilEndThenReadOnly]: 'bg-training-soft text-training-ink',
  [KeepClientAfterLapse.ReadOnly]: 'bg-sunken text-muted-foreground',
};

function initialsOf(firstName: string, lastName: string): string {
  return `${firstName[0] ?? ''}${lastName[0] ?? ''}`.toUpperCase() || '?';
}

/** Date-only values (yyyy-mm-dd) are formatted in UTC so the calendar day never shifts with the viewer's zone. */
function formatDay(value: string | undefined, language: string): string {
  if (!value) return '';
  return new Intl.DateTimeFormat(language, { day: 'numeric', month: 'short', timeZone: 'UTC' }).format(new Date(value));
}

/** The lapsed coach's live clients with a checkbox per row, the roles they are linked under and what happens next. */
export default function KeepClientsTable({ clients, selected, full, disabled, locked = false, onToggle }: Props) {
  const { t, i18n } = useTranslation();

  return (
    <Table className="min-w-190">
      <TableHeader className="sticky top-0 z-10 bg-card">
        <TableRow>
          <TableHead className={HEAD_CLASS}>{t('subscription.lapse.table.keep')}</TableHead>
          <TableHead className={HEAD_CLASS}>{t('subscription.lapse.table.client')}</TableHead>
          <TableHead className={HEAD_CLASS}>{t('subscription.lapse.table.roles')}</TableHead>
          <TableHead className={HEAD_CLASS}>{t('subscription.lapse.table.publishedEnd')}</TableHead>
          <TableHead className={HEAD_CLASS}>{t('subscription.lapse.table.after')}</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {clients.map((client) => {
          const clientId = client.clientId ?? '';
          const firstName = client.firstName ?? '';
          const lastName = client.lastName ?? '';
          const name = `${firstName} ${lastName}`.trim();
          const checked = selected.has(clientId);
          const roles = ROLE_ORDER.filter((role) => client.roles?.includes(role));
          const afterLapse = client.afterLapse ?? KeepClientAfterLapse.ReadOnly;
          const endsOn = formatDay(client.publishedWeeksEnd, i18n.language);
          const afterLabel =
            afterLapse === KeepClientAfterLapse.RunsUntilEndThenReadOnly
              ? endsOn
                ? t('subscription.lapse.after.runsUntil', { date: endsOn })
                : t('subscription.lapse.after.runsUntilNoDate')
              : t(`subscription.lapse.after.${afterLapse === KeepClientAfterLapse.FullAccess ? 'fullAccess' : 'readOnly'}`);

          return (
            <TableRow key={clientId} className="border-line">
              <TableCell className={CELL_CLASS}>
                <Checkbox
                  checked={checked}
                  disabled={disabled || (full && !checked) || (locked && client.isKept === true)}
                  aria-label={t('subscription.lapse.keepAria', { name })}
                  onCheckedChange={() => onToggle(clientId)}
                  className="size-4.5"
                />
              </TableCell>
              <TableCell className={CELL_CLASS}>
                <div className="flex items-center gap-3">
                  <span
                    aria-hidden="true"
                    className="flex size-9 shrink-0 items-center justify-center rounded-full bg-sunken text-meta font-semibold text-ink"
                  >
                    {initialsOf(firstName, lastName)}
                  </span>
                  <span className="text-copy font-semibold text-ink">{name}</span>
                </div>
              </TableCell>
              <TableCell className={CELL_CLASS}>
                <div className="flex flex-wrap gap-1.5">
                  {roles.map((role) => (
                    <span key={role} className={`${BADGE_BASE} ${ROLE_TONE[role] ?? ''}`}>
                      {t(`roles.${role.toLowerCase()}`)}
                    </span>
                  ))}
                </div>
              </TableCell>
              <TableCell className={CELL_TEXT_CLASS}>
                {client.publishedWeeksEnd ? endsOn : t('subscription.lapse.notPublished')}
              </TableCell>
              <TableCell className={CELL_CLASS}>
                <span className={`${BADGE_BASE} ${AFTER_TONE[afterLapse]}`}>
                  <span className="size-1.5 rounded-full bg-current" aria-hidden="true" />
                  {afterLabel}
                </span>
              </TableCell>
            </TableRow>
          );
        })}
      </TableBody>
    </Table>
  );
}
