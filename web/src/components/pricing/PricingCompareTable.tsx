import { useTranslation } from 'react-i18next';
import { Check, X } from 'lucide-react';
import type { PublicSubscriptionPlanDto } from '@/api/generated';
import {
  PRICING_COMPARE_ID,
  PRICING_FEATURES,
  PRICING_FEATURE_COPY,
} from '@/components/pricing/pricingFeatures';
import { formatMoney } from '@/components/subscription/money';
import { coachPlanName } from '@/components/subscription/planNames';

interface Props {
  plans: PublicSubscriptionPlanDto[];
}

/** Compare table: one column per plan; rows are the client cap, the second role and every feature. */
export default function PricingCompareTable({ plans }: Props) {
  const { t, i18n } = useTranslation();

  const secondRole = (plan: PublicSubscriptionPlanDto): string => {
    const addOn = plan.secondRoleAddOnPriceMinorUnits;
    if (addOn !== undefined && addOn !== null) {
      return `+${formatMoney(addOn, plan.currency ?? 'CZK', i18n.language)}`;
    }
    return plan.bothRolesIncluded ? t('pricing.table.included') : '–';
  };

  return (
    <section aria-labelledby="pricing-compare-title" className="flex flex-col gap-4">
      <h2 id="pricing-compare-title" className="font-display text-display font-semibold text-ink">
        {t('pricing.table.title')}
      </h2>
      <div
        id={PRICING_COMPARE_ID}
        tabIndex={-1}
        className="scroll-mt-6 overflow-x-auto rounded-2xl border border-line bg-card shadow-panel outline-none"
      >
        <table className="w-full min-w-160 border-collapse text-left">
          <thead>
            <tr className="bg-sunken">
              <th scope="col" className="px-5 py-4 text-body font-medium text-muted-foreground">
                {t('pricing.table.feature')}
              </th>
              {plans.map((plan) => (
                <th key={plan.code} scope="col" className="px-3 py-4 text-center text-copy font-semibold text-ink">
                  {coachPlanName(plan, i18n.language)}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            <tr className="border-t border-line">
              <th scope="row" className="px-5 py-3.5 text-copy font-semibold text-ink">
                {t('pricing.table.clients')}
              </th>
              {plans.map((plan) => (
                <td key={plan.code} className="px-3 py-3.5 text-center text-copy font-semibold text-ink">
                  {plan.maxActiveClients === undefined || plan.maxActiveClients === null
                    ? t('pricing.table.unlimited')
                    : plan.maxActiveClients}
                </td>
              ))}
            </tr>
            <tr className="border-t border-line">
              <th scope="row" className="px-5 py-3.5 text-copy font-semibold text-ink">
                {t('pricing.table.secondRole')}
              </th>
              {plans.map((plan) => (
                <td key={plan.code} className="px-3 py-3.5 text-center text-body text-ink-2">
                  {secondRole(plan)}
                </td>
              ))}
            </tr>
            {PRICING_FEATURES.map(({ flag, key }) => (
              <tr key={key} data-testid="pricing-feature-row" className="border-t border-line">
                <th scope="row" className="px-5 py-3 font-normal">
                  <span className="block text-copy font-semibold text-ink">{t(PRICING_FEATURE_COPY[key].label)}</span>
                  <span className="block text-body text-muted-foreground">{t(PRICING_FEATURE_COPY[key].hint)}</span>
                </th>
                {plans.map((plan) => {
                  const included = plan[flag] === true;
                  return (
                    <td key={plan.code} className="px-3 py-3 text-center">
                      {included ? (
                        <Check className="mx-auto size-4 text-success" aria-hidden="true" />
                      ) : (
                        <X className="mx-auto size-4 text-muted-foreground" aria-hidden="true" />
                      )}
                      <span className="sr-only">
                        {included ? t('pricing.table.yes') : t('pricing.table.no')}
                      </span>
                    </td>
                  );
                })}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}
