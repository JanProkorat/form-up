import type { TFunction } from 'i18next';
import { SecondRoleState, type GetMySubscriptionResponse } from '@/api/generated';

type SecondRoleFields = Pick<GetMySubscriptionResponse, 'secondRole' | 'scheduledPlan'>;

/**
 * Why a coach cannot add a second role, or null when nothing needs saying.
 * Only a plan without a second role has a reason; a scheduled plan change makes it a dated "after your plan change".
 */
export function secondRoleReason(
  t: TFunction,
  subscription: SecondRoleFields | undefined,
  language: string,
): string | null {
  if (!subscription || subscription.secondRole !== SecondRoleState.NotAvailable) return null;
  const startsAt = subscription.scheduledPlan?.startsAt;
  if (!startsAt) return t('settings.roles.secondRoleUnavailable');
  return t('settings.roles.secondRoleAfterChange', {
    date: new Intl.DateTimeFormat(language, { dateStyle: 'medium' }).format(new Date(startsAt)),
  });
}
