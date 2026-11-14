import { useTranslation } from 'react-i18next';
import { Tag } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import {
  PromotionDuration,
  PromotionMode,
  PromotionStatus,
  type PromotionDto,
} from '@/api/admin-promotions';
import { CELL_CLASS, CELL_TEXT_CLASS, CatalogTile, HEAD_CLASS } from '@/components/package-catalog/catalogParts';
import { PROMOTION_PLAN_CODES } from '@/components/admin-promotions/promotionForm';
import { formatNameList, promotionValueText } from '@/components/subscription/promotionText';
import { cn } from '@/lib/utils';

const COLUMN_COUNT = 8;

const STATUS_TONE: Record<PromotionStatus, string> = {
  [PromotionStatus.Active]: 'bg-success-soft text-success-ink',
  [PromotionStatus.Scheduled]: 'bg-caution-soft text-caution-ink',
  [PromotionStatus.Ended]: 'bg-sunken text-ink-2',
};

interface Props {
  promotions: PromotionDto[];
  isPending: boolean;
  isError: boolean;
  onRetry: () => void;
}

/** The promotions of the chosen tab: name, how coaches get it, value, length, window, plans, uses and status. */
export default function PromotionsTable({ promotions, isPending, isError, onRetry }: Props) {
  const { t, i18n } = useTranslation();
  const language = i18n.language;
  const dateFormat = new Intl.DateTimeFormat(language, { dateStyle: 'medium' });

  const lastsText = (promotion: PromotionDto) =>
    promotion.duration === PromotionDuration.FirstMonth
      ? t('adminPromotions.lasts.firstMonth')
      : promotion.duration === PromotionDuration.Months
        ? t('adminPromotions.lasts.months', { count: promotion.durationMonths ?? 0 })
        : t('adminPromotions.lasts.forever');

  const windowText = (promotion: PromotionDto) =>
    promotion.claimUntil
      ? t('adminPromotions.window.range', {
          from: dateFormat.format(new Date(promotion.claimFrom ?? '')),
          to: dateFormat.format(new Date(promotion.claimUntil)),
        })
      : t('adminPromotions.window.noEnd');

  const plansText = (promotion: PromotionDto) => {
    const codes = promotion.planCodes ?? [];
    if (PROMOTION_PLAN_CODES.every((code) => codes.includes(code))) {
      return t('adminPromotions.table.allPaidPlans');
    }
    return formatNameList(
      PROMOTION_PLAN_CODES.filter((code) => codes.includes(code)).map((code) => t(`adminPromotions.plans.${code}`)),
      language,
    );
  };

  return (
    <Table className="min-w-230">
      <TableHeader className="sticky top-0 z-10 bg-card">
        <TableRow>
          <TableHead className={HEAD_CLASS}>{t('adminPromotions.table.promotion')}</TableHead>
          <TableHead className={HEAD_CLASS}>{t('adminPromotions.table.how')}</TableHead>
          <TableHead className={HEAD_CLASS}>{t('adminPromotions.table.value')}</TableHead>
          <TableHead className={HEAD_CLASS}>{t('adminPromotions.table.lasts')}</TableHead>
          <TableHead className={HEAD_CLASS}>{t('adminPromotions.table.window')}</TableHead>
          <TableHead className={HEAD_CLASS}>{t('adminPromotions.table.plans')}</TableHead>
          <TableHead className={HEAD_CLASS}>{t('adminPromotions.table.used')}</TableHead>
          <TableHead className={HEAD_CLASS}>{t('adminPromotions.table.status')}</TableHead>
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
                <p className="text-body text-muted-foreground">{t('adminPromotions.loadError')}</p>
                <Button type="button" variant="outline" size="sm" onClick={onRetry}>
                  {t('adminPromotions.retry')}
                </Button>
              </div>
            </TableCell>
          </TableRow>
        )}
        {!isPending && !isError && promotions.length === 0 && (
          <TableRow>
            <TableCell colSpan={COLUMN_COUNT} className="py-10 text-center text-body text-muted-foreground">
              {t('adminPromotions.empty')}
            </TableCell>
          </TableRow>
        )}
        {!isPending &&
          !isError &&
          promotions.map((promotion) => {
            const isCode = promotion.mode === PromotionMode.Code;
            const status = promotion.status ?? PromotionStatus.Ended;
            return (
              <TableRow key={promotion.promotionId} className="border-line">
                <TableCell className={CELL_CLASS}>
                  <div className="flex items-center gap-3">
                    <CatalogTile muted={status === PromotionStatus.Ended}>
                      <Tag className="size-4.5" aria-hidden="true" />
                    </CatalogTile>
                    <div className="flex min-w-0 max-w-64 flex-col">
                      <span className="truncate text-copy font-semibold text-ink">{promotion.name}</span>
                      <span className="truncate text-meta text-muted-foreground">
                        {isCode ? t('adminPromotions.table.codeSubline') : t('adminPromotions.table.everyone')}
                      </span>
                    </div>
                  </div>
                </TableCell>
                <TableCell className={CELL_CLASS}>
                  {isCode ? (
                    <span className="rounded-md border border-dashed border-line px-2.5 py-1 font-mono text-meta font-medium text-ink-2">
                      {promotion.code}
                    </span>
                  ) : (
                    <span className="rounded-md bg-sunken px-2.5 py-1 text-meta font-medium text-ink-2">
                      {t('adminPromotions.table.automatic')}
                    </span>
                  )}
                </TableCell>
                <TableCell className={`${CELL_CLASS} text-copy font-semibold text-ink`}>
                  {promotionValueText(t, promotion, language)}
                </TableCell>
                <TableCell className={CELL_TEXT_CLASS}>{lastsText(promotion)}</TableCell>
                <TableCell className={CELL_TEXT_CLASS}>{windowText(promotion)}</TableCell>
                <TableCell className={CELL_TEXT_CLASS}>{plansText(promotion)}</TableCell>
                <TableCell className={CELL_TEXT_CLASS}>
                  {promotion.maxUses == null
                    ? (promotion.usedCount ?? 0)
                    : t('adminPromotions.table.usedOf', { count: promotion.usedCount ?? 0, max: promotion.maxUses })}
                </TableCell>
                <TableCell className={CELL_CLASS}>
                  <span
                    className={cn(
                      'inline-flex items-center gap-1.5 rounded-full px-2.5 py-1 text-meta font-semibold',
                      STATUS_TONE[status],
                    )}
                  >
                    <span className="size-1.5 rounded-full bg-current" aria-hidden="true" />
                    {t(`adminPromotions.status.${status}`)}
                  </span>
                </TableCell>
              </TableRow>
            );
          })}
      </TableBody>
    </Table>
  );
}
