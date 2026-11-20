import { useTranslation } from 'react-i18next';
import { Tag } from 'lucide-react';
import {
  BillingInterval,
  PromotionBillingPeriod,
  PromotionDuration,
  type CoachPlanDto,
  type CoachPromotionDto,
} from '@/api/generated';
import { coachPlanName } from '@/components/subscription/planNames';
import { FREE_PLAN_CODE } from '@/components/subscription/planPricing';
import { formatNameList, promotionCoversInterval, promotionValueText } from '@/components/subscription/promotionText';

/** The coach and the public promotion payloads both fit. */
type BannerPromotion = Pick<
  CoachPromotionDto,
  'name' | 'kind' | 'value' | 'currency' | 'duration' | 'durationMonths' | 'billingPeriod' | 'claimUntil' | 'planCodes'
>;

/** The coach and the public plan payloads both fit. */
type BannerPlan = Pick<CoachPlanDto, 'code' | 'nameCs' | 'nameEn' | 'nameDe'>;

interface Props {
  promotion: BannerPromotion;
  plans: BannerPlan[];
  /** "picker": prices still get the offer at checkout. "register": the prices below already show it. */
  variant: 'picker' | 'register';
  /** The billing interval the prices below show; picks the wording for a promotion limited to one period. */
  interval?: BillingInterval;
  /** Sentence appended to the scope line, already translated. */
  note?: string;
}

/** The automatic offer above the plan cards: its name, what it takes off, which plans it covers and until when. */
export default function PromotionBanner({ promotion, plans, variant, interval = BillingInterval.Monthly, note }: Props) {
  const { t, i18n } = useTranslation();
  const language = i18n.language;
  const value = promotionValueText(t, promotion, language);
  const name = promotion.name ?? '';

  const baseTitle =
    promotion.duration === PromotionDuration.FirstMonth
      ? t('subscription.promotion.banner.firstMonth', { name, value })
      : promotion.duration === PromotionDuration.Months
        ? t('subscription.promotion.banner.months', { name, value, count: promotion.durationMonths ?? 0 })
        : t('subscription.promotion.banner.forever', { name, value });
  // A forever discount reads the same on both periods; a timed one ends after the first year on annual billing.
  const timed = promotion.duration !== PromotionDuration.Forever;
  const period = promotion.billingPeriod ?? PromotionBillingPeriod.Both;
  const title =
    period === PromotionBillingPeriod.Annual
      ? timed
        ? t('subscription.promotion.banner.annualOnly', { name, value })
        : baseTitle
      : period === PromotionBillingPeriod.Monthly
        ? interval === BillingInterval.Annual
          ? t('subscription.promotion.banner.monthlyOnly', { title: baseTitle })
          : baseTitle
        : interval === BillingInterval.Annual && timed
          ? t('subscription.promotion.banner.orFirstYear', { title: baseTitle })
          : baseTitle;
  const coversInterval = promotionCoversInterval(promotion.billingPeriod, interval);

  const covered = promotion.planCodes ?? [];
  const paid = plans.filter((plan) => plan.code !== FREE_PLAN_CODE);
  const coveredPlans = paid.filter((plan) => plan.code !== undefined && covered.includes(plan.code));
  const coversAll = coveredPlans.length === paid.length;
  const planNames = formatNameList(
    coveredPlans.map((plan) => coachPlanName(plan, language)),
    language,
  );

  const scope = !coversInterval
    ? t('subscription.promotion.banner.otherPeriod')
    : variant === 'register'
      ? coversAll
        ? t('subscription.promotion.banner.taken')
        : t('subscription.promotion.banner.takenSomePlans', { plans: planNames })
      : coversAll
        ? t('subscription.promotion.banner.allPlans')
        : t('subscription.promotion.banner.somePlans', { plans: planNames });
  const claimBy =
    variant === 'picker' && coversInterval && promotion.claimUntil
      ? t('subscription.promotion.banner.claimBy', {
          date: new Intl.DateTimeFormat(language, { dateStyle: 'medium' }).format(new Date(promotion.claimUntil)),
        })
      : null;

  return (
    <div role="status" className="flex items-start gap-3 rounded-xl bg-training-soft px-4 py-3.5">
      <Tag className="mt-0.5 size-4.5 shrink-0 text-training-ink" aria-hidden="true" />
      <div className="flex flex-col gap-0.5">
        <span className="text-copy font-semibold text-ink">{title}</span>
        <span className="text-body text-ink-2">{[scope, claimBy, note].filter(Boolean).join(' ')}</span>
      </div>
    </div>
  );
}
