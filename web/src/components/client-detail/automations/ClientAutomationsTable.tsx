import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Camera, CheckSquare, FileText, MoreHorizontal, Pause, RotateCcw, Ruler } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Switch } from '@/components/ui/switch';
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import {
  AutomationActionType,
  AutomationRuleSource,
  type AutomationRuleDto,
  type ClientAutomationItem,
} from '@/api/automations';
import AutomationIconTile from '@/components/automations/AutomationIconTile';
import { formatNextSend, isCheckInRule, toneForAction, whenLabel } from '@/components/automations/automationFormat';
import { clientItemToRule } from '@/components/automations/automationRuleForm';
import { lastResultLabel, rowSubtitle } from '@/components/client-detail/automations/clientAutomationModel';
import { cn } from '@/lib/utils';

const HEAD_CLASS = 'px-4 py-3.5 text-meta font-semibold';
const CELL_CLASS = 'px-4 py-2.75';

const ACTION_ICON: Record<AutomationActionType, ReactNode> = {
  [AutomationActionType.SendForm]: <FileText className="size-4.5" aria-hidden="true" />,
  [AutomationActionType.RequestProgressPhotos]: <Camera className="size-4.5" aria-hidden="true" />,
  [AutomationActionType.RequestMeasurements]: <Ruler className="size-4.5" aria-hidden="true" />,
};

const BADGE_BASE = 'inline-flex items-center gap-1 rounded-full px-2.5 py-1 text-meta font-semibold whitespace-nowrap';

function SourceBadge({ source, name }: { source: AutomationRuleSource | undefined; name: string }) {
  const { t } = useTranslation();
  switch (source) {
    case AutomationRuleSource.Custom:
      return (
        <span className={cn(BADGE_BASE, 'bg-training-soft text-training-ink')}>
          {t('clientDetail.automations.source.Custom', { name })}
        </span>
      );
    case AutomationRuleSource.OnlyThisClient:
      return (
        <span className={cn(BADGE_BASE, 'bg-training-soft text-training-ink')}>
          {t('clientDetail.automations.source.OnlyThisClient', { name })}
        </span>
      );
    case AutomationRuleSource.Paused:
      return (
        <span className={cn(BADGE_BASE, 'bg-sunken text-ink-2')}>
          <Pause className="size-3" aria-hidden="true" />
          {t('clientDetail.automations.source.Paused')}
        </span>
      );
    default:
      return (
        <span className={cn(BADGE_BASE, 'border border-line text-ink-2')}>
          {t('clientDetail.automations.source.Default')}
        </span>
      );
  }
}

interface Props {
  items: ClientAutomationItem[];
  /** The client's first name, for the badges. */
  clientName: string;
  /** Global rules by public id, for the "Changed from" text of a customised row. */
  parentRules: ReadonlyMap<string, AutomationRuleDto>;
  /** Questionnaire titles by public id, for the "Form · name" subtitle. */
  formTitles: ReadonlyMap<string, string>;
  /** Rule ids with a write in flight. */
  busyIds: ReadonlySet<string>;
  /** The plan has no per-client check-in settings: a check-in row cannot be customised. */
  checkInOverrideLocked: boolean;
  onOpen: (item: ClientAutomationItem) => void;
  onToggle: (item: ClientAutomationItem, on: boolean) => void;
  onReset: (item: ClientAutomationItem) => void;
  onDelete: (item: ClientAutomationItem) => void;
}

