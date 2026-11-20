/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** Google OAuth client ID for @react-oauth/google. */
  readonly VITE_GOOGLE_CLIENT_ID: string;
  /** Apple Services ID for Apple Sign-In (e.g. com.example.app.web). */
  readonly VITE_APPLE_CLIENT_ID: string;
  /**
   * Redirect URI registered in the Apple Developer portal for this web app.
   * Must match exactly (e.g. https://app.example.com).
   */
  readonly VITE_APPLE_REDIRECT_URI: string;
  /** App Store listing URL; unset or empty renders the store button disabled. */
  readonly VITE_APP_STORE_URL?: string;
  /** Google Play listing URL; unset or empty renders the store button disabled. */
  readonly VITE_GOOGLE_PLAY_URL?: string;
  /** Stripe dashboard URL (https only) for the admin Reports page; unset or empty hides the link. */
  readonly VITE_STRIPE_DASHBOARD_URL?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
