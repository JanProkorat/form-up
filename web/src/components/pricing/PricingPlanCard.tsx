import { Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { Users } from 'lucide-react';
import { BillingInterval, type PublicSubscriptionPlanDto } from '@/api/generated';
import { scrollToAnchor } from '@/components/home/smoothAnchor';
import { PRICING_COMPARE_ID, PRICING_FEATURES, includedFeatureCount } from '@/components/pricing/pricingFeatures';
import { formatMoney } from '@/components/subscription/money';
import { coachPlanName } from '@/components/subscription/planNames';
import { Button } from '@/components/ui/button';

interface Props {
  plan: PublicSubscriptionPlanDto;
  /** Where the button leads: registration when signed out, Plan and billing when signed in. */
  ctaTo: string;
}

/** One plan of the public pricing page: name, price, second-role chip, choose link and a feature count. */
export default function PricingPlanCard({ plan, ctaTo }: Props) {
  const { t, i18n } = useTranslation();
  const name = coachPlanName(plan, i18n.language);
  const currency = plan.currency ?? 'CZK';
  const priceMinor = plan.priceMinorUnits ?? 0;
  const price = formatMoney(priceMinor, currency, i18n.language);
  const interval = plan.billingInterval === BillingInterval.Annual ? 'year' : 'month';
  const addOn = plan.secondRoleAddOnPriceMinorUnits;
  const chip =
    addOn !== undefined && addOn !== null
      ? t('pricing.card.secondRoleAddOn', { amount: formatMoney(addOn, currency, i18n.language) })
      : plan.bothRolesIncluded
        ? t('pricing.card.bothRoles')
        : null;
  const cap = plan.maxActiveClients;
  const popular = plan.isMostPopular === true;

  return (
    <article
      aria-label={name}
      className={`flex flex-col gap-4 rounded-2xl bg-card p-5 shadow-panel ${popular ? 'border-2 border-ink' : 'border border-line'}`}
    >
      <div className="flex items-center gap-2">
        <h2 className="text-card-title font-semibold text-ink">{name}</h2>
        {popular && (
          <span className="ml-auto rounded-full bg-success-soft px-2.5 py-0.5 text-body font-semibold text-success-ink">
            {t('pricing.card.mostPopular')}
          </span>
        )}
      </div>

      <div className="flex items-baseline gap-2">
        <span className="text-stat font-semibold text-ink">{price}</span>
        <span className="text-body text-muted-foreground">{t(`pricing.card.per.${interval}`)}</span>
      </div>

      {/* One line is reserved for the chip so the buttons line up across the cards. */}
      <div className="min-h-10">
        {chip && (
          <div className="flex items-center gap-2 rounded-lg bg-sunken px-3 py-2.5 text-body text-ink-2">
            <Users className="size-4 shrink-0" aria-hidden="true" />
            <span>{chip}</span>
          </div>
        )}
      </div>

      <Button asChild variant={popular ? 'default' : 'outline'} size="lg" className="px-3.5 font-semibold">
        <Link to={ctaTo}>
          {priceMinor === 0 ? t('pricing.card.startFree') : t('pricing.card.choose', { plan: name })}
        </Link>
      </Button>

      <div className="h-px bg-line" />

      <div className="flex items-center gap-2 text-subhead font-semibold text-ink">
        <Users className="size-4.5 shrink-0" aria-hidden="true" />
        {cap === undefined || cap === null
          ? t('pricing.card.unlimitedClients')
          : t('pricing.card.clientCap', { count: cap })}
      </div>

      <p className="text-body text-muted-foreground">
        {t('pricing.card.featureCount', { count: includedFeatureCount(plan), total: PRICING_FEATURES.length })}
        {' · '}
        <a
          href={`#${PRICING_COMPARE_ID}`}
          onClick={scrollToAnchor}
          className="font-semibold text-ink underline underline-offset-2"
        >
          {t('pricing.card.seeAll')}
          <span className="sr-only"> {name}</span>
        </a>
      </p>
    </article>
  );
}
