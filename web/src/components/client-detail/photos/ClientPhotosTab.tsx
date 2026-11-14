import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Calendar, Camera, ChevronDown, Columns2, Info } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuRadioGroup,
  DropdownMenuRadioItem,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu';
import type { GetClientDashboardResponse } from '@/api/generated';
import type { ClientPlanItem } from '@/api/client-plans';
import { isSubscriptionRequired } from '@/lib/subscriptionErrors';
import PaywallPanel from '@/components/subscription/PaywallPanel';
import PhotoCard from '@/components/client-detail/photos/PhotoCard';
import PhotoCompareDialog from '@/components/client-detail/photos/PhotoCompareDialog';
import PhotoRequestBanner from '@/components/client-detail/photos/PhotoRequestBanner';
import PhotoViewerDialog from '@/components/client-detail/photos/PhotoViewerDialog';
import RequestPhotosDrawer from '@/components/client-detail/photos/RequestPhotosDrawer';
import {
  DEFAULT_RANGE,
  RANGE_OPTIONS,
  formatMonthKey,
  photoPlanInfo,
  type PhotoRange,
  type PlanCapabilities,
} from '@/components/client-detail/photos/photosModel';
import { hasPhotoForRequest } from '@/components/client-detail/photos/photoRequestModel';
import { useClientPhotos } from '@/components/client-detail/photos/useClientPhotos';
import { useOpenPhotoRequest } from '@/components/client-detail/photos/useOpenPhotoRequest';

interface Props {
  clientId: string;
  dashboard: GetClientDashboardResponse;
  /** The client's plans (page-level query); gives the week a photo was taken in. */
  plans: ClientPlanItem[] | undefined;
}

type Overlay = { kind: 'viewer'; photoId: string } | { kind: 'compare'; photoIds: string[] } | null;

/**
 * Photos tab of the client detail page: a month-grouped gallery of the
 * client's progress photos with a photo viewer and a side-by-side compare.
 * The server decides which photos this coach may see; nothing is filtered here.
 */
