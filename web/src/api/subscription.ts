import { apiClient } from '@/api/client';
import type {
  ChangePlanResponse,
  ConfirmSecondRoleAddOnResponse,
  CreateCheckoutSessionRequest,
  GetCoachPlansResponse,
  GetKeepClientsResponse,
  GetMySubscriptionResponse,
  PreviewSecondRoleAddOnResponse,
  ScheduleSwitchToFreeResponse,
} from '@/api/generated';

/** GET /users/me/subscription — the coach's plan, status, client usage and feature flags. */
export async function getMySubscription(): Promise<GetMySubscriptionResponse> {
  return apiClient.getMySubscriptionEndpoint();
}

/** GET /users/me/subscription/plans — the plans a coach can choose between, cheapest first. */
export async function getCoachPlans(): Promise<GetCoachPlansResponse> {
  return apiClient.getCoachPlansEndpoint();
}

/** GET /users/me/subscription/keep-clients — the live clients of a coach choosing Free or lapsed, and which ones Free keeps. */
export async function getKeepClients(): Promise<GetKeepClientsResponse> {
  return apiClient.getKeepClientsEndpoint();
}

/** PUT /users/me/subscription/keep-clients — chooses the clients the Free plan keeps. */
export async function setKeepClients(clientIds: string[]): Promise<void> {
  return apiClient.setKeepClientsEndpoint({ clientIds });
}

/** POST /users/me/subscription/choose-free — takes the Free plan after a given plan ended; the ids are required only over the cap. */
export async function chooseFreePlan(keepClientIds: string[]): Promise<void> {
  return apiClient.chooseFreePlanEndpoint({ keepClientIds });
}

/** POST /billing/change-plan — moves a live paid subscription to another paid plan (upgrade now, downgrade at renewal). */
export async function changePlan(planCode: string): Promise<ChangePlanResponse> {
  return apiClient.changePlanEndpoint({ planCode });
}

/** POST /billing/switch-to-free — ends the paid plan at the end of the paid period. */
export async function switchToFree(): Promise<ScheduleSwitchToFreeResponse> {
  return apiClient.scheduleSwitchToFreeEndpoint();
}

/** POST /billing/promotion-code — applies a code to the caller's subscription (204). */
export async function applyPromotionCode(code: string): Promise<void> {
  return apiClient.applyPromotionCodeEndpoint({ code });
}

/** POST /billing/second-role/preview — the new bill and today's prorated charge for the second-role add-on. */
export async function previewSecondRole(): Promise<PreviewSecondRoleAddOnResponse> {
  return apiClient.previewSecondRoleAddOnEndpoint();
}

/** POST /billing/second-role/confirm — charges the add-on now; the same attempt id never charges twice. */
export async function confirmSecondRole(attemptId: string): Promise<ConfirmSecondRoleAddOnResponse> {
  return apiClient.confirmSecondRoleAddOnEndpoint({ attemptId });
}

/** POST /billing/checkout-session — resolves with the hosted Stripe Checkout URL. */
export async function createCheckoutSession(request: CreateCheckoutSessionRequest): Promise<string> {
  const result = await apiClient.createCheckoutSessionEndpoint(request);
  if (!result.url) throw new Error('Checkout session came back without a URL');
  return result.url;
}

/** POST /billing/portal-session — resolves with the Stripe Customer Portal URL. */
export async function createPortalSession(): Promise<string> {
  const result = await apiClient.createPortalSessionEndpoint();
  if (!result.url) throw new Error('Portal session came back without a URL');
  return result.url;
}

export const subscriptionKeys = {
  mine: ['subscription', 'me'] as const,
  keepClients: ['subscription', 'keep-clients'] as const,
  plans: ['subscription', 'plans'] as const,
  secondRolePreview: ['subscription', 'second-role-preview'] as const,
};
