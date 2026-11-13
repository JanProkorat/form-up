import { useTranslation } from 'react-i18next';
import { useIsMutating, useMutation, useMutationState, useQuery, useQueryClient } from '@tanstack/react-query';
import { Check, Loader2, TriangleAlert } from 'lucide-react';
import { getMyProfile, profileKeys, updateMyTimeZone } from '@/api/profile';
import { showApiError } from '@/lib/api-errors';
import { useAuthStore } from '@/stores/auth';
import AccountCard from '@/components/settings/AccountCard';
import DangerZoneCard from '@/components/settings/DangerZoneCard';
import NotificationsCard from '@/components/settings/NotificationsCard';
import PaymentsCard from '@/components/settings/payments/PaymentsCard';
import PlanBillingCard from '@/components/settings/PlanBillingCard';
import RolesCard from '@/components/settings/RolesCard';
import { Skeleton } from '@/components/ui/skeleton';

export default function SettingsPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const email = useAuthStore((s) => s.user?.email ?? '');
  const meQuery = useQuery({ queryKey: profileKeys.me, queryFn: getMyProfile });

  const timeZoneMutation = useMutation({
    mutationKey: ['settings', 'timeZone'],
    mutationFn: updateMyTimeZone,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: profileKeys.me }),
    onError: (error) => showApiError(error, 'settings.account.timeZoneError'),
  });

  // While a save is in flight the select shows the pending value; on failure it falls back to the stored one.
  const timeZone = timeZoneMutation.isPending
    ? (timeZoneMutation.variables ?? '')
    : (meQuery.data?.timeZone ?? '');

  // Every Settings save registers under ['settings', ...]; the status line reflects all of them.
  const savingCount = useIsMutating({ mutationKey: ['settings'] });
  const lastSettled = useMutationState({
    filters: { mutationKey: ['settings'] },
    select: (m) => ({ status: m.state.status, submittedAt: m.state.submittedAt }),
  })
    .filter((m) => m.status === 'success' || m.status === 'error')
    .sort((a, b) => b.submittedAt - a.submittedAt)[0];
  const status = savingCount > 0 ? 'saving' : lastSettled?.status === 'error' ? 'error' : 'saved';

  return (
    <div className="flex w-full flex-col gap-5.5">
      <div className="flex flex-wrap items-end justify-between gap-3">
        <div className="flex flex-col gap-1.5">
          <span className="flex items-center gap-1.75 text-label font-semibold tracking-label text-marker-text uppercase">
            <span className="size-1.75 rounded-full bg-marker" aria-hidden="true" />
            {t('settings.eyebrow')}
          </span>
          <h1 className="text-display font-semibold text-ink">{t('settings.title')}</h1>
          <p className="text-copy text-muted-foreground">{t('settings.subtitle')}</p>
        </div>
        <span
          role="status"
          className={
            status === 'error'
              ? 'inline-flex items-center gap-1.5 text-body text-error'
              : 'inline-flex items-center gap-1.5 text-body text-muted-foreground'
          }
        >
          {status === 'saving' && <Loader2 className="size-3.75 animate-spin" aria-hidden="true" />}
          {status === 'saved' && <Check className="size-3.75" aria-hidden="true" />}
          {status === 'error' && <TriangleAlert className="size-3.75" aria-hidden="true" />}
          {t(`settings.status.${status}`)}
        </span>
      </div>

      <div className="flex w-full flex-col gap-4.5">
        {meQuery.isPending ? (
          <Skeleton className="h-64 w-full rounded-lg" />
        ) : meQuery.isError ? (
          <p role="alert" className="text-body text-error">
            {t('settings.loadError')}
          </p>
        ) : (
          <AccountCard
            email={email || (meQuery.data.email ?? '')}
            timeZone={timeZone}
            saving={timeZoneMutation.isPending}
            hasPassword={meQuery.data.hasPassword ?? true}
            passwordChangedAt={meQuery.data.passwordChangedAt}
            onTimeZoneChange={(value) => timeZoneMutation.mutate(value)}
          />
        )}
        <PlanBillingCard />
        <NotificationsCard />
        <PaymentsCard />
        <RolesCard />
        <DangerZoneCard />
      </div>
    </div>
  );
}
