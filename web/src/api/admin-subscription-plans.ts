/**
 * Admin subscription plans API module.
 *
 * Wraps the NSwag-generated /admin/subscription-plans endpoints.
 */
import { apiClient } from '@/api/client';
import { BillingInterval } from '@/api/generated';
import type {
  CreateSubscriptionPlanRequest,
  OptionalFieldOfNullableOfInt32,
  SubscriptionPlanDto,
  UpdateSubscriptionPlanRequest,
} from '@/api/generated';

export { BillingInterval };
export type { CreateSubscriptionPlanRequest, SubscriptionPlanDto };

/**
 * Update body with the client cap as a plain value. The generated type models the server-side
 * wrapper object, but on the wire the field is the bare number, with `null` meaning Unlimited.
 */
export type UpdateSubscriptionPlanBody = Omit<UpdateSubscriptionPlanRequest, 'maxActiveClients'> & {
  maxActiveClients: number | null;
};

export const adminPlansKey = ['admin', 'subscription-plans'] as const;

/** Lists every plan, inactive ones included, via GET /admin/subscription-plans. */
export async function getAdminPlans(): Promise<SubscriptionPlanDto[]> {
  const response = await apiClient.getSubscriptionPlansEndpoint();
  return response.plans ?? [];
}

/** Creates a plan via POST /admin/subscription-plans. */
export async function createAdminPlan(request: CreateSubscriptionPlanRequest): Promise<SubscriptionPlanDto> {
  return apiClient.createSubscriptionPlanEndpoint(request);
}

/** Replaces a plan via PUT /admin/subscription-plans/{code}. The cap key is always sent (null = Unlimited). */
export async function updateAdminPlan(
  code: string,
  body: UpdateSubscriptionPlanBody,
): Promise<SubscriptionPlanDto> {
  // The cast only bridges the generated wrapper type to the bare wire value the server reads.
  const request: UpdateSubscriptionPlanRequest = {
    ...body,
    maxActiveClients: body.maxActiveClients as unknown as OptionalFieldOfNullableOfInt32,
  };
  return apiClient.updateSubscriptionPlanEndpoint(code, request);
}

/** Stops offering a plan via DELETE /admin/subscription-plans/{code}. */
export async function deactivateAdminPlan(code: string): Promise<void> {
  await apiClient.deactivateSubscriptionPlanEndpoint(code);
}
