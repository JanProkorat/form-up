import { Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { Users } from 'lucide-react';
import {
  BillingInterval,
  PromotionDuration,
  type PublicPromotionDto,
  type PublicSubscriptionPlanDto,
} from '@/api/generated';
import { scrollToAnchor } from '@/components/home/smoothAnchor';
import { PRICING_COMPARE_ID, PRICING_FEATURES, includedFeatureCount } from '@/components/pricing/pricingFeatures';
import { formatMoney } from '@/components/subscription/money';
import { coachPlanName } from '@/components/subscription/planNames';
import {
  effectiveInterval,
  planDiscountedPriceFor,
  planPriceFor,
  secondRoleAddOnFor,
} from '@/components/subscription/planPricing';
import { promotionShortText } from '@/components/subscription/promotionText';
import { Button } from '@/components/ui/button';

interface Props {
  plan: PublicSubscriptionPlanDto;
  /** The active automatic promotion, when there is one; the price row then reserves room for its note on every card. */
  promotion?: PublicPromotionDto;
  /** The interval the coach picked on the switch; a paid plan without an annual price keeps showing its monthly one. */
  interval?: BillingInterval;
  /** Where the button leads: registration when signed out, Plan and billing when signed in. */
  ctaTo: string;
}

/** One plan of the public pricing page: name, price, second-role chip, choose link and a feature count. */
export default function PricingPlanCard({ plan, promotion, interval: chosen = BillingInterval.Monthly, ctaTo }: Props) {
  const { t, i18n } = useTranslation();
  const name = coachPlanName(plan, i18n.language);
  const currency = plan.currency ?? 'CZK';
  const interval = effectiveInterval(plan, chosen);
  const annual = interval === BillingInterval.Annual;
  const priceMinor = plan.priceMinorUnits ?? 0;
  const shownMinor = planPriceFor(plan, interval, false);
  const price = formatMoney(shownMinor, currency, i18n.language);
  const discounted = planDiscountedPriceFor(plan, interval, false);
  const offer = promotion && discounted !== null && discounted < shownMinor ? promotion : null;
  const shortOffer = offer ? promotionShortText(t, offer, i18n.language) : null;
  const offerNote = offer
    ? annual
      ? offer.duration === PromotionDuration.Forever
        ? shortOffer
        : t('pricing.card.thenAnnual', { price })
      : offer.duration === PromotionDuration.Forever
        ? shortOffer
        : t('subscription.promotion.card.then', { short: shortOffer, price })
    : undefined;
  const per = annual ? 'year' : 'month';
  const addOn = secondRoleAddOnFor(plan, interval);
  const chip =
    addOn !== null
      ? t('pricing.card.secondRoleAddOn', { amount: formatMoney(addOn, currency, i18n.language) })
      : plan.bothRolesIncluded
        ? t('pricing.card.bothRoles')
        : null;
  const cap = plan.maxActiveClients;
  const popular = plan.isMostPopular === true;

  return (
    <article
      aria-label={name}
      // Seven rows shared with the sibling cards (CSS subgrid), so the buttons line up without reserved space.
      className={`row-span-7 grid grid-rows-subgrid gap-4 rounded-2xl bg-card p-5 shadow-panel ${popular ? 'border-2 border-ink' : 'border border-line'}`}
    >
      <div className="flex items-center gap-2">
        <h2 className="text-card-title font-semibold text-ink">{name}</h2>
        {popular && (
          <span className="ml-auto rounded-full bg-success-soft px-2.5 py-0.5 text-body font-semibold text-success-ink">
            {t('pricing.card.mostPopular')}
          </span>
        )}
      </div>

      {promotion ? (
        <div className="@container flex flex-col gap-1">
          <div className={`flex ${annual ? 'flex-nowrap' : 'flex-wrap'} items-baseline gap-x-1.5 text-stat`}>
            <span className="text-stat font-semibold whitespace-nowrap text-ink @max-[15rem]:text-auth-title">
              {formatMoney(offer && discounted !== null ? discounted : shownMinor, currency, i18n.language)}
            </span>
            {offer && !annual && (
              <s className="text-copy whitespace-nowrap text-muted-foreground @max-[15rem]:text-body">
                <span className="sr-only">{t('subscription.promotion.card.regularPrice')} </span>
                {price}
              </s>
            )}
            <span className="text-body whitespace-nowrap text-muted-foreground">{t(`pricing.card.per.${per}`)}</span>
          </div>
          {offerNote && <span className="text-meta text-muted-foreground">{offerNote}</span>}
        </div>
      ) : (
        <div className="flex items-baseline gap-2">
          <span className="font-display text-price font-semibold tracking-heading text-ink">{price}</span>
          <span className="text-body text-muted-foreground">{t(`pricing.card.per.${per}`)}</span>
        </div>
      )}

      <div>
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
