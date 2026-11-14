import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Info, Plus } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { PromotionStatusFilter, type PromotionDto } from '@/api/admin-promotions';
import {
  getErrorCode,
  getErrorStatus,
  getRfc7807ErrorCode,
  showApiError,
  showError,
  showSuccess,
} from '@/lib/api-errors';
import EndPromotionDialog from '@/components/admin-promotions/EndPromotionDialog';
import { showBillingError } from '@/components/subscription/billingErrors';
import PromotionDrawer, { type PromotionSaveOutcome } from '@/components/admin-promotions/PromotionDrawer';
import PromotionsTable from '@/components/admin-promotions/PromotionsTable';
import {
  SERVER_FIELD_BY_CODE,
  formValuesToCreateRequest,
  type PromotionFormValues,
} from '@/components/admin-promotions/promotionForm';
import { useAdminPromotions, useCreatePromotion, useEndPromotion } from '@/components/admin-promotions/useAdminPromotionsQueries';
import { cn } from '@/lib/utils';

const FILTERS: readonly PromotionStatusFilter[] = [
  PromotionStatusFilter.All,
  PromotionStatusFilter.Active,
  PromotionStatusFilter.Scheduled,
  PromotionStatusFilter.Ended,
];

const CODE_TAKEN_MESSAGE = 'adminPromotions.validation.codeTaken';
const CODE_TAKEN = 'PROMOTION_CODE_TAKEN';
const BILLING_NOT_CONFIGURED = 'BILLING_NOT_CONFIGURED';

const errorCodeOf = (error: unknown): string | null => getRfc7807ErrorCode(error) ?? getErrorCode(error);

/** Form Up admin → Promotions: discounts on Form Up plans, filtered by status, with a drawer to create one. */
export default function AdminPromotionsPage() {
  const { t } = useTranslation();
  const [status, setStatus] = useState<PromotionStatusFilter>(PromotionStatusFilter.All);
  const [drawerOpen, setDrawerOpen] = useState(false);
  const promotionsQuery = useAdminPromotions(status);
  const createPromotion = useCreatePromotion();
  const endPromotion = useEndPromotion();
  const [endTarget, setEndTarget] = useState<PromotionDto | null>(null);
  const promotions = promotionsQuery.data ?? [];

  const handleSave = async (values: PromotionFormValues): Promise<PromotionSaveOutcome> => {
    try {
      await createPromotion.mutateAsync(formValuesToCreateRequest(values));
      showSuccess('adminPromotions.saveSuccess');
      setDrawerOpen(false);
      return 'saved';
    } catch (error) {
      const code = errorCodeOf(error);
      const httpStatus = getErrorStatus(error);
      if (code === CODE_TAKEN) {
        return { field: 'code', message: CODE_TAKEN_MESSAGE };
      }
      const field = code ? SERVER_FIELD_BY_CODE[code] : undefined;
      if (httpStatus === 400 && field && code) {
        return { field, message: `apiErrors.${code}` };
      }
      if (code === BILLING_NOT_CONFIGURED) {
        showBillingError(error, 'adminPromotions.saveError');
      } else {
        showApiError(error, 'adminPromotions.saveError');
      }
      return 'error';
    }
  };

  /** Ends the promotion in the dialog; resolves true when the dialog should close. */
  const handleEnd = async (): Promise<boolean> => {
    if (!endTarget?.promotionId) {
      return true;
    }
    try {
      await endPromotion.mutateAsync(endTarget.promotionId);
      showSuccess('adminPromotions.end.success');
      return true;
    } catch (error) {
      const httpStatus = getErrorStatus(error);
      if (httpStatus === 404) {
        showError('adminPromotions.end.notFound');
        return true;
      }
      if (httpStatus === 409) {
        showApiError(error, 'adminPromotions.end.error');
        return true;
      }
      // 502 and 503: the promotion is unchanged, so the dialog stays open for another try.
      showBillingError(error, 'adminPromotions.end.error');
      return false;
    }
  };

  return (
    <div className="flex h-full flex-col gap-4">
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div className="flex flex-col gap-1.5">
          <h1 className="text-display font-semibold text-ink">{t('adminPromotions.title')}</h1>
          <p className="text-copy text-muted-foreground">{t('adminPromotions.subtitle')}</p>
        </div>
        <Button type="button" size="lg" className="gap-1.75 px-3.5 font-semibold" onClick={() => setDrawerOpen(true)}>
          <Plus aria-hidden="true" />
          {t('adminPromotions.newPromotion')}
        </Button>
      </div>

      <div role="group" aria-label={t('adminPromotions.filters.label')} className="flex flex-wrap gap-2">
        {FILTERS.map((filter) => {
          const selected = status === filter;
          return (
            <button
              key={filter}
              type="button"
              aria-pressed={selected}
              onClick={() => setStatus(filter)}
              className={cn(
                'inline-flex items-center rounded-full px-3.5 py-1.5 text-body font-medium outline-none focus-visible:ring-3 focus-visible:ring-ring/50',
                selected ? 'bg-ink text-primary-foreground' : 'bg-sunken text-ink-2 hover:bg-muted',
              )}
            >
              {t(`adminPromotions.filters.${filter}`)}
            </button>
          );
        })}
      </div>

      <div className="flex min-h-0 flex-col overflow-hidden rounded-2xl border border-line bg-card shadow-panel">
        <div className="min-h-0 overflow-y-auto [&_[data-slot=table-container]]:overflow-visible">
          <PromotionsTable
            promotions={promotions}
            isPending={promotionsQuery.isPending}
            isError={promotionsQuery.isError}
            onRetry={() => void promotionsQuery.refetch()}
            onEnd={setEndTarget}
          />
        </div>
      </div>

      <p className="flex items-center gap-2 text-body text-muted-foreground">
        <Info className="size-4 shrink-0" aria-hidden="true" />
        {t('adminPromotions.note')}
      </p>

      <EndPromotionDialog
        promotion={endTarget ? { name: endTarget.name ?? '', usedCount: endTarget.usedCount ?? 0 } : null}
        onOpenChange={(open) => {
          if (!open) setEndTarget(null);
        }}
        run={handleEnd}
      />

      {drawerOpen && (
        <PromotionDrawer
          open
          onOpenChange={setDrawerOpen}
          onSubmit={handleSave}
          isSaving={createPromotion.isPending}
        />
      )}
    </div>
  );
}