export default function ClientPhotosTab({ clientId, dashboard, plans }: Props) {
  const { t, i18n } = useTranslation();
  const locale = i18n.language;
  const [range, setRange] = useState<PhotoRange>(DEFAULT_RANGE);
  const [overlay, setOverlay] = useState<Overlay>(null);
  const [requestOpen, setRequestOpen] = useState(false);
  const openRequestQuery = useOpenPhotoRequest(clientId);
  const { query, groups, photos, refreshUrls } = useClientPhotos(clientId, range);

  const name = dashboard.firstName?.trim() || t('clientDetail.photos.clientFallback');
  const canViewNutritionPlans = dashboard.canViewNutritionPlans === true;

  const canViewTrainingPlans = dashboard.canViewTrainingPlans === true;
  const capabilities = useMemo<PlanCapabilities>(
    () => ({ nutrition: canViewNutritionPlans, training: canViewTrainingPlans }),
    [canViewNutritionPlans, canViewTrainingPlans],
  );
  const planInfo = useMemo(
    () => new Map(photos.map((photo) => [photo.id, photoPlanInfo(photo, plans, capabilities)] as const)),
    [photos, plans, capabilities],
  );

  if (query.isPending) {
    return (
      <div className="flex flex-col gap-4">
        <Skeleton className="h-9 w-full" />
        <div className="grid grid-cols-[repeat(auto-fill,minmax(10rem,1fr))] gap-3.5">
          {Array.from({ length: 6 }, (_, index) => (
            <Skeleton key={index} className="aspect-[3/4] w-full" />
          ))}
        </div>
      </div>
    );
  }

  if (query.isError && isSubscriptionRequired(query.error)) {
    return <PaywallPanel feature="progressPhotos" />;
  }

  if (query.isError) {
    return (
      <div className="flex flex-col items-center gap-3 py-16">
        <p className="text-body text-muted-foreground">{t('common.dataLoadError')}</p>
        <Button type="button" variant="outline" onClick={() => void query.refetch()}>
          {t('clients.retry')}
        </Button>
      </div>
    );
  }

  const openRequest = openRequestQuery.data ?? null;
  // A failed lookup with nothing cached must not look like "no open request" (the drawer would offer the create form).
  const requestLookupFailed = openRequestQuery.isError && openRequestQuery.data === undefined;

  const requestButton = (
    <Button type="button" size="lg" onClick={() => setRequestOpen(true)}>
      <Camera aria-hidden="true" />
      {t('clientDetail.photos.request')}
    </Button>
  );

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
        <h2 className="text-card-title font-bold text-ink">{t('clientDetail.photos.heading')}</h2>
        <span className="text-body text-muted-foreground">
          {t('clientDetail.photos.count', { count: photos.length })}
        </span>
        <div className="ml-auto flex flex-wrap items-center gap-2">
          <DropdownMenu>
            <DropdownMenuTrigger asChild>
              <Button type="button" variant="outline" size="lg">
                <Calendar aria-hidden="true" />
                {t(`clientDetail.photos.range.${range}`)}
                <ChevronDown aria-hidden="true" />
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent align="end">
              <DropdownMenuRadioGroup value={range} onValueChange={(value) => setRange(value as PhotoRange)}>
                {RANGE_OPTIONS.map((option) => (
                  <DropdownMenuRadioItem key={option} value={option}>
                    {t(`clientDetail.photos.range.${option}`)}
                  </DropdownMenuRadioItem>
                ))}
              </DropdownMenuRadioGroup>
            </DropdownMenuContent>
          </DropdownMenu>
          <Button
            type="button"
            variant="outline"
            size="lg"
            disabled={photos.length < 2}
            onClick={() => setOverlay({ kind: 'compare', photoIds: [] })}
          >
            <Columns2 aria-hidden="true" />
            {t('clientDetail.photos.compare.open')}
          </Button>
          {requestButton}
        </div>
      </div>

      <p className="flex items-center gap-2 text-body text-muted-foreground">
        <Info className="size-4 shrink-0" aria-hidden="true" />
        {t('clientDetail.photos.hint', { name })}
      </p>

      {openRequest && (
        <PhotoRequestBanner request={openRequest} name={name} waiting={!hasPhotoForRequest(photos, openRequest.id)} />
      )}

      {photos.length === 0 ? (
        <div
          role="status"
          className="flex flex-col items-center gap-3 rounded-2xl border border-border bg-card px-6 py-16 text-center"
        >
          <Camera className="size-8 text-muted-foreground" aria-hidden="true" />
          <h3 className="text-card-title font-bold text-ink">{t('clientDetail.photos.emptyTitle')}</h3>
          <p className="max-w-md text-body text-muted-foreground">{t('clientDetail.photos.emptyBody', { name })}</p>
          {requestButton}
        </div>
      ) : (
        <div className="flex flex-col gap-6">
          {(groups ?? []).map((group) => (
            <section key={group.yearMonth} aria-label={formatMonthKey(group.yearMonth, locale)} className="flex flex-col gap-3">
              <div className="flex items-baseline gap-2.5">
                <h3 className="text-card-title font-bold text-ink">{formatMonthKey(group.yearMonth, locale)}</h3>
                <span className="text-body text-muted-foreground">
                  {t('clientDetail.photos.count', { count: group.photos?.length ?? 0 })}
                </span>
              </div>
              <ul className="grid grid-cols-[repeat(auto-fill,minmax(10rem,1fr))] gap-3.5">
                {(group.photos ?? []).map((photo) => (
                  <PhotoCard
                    key={photo.id}
                    photo={photo}
                    plan={planInfo.get(photo.id)}
                    locale={locale}
                    onOpen={() => photo.id && setOverlay({ kind: 'viewer', photoId: photo.id })}
                    onExpired={refreshUrls}
                  />
                ))}
              </ul>
            </section>
          ))}
        </div>
      )}

      <RequestPhotosDrawer
        open={requestOpen}
        onOpenChange={setRequestOpen}
        clientId={clientId}
        name={name}
        plans={plans}
        openRequest={openRequestQuery.isPending || requestLookupFailed ? undefined : openRequest}
        lookupFailed={requestLookupFailed}
        onRetryLookup={() => void openRequestQuery.refetch()}
      />

      {overlay?.kind === 'viewer' && (
        <PhotoViewerDialog
          clientId={clientId}
          photos={photos}
          photoId={overlay.photoId}
          plans={plans}
          capabilities={capabilities}
          locale={locale}
          onNavigate={(photoId) => setOverlay({ kind: 'viewer', photoId })}
          onCompare={(photoId) => setOverlay({ kind: 'compare', photoIds: [photoId] })}
          onExpired={refreshUrls}
          onClose={() => setOverlay(null)}
        />
      )}

      {overlay?.kind === 'compare' && (
        <PhotoCompareDialog
          clientId={clientId}
          photos={photos}
          initialIds={overlay.photoIds}
          plans={plans}
          capabilities={capabilities}
          locale={locale}
          onExpired={refreshUrls}
          onClose={() => setOverlay(null)}
        />
      )}
    </div>
  );
}
