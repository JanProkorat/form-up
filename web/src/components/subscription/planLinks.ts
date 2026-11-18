/** Route of the lapsed coach's "keep these clients" screen. */
export const KEEP_CLIENTS_PATH = '/plan/keep-clients';

/** Anchor id of the Plan and billing section on the Settings page. */
export const PLAN_BILLING_ANCHOR = 'plan-billing';

/** Settings route with the hash that scrolls to the Plan and billing section. */
export const PLAN_BILLING_PATH = `/settings#${PLAN_BILLING_ANCHOR}`;

/** Route of the blocking plan picker shown while a coach's given plan has ended and no plan is chosen. */
export const PLAN_CHOICE_PATH = '/plan/choose';

/** Route of the plan picker a coach opens from Plan and billing, the paywall and the client-limit dialog. */
export const PLAN_PICKER_PATH = '/plan';

/** Route of the plan step in the sign-up flow, the first part of "Your profile". */
export const REGISTER_PLAN_PATH = '/register/plan';

/** Where Stripe Checkout sends the coach back; the paths are set in the backend's Stripe configuration. */
export const BILLING_SUCCESS_PATH = '/billing/success';
export const BILLING_CANCEL_PATH = '/billing/cancel';
/** Where the Stripe Customer Portal sends the coach back. */
export const BILLING_PORTAL_RETURN_PATH = '/billing';
