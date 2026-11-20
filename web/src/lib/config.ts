/** Fallback Stripe dashboard URL (test mode); production sets VITE_STRIPE_DASHBOARD_URL to the live URL. */
export const DEFAULT_STRIPE_DASHBOARD_URL = 'https://dashboard.stripe.com/test';

/** Read at call time so the env override is honoured per render (and per test). */
export function getStripeDashboardUrl(): string {
  return import.meta.env.VITE_STRIPE_DASHBOARD_URL?.trim() || DEFAULT_STRIPE_DASHBOARD_URL;
}
