import { useTranslation } from 'react-i18next';
import { Ban, Check, Copy, MoreHorizontal, Send } from 'lucide-react';
import { Button } from '@/components/ui/button';
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import {
  ClientPaymentKind,
  ClientPaymentStatus,
  type ClientPaymentRowDto,
} from '@/api/client-payments';
import PaymentStatusChip from '@/components/client-detail/payment/PaymentStatusChip';
import { isOpenPayment } from '@/components/client-detail/payment/paymentModel';
import { showError, showSuccess } from '@/lib/api-errors';
import { formatPaymentAmount, formatPeriodLabel, formatShortDay } from '@/lib/payment-format';

const HEAD_CLASS = 'px-3 py-3.5 text-meta font-semibold';
const CELL_CLASS = 'px-3 py-2.75 text-body text-ink-2';

interface Props {
  rows: readonly ClientPaymentRowDto[];
  /** False while the coach has no bank account: a request would be refused. */
  canRequest: boolean;
  onMarkPaid: (row: ClientPaymentRowDto) => void;
  onRequest: (row: ClientPaymentRowDto) => void;
  onWaive: (row: ClientPaymentRowDto) => void;
}

/** The client's payments, newest due date first, each with the action that fits its status. */
export default function PaymentsTable({ rows, canRequest, onMarkPaid, onRequest, onWaive }: Props) {
  const { t, i18n } = useTranslation();
  const language = i18n.language;

  const copyVariableSymbol = async (symbol: string) => {
    try {
      await navigator.clipboard.writeText(symbol);
      showSuccess('payments.client.row.copied');
    } catch {
      showError('payments.client.row.copyError');
    }
  };

  return (
    <div className="@container">
      <Table className="min-w-170">
        <TableHeader className="bg-card">
          <TableRow>
            <TableHead className={HEAD_CLASS}>{t('payments.client.table.period')}</TableHead>
            <TableHead className={HEAD_CLASS}>{t('payments.client.table.amount')}</TableHead>
            <TableHead className={HEAD_CLASS}>{t('payments.client.table.due')}</TableHead>
            <TableHead className={HEAD_CLASS}>{t('payments.client.table.variableSymbol')}</TableHead>
            <TableHead className={HEAD_CLASS}>{t('payments.client.table.status')}</TableHead>
            <TableHead className="px-3 py-3.5">
              <span className="sr-only">{t('payments.client.table.actions')}</span>
            </TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {rows.map((row) => {
            const open = isOpenPayment(row);
            const canMarkPaid = open && row.status !== ClientPaymentStatus.Upcoming;
            const hasMenu = open || Boolean(row.variableSymbol);
            const isOneOff = row.kind === ClientPaymentKind.OneOff;
            return (
              <TableRow key={row.paymentId} className="border-line" data-testid="payment-row">
                <TableCell className={CELL_CLASS}>
                  <span className="flex max-w-44 flex-col">
                    <span className="truncate font-semibold text-ink" title={isOneOff ? row.title : undefined}>
                      {isOneOff ? row.title : formatPeriodLabel(row.periodStart, row.periodEnd, language)}
                    </span>
                    {isOneOff && <span className="text-meta text-muted-foreground">{t('payments.client.row.oneOff')}</span>}
                  </span>
                </TableCell>
                <TableCell className={`${CELL_CLASS} whitespace-nowrap`}>
                  {formatPaymentAmount(row.amount, row.currency, language)}
                </TableCell>
                <TableCell className={`${CELL_CLASS} whitespace-nowrap`}>
                  {row.status === ClientPaymentStatus.Waived ? '—' : formatShortDay(row.dueDate, language)}
                </TableCell>
                <TableCell className={`${CELL_CLASS} tabular-nums`}>{row.variableSymbol ?? '—'}</TableCell>
                <TableCell
                  className={`${CELL_CLASS} whitespace-normal [&_[data-slot=badge]]:h-auto [&_[data-slot=badge]]:max-w-48 [&_[data-slot=badge]]:whitespace-normal`}
                >
                  <PaymentStatusChip row={row} />
                </TableCell>
                <TableCell className={CELL_CLASS}>
                  <span className="flex items-center justify-end gap-1.5">
                    {canMarkPaid && (
                      <Button
                        type="button"
                        variant="outline"
                        size="sm"
                        aria-label={t('payments.client.row.markPaid')}
                        title={t('payments.client.row.markPaid')}
                        className="h-7.5 px-2 font-semibold @[54rem]:px-3.5"
                        onClick={() => onMarkPaid(row)}
                      >
                        <Check aria-hidden="true" />
                        <span className="hidden @[54rem]:inline">{t('payments.client.row.markPaid')}</span>
                      </Button>
                    )}
                    {hasMenu && (
                      <DropdownMenu>
                        <DropdownMenuTrigger asChild>
                          <Button
                            type="button"
                            variant="ghost"
                            size="icon-sm"
                            aria-label={t('payments.client.row.menu')}
                            className="text-muted-foreground"
                          >
                            <MoreHorizontal aria-hidden="true" />
                          </Button>
                        </DropdownMenuTrigger>
                        <DropdownMenuContent align="end">
                          {open && (
                            <DropdownMenuItem disabled={!canRequest} onSelect={() => onRequest(row)}>
                              <Send aria-hidden="true" />
                              {row.status === ClientPaymentStatus.Upcoming
                                ? t('payments.client.row.request')
                                : t('payments.client.row.requestAgain')}
                            </DropdownMenuItem>
                          )}
                          {open && !canMarkPaid && (
                            <DropdownMenuItem onSelect={() => onMarkPaid(row)}>
                              <Check aria-hidden="true" />
                              {t('payments.client.row.markPaid')}
                            </DropdownMenuItem>
                          )}
                          {row.variableSymbol && (
                            <DropdownMenuItem onSelect={() => void copyVariableSymbol(row.variableSymbol ?? '')}>
                              <Copy aria-hidden="true" />
                              {t('payments.client.row.copySymbol')}
                            </DropdownMenuItem>
                          )}
                          {open && <DropdownMenuSeparator />}
                          {open && (
                            <DropdownMenuItem onSelect={() => onWaive(row)}>
                              <Ban aria-hidden="true" />
                              {t('payments.client.row.waive')}
                            </DropdownMenuItem>
                          )}
                        </DropdownMenuContent>
                      </DropdownMenu>
                    )}
                  </span>
                </TableCell>
              </TableRow>
            );
          })}
        </TableBody>
      </Table>
    </div>
  );
}
