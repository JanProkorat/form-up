import { getErrorCode, getRfc7807ErrorCode } from '@/lib/api-errors';

const SUBSCRIPTION_REQUIRED = 'SUBSCRIPTION_REQUIRED';
const CLIENT_LIMIT_REACHED = 'CLIENT_LIMIT_REACHED';

const CLIENT_OVER_PLAN_LIMIT = 'CLIENT_OVER_PLAN_LIMIT';
const SUBSCRIPTION_NOT_LAPSED = 'SUBSCRIPTION_NOT_LAPSED';

function hasCode(error: unknown, code: string): boolean {
  return getRfc7807ErrorCode(error) === code || getErrorCode(error) === code;
}

/** 403 SUBSCRIPTION_REQUIRED: the coach's plan does not include the feature. */
export function isSubscriptionRequired(error: unknown): boolean {
  return hasCode(error, SUBSCRIPTION_REQUIRED);
}

/** 403 CLIENT_LIMIT_REACHED: the plan's active-client cap is full. */
export function isClientLimitReached(error: unknown): boolean {
  return hasCode(error, CLIENT_LIMIT_REACHED);
}

/** 403 CLIENT_OVER_PLAN_LIMIT: the client is over the Free plan's cap after a lapse, so the plan action is refused. */
export function isClientOverPlanLimit(error: unknown): boolean {
  return hasCode(error, CLIENT_OVER_PLAN_LIMIT);
}

/** 409 SUBSCRIPTION_NOT_LAPSED: the keep-clients choice only exists while a paid plan has lapsed. */
export function isSubscriptionNotLapsed(error: unknown): boolean {
  return hasCode(error, SUBSCRIPTION_NOT_LAPSED);
}
