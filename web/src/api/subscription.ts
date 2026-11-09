import { apiClient } from '@/api/client';
import type { GetKeepClientsResponse, GetMySubscriptionResponse } from '@/api/generated';

/** GET /users/me/subscription — the coach's plan, status, client usage and feature flags. */
export async function getMySubscription(): Promise<GetMySubscriptionResponse> {
  return apiClient.getMySubscriptionEndpoint();
}

/** GET /users/me/subscription/keep-clients — the live clients of a lapsed coach and which ones the Free plan keeps. */
export async function getKeepClients(): Promise<GetKeepClientsResponse> {
  return apiClient.getKeepClientsEndpoint();
}

/** PUT /users/me/subscription/keep-clients — chooses the clients the Free plan keeps. */
export async function setKeepClients(clientIds: string[]): Promise<void> {
  return apiClient.setKeepClientsEndpoint({ clientIds });
}

export const subscriptionKeys = {
  mine: ['subscription', 'me'] as const,
  keepClients: ['subscription', 'keep-clients'] as const,
};
