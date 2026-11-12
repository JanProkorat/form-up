import { useTranslation } from 'react-i18next';
import { Layers, MoreHorizontal } from 'lucide-react';
import { Button } from '@/components/ui/button';
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu';
import { Skeleton } from '@/components/ui/skeleton';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import type { SubscriptionPlanDto } from '@/api/admin-subscription-plans';
import { CELL_CLASS, CELL_TEXT_CLASS, CatalogTile, HEAD_CLASS } from '@/components/package-catalog/catalogParts';
import { formatPlanMoney, isFreePlan, planName } from '@/components/admin-plans/planForm';
import { cn } from '@/lib/utils';

const COLUMN_COUNT = 8;

interface Props {
  plans: SubscriptionPlanDto[];
  isPending: boolean;
  isError: boolean;
  onRetry: () => void;
  onEdit: (plan: SubscriptionPlanDto) => void;
  onStopOffering: (plan: SubscriptionPlanDto) => void;
}

/** The feature chips of a plan; one "Everything" chip when every feature is on. */
function featureLabelKeys(plan: SubscriptionPlanDto): string[] {
  const flags: [boolean | undefined, string][] = [
    [plan.canCreatePlans, 'plans'],
    [plan.canMessage, 'chat'],
    [plan.canSendQuestionnaires, 'forms'],
    [plan.canUseWeeklyCheckIns, 'checkIns'],
    [plan.canUsePerClientCheckInConfig, 'perClientCheckIns'],
    [plan.canUsePayments, 'payments'],
  ];
  const on = flags.filter(([enabled]) => enabled).map(([, key]) => key);
  return on.length === flags.length ? ['everything'] : on;
}

/** The plan catalogue: name and code, price, second role, client cap, features, coaches and status. */
export default function PlansTable({ plans, isPending, isError, onRetry, onEdit, onStopOffering }: Props) {
  const { t, i18n } = useTranslation();

  return (
    <Table className="min-w-230">
      <TableHeader className="sticky top-0 z-10 bg-card">
        <TableRow>
          <TableHead className={HEAD_CLASS}>{t('adminPlans.table.plan')}</TableHead>
          <TableHead className={HEAD_CLASS}>{t('adminPlans.table.perMonth')}</TableHead>
          <TableHead className={HEAD_CLASS}>{t('adminPlans.table.secondRole')}</TableHead>
          <TableHead className={HEAD_CLASS}>{t('adminPlans.table.clients')}</TableHead>
          <TableHead className={HEAD_CLASS}>{t('adminPlans.table.features')}</TableHead>
          <TableHead className={HEAD_CLASS}>{t('adminPlans.table.coaches')}</TableHead>
          <TableHead className={HEAD_CLASS}>{t('adminPlans.table.status')}</TableHead>
          <TableHead className="w-12 px-4 py-3.5">
            <span className="sr-only">{t('adminPlans.table.actions')}</span>
          </TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {isPending &&
          Array.from({ length: 4 }).map((_, index) => (
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
                <p className="text-body text-muted-foreground">{t('adminPlans.loadError')}</p>
                <Button type="button" variant="outline" size="sm" onClick={onRetry}>
                  {t('adminPlans.retry')}
                </Button>
              </div>
            </TableCell>
          </TableRow>
        )}
        {!isPending && !isError && plans.length === 0 && (
          <TableRow>
            <TableCell colSpan={COLUMN_COUNT} className="py-10 text-center text-body text-muted-foreground">
              {t('adminPlans.empty')}
            </TableCell>
          </TableRow>
        )}
        {!isPending &&
          !isError &&
          plans.map((plan) => {
            const code = plan.code ?? '';
            const name = planName(plan, i18n.language);
            const offered = plan.isActive ?? false;
            return (
              <TableRow key={code} className="cursor-pointer border-line" onClick={() => onEdit(plan)}>
                <TableCell className={CELL_CLASS}>
                  <div className="flex items-center gap-3">
                    <CatalogTile muted={!offered}>
                      <Layers className="size-4.5" aria-hidden="true" />
                    </CatalogTile>
                    <div className="flex min-w-0 max-w-64 flex-col">
                      <button
                        type="button"
                        className="truncate text-left text-copy font-semibold text-ink outline-none hover:underline focus-visible:ring-3 focus-visible:ring-ring/50"
                        onClick={(event) => {
                          event.stopPropagation();
                          onEdit(plan);
                        }}
                      >
                        {name}
                      </button>
                      <span className="truncate text-meta text-muted-foreground">
                        {t('adminPlans.table.code', { code })}
                      </span>
                    </div>
                  </div>
                </TableCell>
                <TableCell className={`${CELL_CLASS} text-copy font-semibold text-ink`}>
                  {formatPlanMoney(plan.priceMinorUnits ?? 0, plan.currency, i18n.language)}
                </TableCell>
                <TableCell className={CELL_TEXT_CLASS}>
                  {plan.bothRolesIncluded || plan.secondRoleAddOnPriceMinorUnits == null
                    ? t('adminPlans.table.included')
                    : formatPlanMoney(plan.secondRoleAddOnPriceMinorUnits, plan.currency, i18n.language)}
                </TableCell>
                <TableCell className={CELL_TEXT_CLASS}>
                  {plan.maxActiveClients == null ? t('adminPlans.unlimited') : plan.maxActiveClients}
                </TableCell>
                <TableCell className={CELL_CLASS}>
                  <div className="flex flex-wrap gap-1.5">
                    {featureLabelKeys(plan).map((key) => (
                      <span
                        key={key}
                        className="rounded-md bg-sunken px-2.5 py-1 text-meta font-medium text-ink-2"
                      >
                        {t(`adminPlans.features.chip.${key}`)}
                      </span>
                    ))}
                  </div>
                </TableCell>
                <TableCell className={CELL_TEXT_CLASS}>{plan.coachCount ?? 0}</TableCell>
                <TableCell className={CELL_CLASS}>
                  <span
                    className={cn(
                      'inline-flex items-center gap-1.5 rounded-full px-2.5 py-1 text-meta font-semibold',
                      offered ? 'bg-success-soft text-success-ink' : 'bg-sunken text-ink-2',
                    )}
                  >
                    {offered && <span className="size-1.5 rounded-full bg-current" aria-hidden="true" />}
                    {t(offered ? 'adminPlans.status.active' : 'adminPlans.status.stopped')}
                  </span>
                </TableCell>
                <TableCell className={CELL_CLASS}>
                  <DropdownMenu>
                    <DropdownMenuTrigger asChild>
                      <Button
                        type="button"
                        variant="ghost"
                        size="icon-sm"
                        aria-label={t('adminPlans.rowMenu.open', { name })}
                        onClick={(event) => event.stopPropagation()}
                      >
                        <MoreHorizontal className="size-4" aria-hidden="true" />
                      </Button>
                    </DropdownMenuTrigger>
                    <DropdownMenuContent align="end" onClick={(event) => event.stopPropagation()}>
                      <DropdownMenuItem onSelect={() => onEdit(plan)}>{t('adminPlans.rowMenu.edit')}</DropdownMenuItem>
                      {offered && !isFreePlan(plan) && (
                        <>
                          <DropdownMenuSeparator />
                          <DropdownMenuItem variant="destructive" onSelect={() => onStopOffering(plan)}>
                            {t('adminPlans.rowMenu.stopOffering')}
                          </DropdownMenuItem>
                        </>
                      )}
                    </DropdownMenuContent>
                  </DropdownMenu>
                </TableCell>
              </TableRow>
            );
          })}
      </TableBody>
    </Table>
  );
}
