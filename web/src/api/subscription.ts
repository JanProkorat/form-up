import { apiClient } from '@/api/client';
import type {
  GetCoachPlansResponse,
  GetKeepClientsResponse,
  GetMySubscriptionResponse,
  GetPublicSubscriptionPlansResponse,
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

/** GET /subscription-plans — the offered plans for the public pricing page; anonymous, cheapest first. */
export async function getPublicSubscriptionPlans(): Promise<GetPublicSubscriptionPlansResponse> {
  return apiClient.getPublicSubscriptionPlansEndpoint();
}

export const pricingKeys = {
  plans: ['pricing', 'plans'] as const,
};

export const subscriptionKeys = {
  mine: ['subscription', 'me'] as const,
  keepClients: ['subscription', 'keep-clients'] as const,
  plans: ['subscription', 'plans'] as const,
};
