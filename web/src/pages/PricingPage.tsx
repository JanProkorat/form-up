import { useTranslation } from 'react-i18next';
import HomeFooter from '@/components/home/HomeFooter';
import HomeNav from '@/components/home/HomeNav';
import PricingCompareTable from '@/components/pricing/PricingCompareTable';
import PricingPlanCard from '@/components/pricing/PricingPlanCard';
import { PLAN_BILLING_PATH } from '@/components/subscription/planLinks';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { usePublicPlans } from '@/hooks/useSubscriptionQueries';
import { useAuthStore } from '@/stores/auth';

const REGISTER_PATH = '/register';

/**
 * Public pricing page ("/pricing"): the plans the backend offers, as cards and a feature compare table.
 * Top-level route outside the entry layout, so a signed-in coach opening it from an email still sees it.
 */
export default function PricingPage() {
  const { t } = useTranslation();
  const isAuthenticated = useAuthStore((s) => s.isAuthenticated);
  const plansQuery = usePublicPlans();
  const plans = plansQuery.data?.plans ?? [];
  const ctaTo = isAuthenticated ? PLAN_BILLING_PATH : REGISTER_PATH;

  return (
    <div className="home-page-wash isolate flex min-h-svh flex-col bg-background">
      <HomeNav />
      <main className="mx-auto flex w-full max-w-7xl flex-1 flex-col gap-10 px-4 pb-16 sm:px-10 lg:px-16">
        <div className="flex flex-col items-center gap-3 pt-8 text-center">
          <h1 className="font-display text-home-heading font-semibold text-ink">{t('pricing.title')}</h1>
          <p className="max-w-2xl text-home-sub text-ink-2">{t('pricing.subtitle')}</p>
        </div>

        {plansQuery.isError ? (
          <div role="alert" className="flex flex-col items-center gap-3 text-center">
            <p className="text-copy text-error">{t('pricing.loadError')}</p>
            <Button
              type="button"
              variant="outline"
              size="lg"
              className="px-3.5 font-semibold"
              onClick={() => void plansQuery.refetch()}
            >
              {t('pricing.retry')}
            </Button>
          </div>
        ) : plansQuery.isPending ? (
          <div className="flex flex-col gap-10" aria-busy="true">
            <div className="grid grid-cols-[repeat(auto-fit,minmax(16rem,1fr))] gap-4">
              {[0, 1, 2, 3].map((index) => (
                <Skeleton key={index} className="h-80 w-full rounded-2xl" />
              ))}
            </div>
            <Skeleton className="h-96 w-full rounded-2xl" />
          </div>
        ) : plans.length === 0 ? (
          <p className="text-center text-copy text-muted-foreground">{t('pricing.empty')}</p>
        ) : (
          <>
            <div className="grid grid-cols-[repeat(auto-fit,minmax(16rem,1fr))] items-stretch gap-4">
              {plans.map((plan) => (
                <PricingPlanCard key={plan.code} plan={plan} ctaTo={ctaTo} />
              ))}
            </div>
            <PricingCompareTable plans={plans} />
          </>
        )}
      </main>
      <HomeFooter />
    </div>
  );
}