/** The client's effective rules; the weekly check-in is an ordinary rule row. */
export default function ClientAutomationsTable({
  items,
  clientName,
  parentRules,
  formTitles,
  busyIds,
  checkInOverrideLocked,
  onOpen,
  onToggle,
  onReset,
  onDelete,
}: Props) {
  const { t, i18n } = useTranslation();
  const language = i18n.language;

  return (
    <Table className="min-w-220">
      <TableHeader className="bg-card">
        <TableRow>
          <TableHead className={HEAD_CLASS}>{t('automations.table.columnRule')}</TableHead>
          <TableHead className={HEAD_CLASS}>{t('automations.table.columnWhen')}</TableHead>
          <TableHead className={HEAD_CLASS}>{t('clientDetail.automations.table.columnSource')}</TableHead>
          <TableHead className={HEAD_CLASS}>{t('automations.table.columnNextSend')}</TableHead>
          <TableHead className={HEAD_CLASS}>{t('clientDetail.automations.table.columnLastTime')}</TableHead>
          <TableHead className="px-4 py-3.5">
            <span className="sr-only">{t('automations.table.columnActions')}</span>
          </TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {items.map((item) => {
          const ruleId = item.ruleId ?? '';
          const name = item.name ?? '';
          const isClientOnly = item.source === AutomationRuleSource.OnlyThisClient;
          const rule = clientItemToRule(item);
          const checkIn = isCheckInRule(rule);
          const formTitle = item.questionnaireId ? formTitles.get(item.questionnaireId) : undefined;
          const globalDisabled = !isClientOnly && item.enabled === false;
          const checked = isClientOnly ? item.enabled !== false : !item.isPaused && !globalDisabled;
          const busy = busyIds.has(ruleId);
          const overrideLocked = checkIn && !isClientOnly && checkInOverrideLocked;

          return (
            <TableRow key={ruleId} className="border-line">
              <TableCell className={CELL_CLASS}>
                <div className="flex items-center gap-3">
                  <AutomationIconTile tone={checkIn ? 'checkIn' : toneForAction(item.actionType)}>
                    {checkIn ? (
                      <CheckSquare className="size-4.5" aria-hidden="true" />
                    ) : (
                      ACTION_ICON[item.actionType ?? AutomationActionType.SendForm]
                    )}
                  </AutomationIconTile>
                  <div className="flex min-w-0 max-w-80 flex-col">
                    <button
                      type="button"
                      disabled={overrideLocked}
                      className="truncate text-left text-copy font-semibold text-ink outline-none hover:underline focus-visible:ring-3 focus-visible:ring-ring/50 disabled:cursor-not-allowed disabled:no-underline"
                      onClick={() => onOpen(item)}
                    >
                      {name}
                    </button>
                    <span className="truncate text-meta text-muted-foreground">
                      {overrideLocked
                        ? t('subscription.checkIns.overrideLocked')
                        : rowSubtitle(item, parentRules.get(ruleId), formTitle, t, language)}
                    </span>
                  </div>
                </div>
              </TableCell>
              <TableCell className={`${CELL_CLASS} text-body text-ink-2`}>{whenLabel(rule, t)}</TableCell>
              <TableCell className={CELL_CLASS}>
                <SourceBadge source={item.source} name={clientName} />
              </TableCell>
              <TableCell className={`${CELL_CLASS} text-body text-ink-2`}>
                {formatNextSend(item.nextSendAt, language)}
              </TableCell>
              <TableCell className={`${CELL_CLASS} text-body text-ink-2`}>
                {lastResultLabel(item.lastResult, t, language)}
              </TableCell>
              <TableCell className={CELL_CLASS}>
                <span className="flex items-center justify-end gap-3">
                  {item.source === AutomationRuleSource.Custom && (
                    <Button type="button" variant="ghost" size="sm" disabled={busy} onClick={() => onReset(item)}>
                      <RotateCcw aria-hidden="true" />
                      {t('clientDetail.automations.row.reset')}
                    </Button>
                  )}
                  <Switch
                    checked={checked}
                    disabled={busy || globalDisabled}
                    title={globalDisabled ? t('clientDetail.automations.row.disabledHint') : undefined}
                    onCheckedChange={(on) => onToggle(item, on)}
                    aria-label={t(
                      isClientOnly ? 'automations.table.toggle' : 'clientDetail.automations.row.toggleActive',
                      { name },
                    )}
                  />
                  {isClientOnly && (
                    <DropdownMenu>
                      <DropdownMenuTrigger asChild>
                        <Button
                          type="button"
                          variant="ghost"
                          size="icon-sm"
                          aria-label={t('automations.rowMenu.open', { name })}
                        >
                          <MoreHorizontal className="size-4" aria-hidden="true" />
                        </Button>
                      </DropdownMenuTrigger>
                      <DropdownMenuContent align="end">
                        <DropdownMenuItem onSelect={() => onOpen(item)}>{t('automations.rowMenu.edit')}</DropdownMenuItem>
                        <DropdownMenuSeparator />
                        <DropdownMenuItem variant="destructive" onSelect={() => onDelete(item)}>
                          {t('automations.rowMenu.delete')}
                        </DropdownMenuItem>
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
  );
}
