import { getErrorCode, getProblemBody, getRfc7807ErrorCode } from '@/lib/api-errors';

const SUBSCRIPTION_REQUIRED = 'SUBSCRIPTION_REQUIRED';
const CLIENT_LIMIT_REACHED = 'CLIENT_LIMIT_REACHED';

const CLIENT_OVER_PLAN_LIMIT = 'CLIENT_OVER_PLAN_LIMIT';
const SUBSCRIPTION_NOT_LAPSED = 'SUBSCRIPTION_NOT_LAPSED';
const PLAN_CHOICE_REQUIRED = 'PLAN_CHOICE_REQUIRED';
const PLAN_CHOICE_NOT_REQUIRED = 'PLAN_CHOICE_NOT_REQUIRED';

/** Window event fired by the API layer when any request is refused with 403 PLAN_CHOICE_REQUIRED. */
export const PLAN_CHOICE_REQUIRED_EVENT = 'app:planChoiceRequired';

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

/**
 * 403 PLAN_CHOICE_REQUIRED: the coach's given plan ended and every write is refused until a plan is chosen.
 * Also reads the raw-text body of an unparsed axios error, which is what the API interceptor sees.
 */
export function isPlanChoiceRequired(error: unknown): boolean {
  return hasCode(error, PLAN_CHOICE_REQUIRED) || getProblemBody(error)?.errorCode === PLAN_CHOICE_REQUIRED;
}

/** 409 PLAN_CHOICE_NOT_REQUIRED: choosing the Free plan only exists while a plan choice is pending. */
export function isPlanChoiceNotRequired(error: unknown): boolean {
  return hasCode(error, PLAN_CHOICE_NOT_REQUIRED);
}
