import { useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { ChevronRight, Eye, Plus, Tag } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { PaymentCurrency, type CoachPackageDto } from '@/api/coach-packages';
import type { CoachDiscountDto } from '@/api/coach-discounts';
import {
  getErrorCode,
  getErrorStatus,
  getRfc7807ErrorCode,
  showApiError,
  showSuccess,
} from '@/lib/api-errors';
import { isSubscriptionRequired } from '@/lib/subscriptionErrors';
import { useSubscription } from '@/hooks/useSubscriptionQueries';
import { billingSuffix } from '@/components/client-detail/payment/paymentModel';
import { formatMoney } from '@/lib/money';
import PaywallPanel from '@/components/subscription/PaywallPanel';
import DiscountDrawer from '@/components/package-catalog/DiscountDrawer';
import DiscountsTable from '@/components/package-catalog/DiscountsTable';
import PackageDrawer from '@/components/package-catalog/PackageDrawer';
import PackagesTable from '@/components/package-catalog/PackagesTable';
import { discountValuesToRequest, type DiscountFormValues } from '@/components/package-catalog/discountForm';
import { formValuesToRequest, type PackageFormValues } from '@/components/package-catalog/packageForm';
import type { SaveOutcome } from '@/components/package-catalog/packageFormParts';
import {
  useArchiveDiscount,
  useArchivePackage,
  useCoachDiscounts,
  useCoachPackages,
  useCreateDiscount,
  useCreatePackage,
  useProfileFromPrice,
  useUpdateDiscount,
  useUpdatePackage,
} from '@/components/package-catalog/usePackagesQueries';

type TabValue = 'packages' | 'discounts';

const NOT_ENTITLED = 'PAYMENTS_NOT_ENTITLED';
const ERROR_PACKAGE_ARCHIVED = 'PACKAGE_ARCHIVED';
const ERROR_DISCOUNT_ARCHIVED = 'DISCOUNT_ARCHIVED';
const ERROR_CURRENCY_LOCKED = 'CURRENCY_LOCKED';

const errorCodeOf = (error: unknown): string | null => getErrorCode(error) ?? getRfc7807ErrorCode(error);

/** True when the coach's plan does not include payments: the API answers 403 with this code. */
function isNotEntitled(error: unknown): boolean {
  return getErrorStatus(error) === 403 && errorCodeOf(error) === NOT_ENTITLED;
}

type ArchiveTarget = { kind: 'package' | 'discount'; id: string; name: string };

/** Business → Packages: the coach's packages and discounts, with create, edit and archive. */
export default function PackagesPage() {
  const { t, i18n } = useTranslation();
  const [searchParams, setSearchParams] = useSearchParams();
  const tab: TabValue = searchParams.get('tab') === 'discounts' ? 'discounts' : 'packages';

  const { isEntitled } = useSubscription();
  // isEntitled stays true while the plan loads; a refusal from the server locks the page too.
  const planAllows = isEntitled('canUsePackages');
  const packagesQuery = useCoachPackages(planAllows);
  const discountsQuery = useCoachDiscounts(planAllows);
  const fromPriceQuery = useProfileFromPrice();
  const createPackage = useCreatePackage();
  const updatePackage = useUpdatePackage();
  const archivePackage = useArchivePackage();
  const createDiscount = useCreateDiscount();
  const updateDiscount = useUpdateDiscount();
  const archiveDiscount = useArchiveDiscount();

  const [packageDrawer, setPackageDrawer] = useState<{ item?: CoachPackageDto } | null>(null);
  const [discountDrawer, setDiscountDrawer] = useState<{ item?: CoachDiscountDto } | null>(null);
  const [archiveTarget, setArchiveTarget] = useState<ArchiveTarget | null>(null);

  const packages = packagesQuery.data ?? [];
  const activePackages = packages.filter((item) => item.archivedAt == null);
  const discounts = discountsQuery.data ?? [];

  const activeQuery = tab === 'packages' ? packagesQuery : discountsQuery;
  const paywalled =
    !planAllows || (activeQuery.isError && (isNotEntitled(activeQuery.error) || isSubscriptionRequired(activeQuery.error)));

  const handleSavePackage = async (values: PackageFormValues): Promise<SaveOutcome> => {
    const request = formValuesToRequest(values);
    const packageId = packageDrawer?.item?.packageId;
    try {
      if (packageId) {
        await updatePackage.mutateAsync({ packageId, request });
      } else {
        await createPackage.mutateAsync(request);
      }
      showSuccess('packages.saveSuccess');
      setPackageDrawer(null);
      return 'saved';
    } catch (error) {
      const code = errorCodeOf(error);
      showApiError(error, 'packages.saveError');
      if (code === ERROR_PACKAGE_ARCHIVED) {
        // The package was archived elsewhere: the list refetches, so close the stale drawer.
        setPackageDrawer(null);
      }
      return code === ERROR_CURRENCY_LOCKED ? 'currencyLocked' : 'error';
    }
  };

  const handleSaveDiscount = async (values: DiscountFormValues): Promise<SaveOutcome> => {
    const request = discountValuesToRequest(values);
    const discountId = discountDrawer?.item?.discountId;
    try {
      if (discountId) {
        await updateDiscount.mutateAsync({ discountId, request });
      } else {
        await createDiscount.mutateAsync(request);
      }
      showSuccess('packages.discounts.saveSuccess');
      setDiscountDrawer(null);
      return 'saved';
    } catch (error) {
      const code = errorCodeOf(error);
      showApiError(error, 'packages.discounts.saveError');
      if (code === ERROR_DISCOUNT_ARCHIVED) {
        setDiscountDrawer(null);
      }
      return code === ERROR_CURRENCY_LOCKED ? 'currencyLocked' : 'error';
    }
  };

  const handleArchive = () => {
    if (!archiveTarget) {
      return;
    }
    const { kind, id } = archiveTarget;
    const mutation = kind === 'package' ? archivePackage : archiveDiscount;
    mutation.mutate(id, {
      onSuccess: () => showSuccess(kind === 'package' ? 'packages.archiveSuccess' : 'packages.discounts.archiveSuccess'),
      onError: (error) => showApiError(error, kind === 'package' ? 'packages.archiveError' : 'packages.discounts.archiveError'),
      onSettled: () => setArchiveTarget(null),
    });
  };

  // Hidden while loading and when the profile cannot be read; the server derives the value.
  const fromPrice = fromPriceQuery.data;
  const isDiscountTab = tab === 'discounts';

  return (
    <div className="flex h-full flex-col gap-4">
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div className="flex flex-col gap-1.5">
          <span className="flex items-center gap-1.75 text-label font-semibold tracking-label text-marker-text uppercase">
            <span className="size-1.75 rounded-full bg-marker" aria-hidden="true" />
            {t('packages.eyebrow')}
          </span>
          <h1 className="text-display font-semibold text-ink">{t('packages.title')}</h1>
          <p className="text-copy text-muted-foreground">{t('packages.subtitle')}</p>
        </div>
        {!paywalled && (
          <Button
            type="button"
            size="lg"
            className="gap-1.75 px-3.5 font-semibold"
            onClick={() => (isDiscountTab ? setDiscountDrawer({}) : setPackageDrawer({}))}
          >
            <Plus aria-hidden="true" />
            {isDiscountTab ? t('packages.discounts.newDiscount') : t('packages.newPackage')}
          </Button>
        )}
      </div>

      <Tabs
        value={tab}
        onValueChange={(value) => setSearchParams(value === 'discounts' ? { tab: 'discounts' } : {}, { replace: true })}
        className="min-h-0 flex-1 gap-4"
      >
        <TabsList variant="underline">
          <TabsTrigger variant="underline" value="packages">
            {t('packages.tabs.packages')}
          </TabsTrigger>
          <TabsTrigger variant="underline" value="discounts">
            {t('packages.tabs.discounts')}
          </TabsTrigger>
        </TabsList>

        <TabsContent value="packages" className="flex flex-col gap-4">
          {paywalled ? (
            <PackagesPaywall />
          ) : (
            <>
              <div className="flex min-h-0 flex-col overflow-hidden rounded-2xl border border-line bg-card shadow-panel">
                <div className="min-h-0 overflow-y-auto [&_[data-slot=table-container]]:overflow-visible">
                  <PackagesTable
                    packages={packages}
                    isPending={packagesQuery.isPending}
                    isError={packagesQuery.isError}
                    onRetry={() => void packagesQuery.refetch()}
                    onEdit={(item) => setPackageDrawer({ item })}
                    onArchive={(item) =>
                      setArchiveTarget({ kind: 'package', id: item.packageId ?? '', name: item.name ?? '' })
                    }
                  />
                </div>
                {!packagesQuery.isPending && !packagesQuery.isError && (
                  <p className="border-t border-line px-4 py-3 text-meta text-muted-foreground">
                    {t('packages.footer', { count: packages.length })}
                  </p>
                )}
              </div>
              {fromPrice && (
                <div className="flex items-center gap-4 rounded-2xl border border-line bg-card px-5 py-4">
                  <span className="flex size-11 shrink-0 items-center justify-center rounded-xl bg-sunken text-ink-2">
                    <Eye className="size-4.5" aria-hidden="true" />
                  </span>
                  <div className="flex min-w-0 flex-1 flex-col">
                    <span className="text-copy font-semibold text-ink">
                      {fromPrice.price == null
                        ? t('packages.profileCard.emptyTitle')
                        : t('packages.profileCard.title', {
                            price: formatMoney(
                              fromPrice.price,
                              fromPrice.currency ?? PaymentCurrency.CZK,
                              i18n.language,
                            ),
                            unit: billingSuffix(t, fromPrice.billing, fromPrice.intervalWeeks),
                          })}
                    </span>
                    <span className="text-body text-muted-foreground">
                      {fromPrice.price == null ? t('packages.profileCard.emptyBody') : t('packages.profileCard.body')}
                    </span>
                  </div>
                  <Link to="/profile" className="flex items-center gap-1 text-copy font-semibold text-ink">
                    {t('packages.profileCard.open')}
                    <ChevronRight className="size-4" aria-hidden="true" />
                  </Link>
                </div>
              )}
            </>
          )}
        </TabsContent>

        <TabsContent value="discounts" className="flex flex-col gap-4">
          {paywalled ? (
            <PackagesPaywall />
          ) : (
            <>
              <div className="flex min-h-0 flex-col overflow-hidden rounded-2xl border border-line bg-card shadow-panel">
                <div className="min-h-0 overflow-y-auto [&_[data-slot=table-container]]:overflow-visible">
                  <DiscountsTable
                    discounts={discounts}
                    packages={packages}
                    isPending={discountsQuery.isPending}
                    isError={discountsQuery.isError}
                    onRetry={() => void discountsQuery.refetch()}
                    onEdit={(item) => setDiscountDrawer({ item })}
                    onArchive={(item) =>
                      setArchiveTarget({ kind: 'discount', id: item.discountId ?? '', name: item.name ?? '' })
                    }
                  />
                </div>
                {!discountsQuery.isPending && !discountsQuery.isError && (
                  <p className="border-t border-line px-4 py-3 text-meta text-muted-foreground">
                    {t('packages.discounts.footer')}
                  </p>
                )}
              </div>
              <div className="flex items-center gap-4 rounded-2xl border border-line bg-card px-5 py-4">
                <span className="flex size-11 shrink-0 items-center justify-center rounded-xl bg-sunken text-ink-2">
                  <Tag className="size-4.5" aria-hidden="true" />
                </span>
                <div className="flex min-w-0 flex-1 flex-col">
                  <span className="flex items-center gap-2 text-copy font-semibold text-ink">
                    {t('packages.discounts.codes.title')}
                    <span className="rounded-full border border-line px-2.5 py-0.5 text-meta font-medium text-ink-2">
                      {t('packages.discounts.codes.comingLater')}
                    </span>
                  </span>
                  <span className="text-body text-muted-foreground">{t('packages.discounts.codes.body')}</span>
                </div>
                <Button type="button" variant="outline" disabled>
                  <Plus aria-hidden="true" />
                  {t('packages.discounts.codes.create')}
                </Button>
              </div>
            </>
          )}
        </TabsContent>
      </Tabs>

      {packageDrawer && (
        <PackageDrawer
          open
          onOpenChange={(open) => {
            if (!open) {
              setPackageDrawer(null);
            }
          }}
          item={packageDrawer.item}
          onSubmit={handleSavePackage}
          isSaving={createPackage.isPending || updatePackage.isPending}
        />
      )}

      {discountDrawer && (
        <DiscountDrawer
          open
          onOpenChange={(open) => {
            if (!open) {
              setDiscountDrawer(null);
            }
          }}
          item={discountDrawer.item}
          packages={activePackages}
          onSubmit={handleSaveDiscount}
          isSaving={createDiscount.isPending || updateDiscount.isPending}
        />
      )}

      <Dialog open={archiveTarget !== null} onOpenChange={(open) => !open && setArchiveTarget(null)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>
              {archiveTarget?.kind === 'discount'
                ? t('packages.discounts.archiveDialog.title')
                : t('packages.archiveDialog.title')}
            </DialogTitle>
            <DialogDescription>
              {archiveTarget?.kind === 'discount'
                ? t('packages.discounts.archiveDialog.description', { name: archiveTarget.name })
                : t('packages.archiveDialog.description', { name: archiveTarget?.name ?? '' })}
            </DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => setArchiveTarget(null)}>
              {t('common.cancel')}
            </Button>
            <Button
              type="button"
              variant="destructive"
              disabled={archivePackage.isPending || archiveDiscount.isPending}
              onClick={handleArchive}
            >
              {t('packages.archiveDialog.confirm')}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}

function PackagesPaywall() {
  return <PaywallPanel feature="packages" />;
}
