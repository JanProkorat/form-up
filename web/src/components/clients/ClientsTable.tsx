import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { Skeleton } from '@/components/ui/skeleton';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import type { ClientSummary } from '@/api/clients';
import { Avatar, AvatarFallback, AvatarImage } from '@/components/ui/avatar';
import ClientStatusBadge from '@/components/clients/ClientStatusBadge';
import OutsidePlanChip from '@/components/clients/OutsidePlanChip';
import ClientTagPill from '@/components/clients/ClientTagPill';
import ClientTagPickerPopover from '@/components/clients/ClientTagPickerPopover';
import PlanIconsCell from '@/components/clients/PlanIconsCell';
import ClientRowMenu from '@/components/clients/ClientRowMenu';
import ClientsEmptyState from '@/components/clients/ClientsEmptyState';

const COLUMN_COUNT = 7;
const SKELETON_ROW_COUNT = 5;
const CELL_CLASS = 'px-4 py-2.75 text-body';

function initials(firstName?: string, lastName?: string): string {
  return `${firstName?.[0] ?? ''}${lastName?.[0] ?? ''}`.toUpperCase() || '?';
}

interface Props {
  clients: ClientSummary[];
  isPending: boolean;
  isError: boolean;
  onRetry: () => void;
  hasActiveFilter: boolean;
  onClearFilters: () => void;
  onInviteClient: () => void;
  selectedIds: Set<string>;
  onToggleRow: (id: string) => void;
  onToggleAll: (ids: string[]) => void;
}

/** The Active/Paused/Archived tabs' table. See PendingTable for the Pending tab. */
export default function ClientsTable({
  clients,
  isPending,
  isError,
  onRetry,
  hasActiveFilter,
  onClearFilters,
  onInviteClient,
  selectedIds,
  onToggleRow,
  onToggleAll,
}: Props) {
  const { t } = useTranslation();

  const pageRowIds = clients.map((client) => client.publicId ?? '').filter(Boolean);
  const allSelected = pageRowIds.length > 0 && pageRowIds.every((id) => selectedIds.has(id));
  const someSelected = !allSelected && pageRowIds.some((id) => selectedIds.has(id));

  return (
    <Table>
      <TableHeader className="bg-card">
        <TableRow>
          <TableHead className="w-10">
            <Checkbox
              checked={allSelected ? true : someSelected ? 'indeterminate' : false}
              onCheckedChange={() => onToggleAll(pageRowIds)}
              aria-label={t('clients.table.selectAll')}
              disabled={pageRowIds.length === 0}
            />
          </TableHead>
          <TableHead>{t('clients.table.columnClient')}</TableHead>
          <TableHead>{t('clients.table.columnStatus')}</TableHead>
          <TableHead>{t('clients.table.columnTags')}</TableHead>
          <TableHead className="text-center">{t('clients.table.columnUnread')}</TableHead>
          <TableHead>{t('clients.table.columnPlans')}</TableHead>
          <TableHead className="w-10">
            <span className="sr-only">{t('common.actions')}</span>
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
                <p className="text-body text-muted-foreground">{t('common.dataLoadError')}</p>
                <Button type="button" variant="outline" size="sm" onClick={onRetry}>
                  {t('clients.retry')}
                </Button>
              </div>
            </TableCell>
          </TableRow>
        )}

        {!isPending && !isError && clients.length === 0 && (
          <TableRow>
            <TableCell colSpan={COLUMN_COUNT}>
              <ClientsEmptyState
                hasActiveFilter={hasActiveFilter}
                onClearFilters={onClearFilters}
                onInviteClient={onInviteClient}
              />
            </TableCell>
          </TableRow>
        )}

        {!isPending &&
          !isError &&
          clients.map((client) => {
            const rowId = client.publicId ?? '';
            // An ended link has no detail page (the API answers 404), so the row must not link to it.
            const ended = client.isActive === false;
            const canOpenDetail = Boolean(rowId) && !ended;
            const restricted = client.isOutsidePlan === true;
            return (
              <TableRow key={client.publicId} className={restricted ? 'bg-sunken' : undefined}>
                <TableCell className={CELL_CLASS}>
                  <Checkbox
                    checked={Boolean(rowId) && selectedIds.has(rowId)}
                    onCheckedChange={() => rowId && onToggleRow(rowId)}
                    disabled={!rowId}
                    aria-label={t('clients.table.selectRow', {
                      name: `${client.firstName ?? ''} ${client.lastName ?? ''}`.trim(),
                    })}
                  />
                </TableCell>
                <TableCell className={CELL_CLASS}>
                  <div className="flex items-center gap-2.5">
                    <Avatar className="size-8.5">
                      {client.avatarBlobUrl && <AvatarImage src={client.avatarBlobUrl} alt="" />}
                      <AvatarFallback className="bg-background text-label font-semibold text-ink-2">
                        {initials(client.firstName, client.lastName)}
                      </AvatarFallback>
                    </Avatar>
                    <div className="flex flex-col">
                      {canOpenDetail ? (
                        <Link
                          to={`/clients/${rowId}`}
                          className={`font-medium hover:underline ${restricted ? 'text-muted-foreground' : 'text-foreground'}`}
                        >
                          {client.firstName} {client.lastName}
                        </Link>
                      ) : (
                        <span className={`font-medium ${restricted ? 'text-muted-foreground' : 'text-foreground'}`}>
                          {client.firstName} {client.lastName}
                        </span>
                      )}
                      <span className="text-caption text-muted-foreground">{client.email}</span>
                    </div>
                  </div>
                </TableCell>
                <TableCell className={CELL_CLASS}>
                  <div className="flex flex-wrap items-center gap-1.5">
                    <ClientStatusBadge status={client.status} planLabels />
                    {restricted && <OutsidePlanChip />}
                  </div>
                </TableCell>
                <TableCell className={CELL_CLASS}>
                  <div className="flex flex-wrap items-center gap-1">
                    {(client.tags ?? []).map((tag) => (
                      <ClientTagPill key={tag.tagId} tag={tag} />
                    ))}
                    <ClientTagPickerPopover client={client} />
                  </div>
                </TableCell>
                <TableCell className={`${CELL_CLASS} text-center`}>
                  {(client.unreadMessageCount ?? 0) > 0 && (
                    <span
                      className="inline-flex h-5.5 min-w-5.5 items-center justify-center rounded-full bg-marker-solid px-1.5 text-label font-bold text-on-dark"
                      title={t('clients.table.unreadTooltip', { count: client.unreadMessageCount ?? 0 })}
                      aria-label={t('clients.table.unreadTooltip', { count: client.unreadMessageCount ?? 0 })}
                    >
                      {client.unreadMessageCount}
                    </span>
                  )}
                </TableCell>
                <TableCell className={CELL_CLASS}>
                  <PlanIconsCell
                    activePlans={client.activePlans ?? []}
                    firstName={client.firstName}
                    lastName={client.lastName}
                    avatarBlobUrl={client.avatarBlobUrl}
                  />
                </TableCell>
                <TableCell className={CELL_CLASS}>
                  <ClientRowMenu publicId={canOpenDetail || ended ? rowId : ''} ended={ended} />
                </TableCell>
              </TableRow>
            );
          })}
      </TableBody>
    </Table>
  );
}
