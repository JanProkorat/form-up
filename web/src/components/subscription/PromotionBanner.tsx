import { useTranslation } from 'react-i18next';
import { Tag } from 'lucide-react';
import { PromotionDuration, type CoachPlanDto, type CoachPromotionDto } from '@/api/generated';
import { coachPlanName } from '@/components/subscription/planNames';
import { FREE_PLAN_CODE } from '@/components/subscription/planPricing';
import { formatNameList, promotionValueText } from '@/components/subscription/promotionText';

interface Props {
  promotion: CoachPromotionDto;
  plans: CoachPlanDto[];
  /** "picker": prices still get the offer at checkout. "register": the prices below already show it. */
  variant: 'picker' | 'register';
}

/** The automatic offer above the plan cards: its name, what it takes off, which plans it covers and until when. */
export default function PromotionBanner({ promotion, plans, variant }: Props) {
  const { t, i18n } = useTranslation();
  const language = i18n.language;
  const value = promotionValueText(t, promotion, language);
  const name = promotion.name ?? '';

  const title =
    promotion.duration === PromotionDuration.FirstMonth
      ? t('subscription.promotion.banner.firstMonth', { name, value })
      : promotion.duration === PromotionDuration.Months
        ? t('subscription.promotion.banner.months', { name, value, count: promotion.durationMonths ?? 0 })
        : t('subscription.promotion.banner.forever', { name, value });

  const covered = promotion.planCodes ?? [];
  const paid = plans.filter((plan) => plan.code !== FREE_PLAN_CODE);
  const coveredPlans = paid.filter((plan) => plan.code !== undefined && covered.includes(plan.code));
  const coversAll = coveredPlans.length === paid.length;
  const planNames = formatNameList(
    coveredPlans.map((plan) => coachPlanName(plan, language)),
    language,
  );

  const scope =
    variant === 'register'
      ? coversAll
        ? t('subscription.promotion.banner.taken')
        : t('subscription.promotion.banner.takenSomePlans', { plans: planNames })
      : coversAll
        ? t('subscription.promotion.banner.allPlans')
        : t('subscription.promotion.banner.somePlans', { plans: planNames });
  const claimBy =
    variant === 'picker' && promotion.claimUntil
      ? t('subscription.promotion.banner.claimBy', {
          date: new Intl.DateTimeFormat(language, { dateStyle: 'medium' }).format(new Date(promotion.claimUntil)),
        })
      : null;

  return (
    <div role="status" className="flex items-start gap-3 rounded-xl bg-success-soft px-4 py-3.5">
      <Tag className="mt-0.5 size-4.5 shrink-0 text-success-ink" aria-hidden="true" />
      <div className="flex flex-col gap-0.5">
        <span className="text-copy font-semibold text-ink">{title}</span>
        <span className="text-body text-ink-2">{claimBy ? `${scope} ${claimBy}` : scope}</span>
      </div>
    </div>
  );
}
