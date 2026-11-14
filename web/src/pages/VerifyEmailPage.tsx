import { useState } from 'react';
import type { ReactNode } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { useMutation, useQuery } from '@tanstack/react-query';
import axios from 'axios';
import { MailIcon, TriangleAlertIcon } from 'lucide-react';
import { resendVerificationAnonymous, verifyEmail } from '@/api/auth';
import { getMyProfile } from '@/api/profile';
import { useAuthStore } from '@/stores/auth';
import { getApiErrorMessage, getErrorCode } from '@/lib/api-errors';
import { Button } from '@/components/ui/button';
import RegisterShell from '@/components/register/RegisterShell';
import GetAppState from '@/components/register/GetAppState';
import CoachVerifiedState from '@/components/register/CoachVerifiedState';
import { REGISTER_PLAN_PATH } from '@/components/subscription/planLinks';
import { Card, CardHeader, CardTitle, CardDescription, CardContent } from '@/components/ui/card';

interface VerifyMutationResult {
  /** True when a stored session exists but its `emailConfirmed` could not be refreshed:
   * the coach actions then lead to "/login" so ProtectedRoute can't bounce the user back
   * here with a now-consumed token. */
  staleSession: boolean;
  firstName: string;
  /** Set when the verified account is client-only: the page shows the get-the-app state. */
  client: { email: string } | null;
}

/** Client-only account — same predicate as ProtectedRoute. */
function isClientOnly(roles: string[] | undefined): boolean {
  return (
    !!roles?.includes('Client') &&
    !roles.some((role) => ['Trainer', 'Nutritionist', 'Admin'].includes(role))
  );
}

/**
 * "/verify-email" — own centred page (prototype `#scene-verified`,
 * scratchpad gf-register.html), NOT a panel state. Three distinct callers
 * (design-review findings, issue #1058 phase 3):
 *
 *   1. `?token=…` present            → verify it.
 *   2. No token, active session      → ProtectedRoute.tsx:18-20 redirects
 *      every authenticated-but-unverified user here with no token. Render
 *      "check your inbox" using the session's own email, with a resend.
 *   3. No token, no session          → invalid-link state linking to "/login".
 *
 * StrictMode / single-fire (fixed after the initial phase-3 landing — see
 * git history for the broken `useMutation`-triggered-from-`useEffect`
 * version): `VerifyEmailEndpoint` CONSUMES the token (sets `UsedAt`), so a
 * second call with the same token turns a real, successful verification
 * into a false `INVALID_VERIFICATION_TOKEN` failure. The first version of
 * this page called `useMutation(...).mutate(token)` from inside a
 * `useEffect`, guarded by a `useRef` latch to stop the request itself from
 * firing twice. That latch worked — Playwright confirmed exactly one
 * `/auth/verify-email` request — but the component never re-rendered once
 * the mutation settled: React 18/19 StrictMode's dev-only mount → simulated
 * unmount → remount cycle re-subscribes `useMutation`'s internal observer
 * on the second (surviving) mount pass, while the single fetch this page
 * fired belongs to the FIRST pass's subscription — the notification that
 * fetch produces on settle has nowhere live left to land, so the surviving
 * component's `verifyMutation` object stays permanently idle even though
 * the network call it triggered completed. The ref that correctly stopped a
 * second consuming request also, as a side effect, stopped the only
 * `useEffect` invocation whose resulting mutation instance would have been
 * observed by the component actually left on screen.
 *
 * `useQuery` does not have this failure mode: the fetch and its result live
 * in the shared `QueryClient` cache, keyed by `['verify-email', token]`,
 * external to any one component instance. Both the discarded and the
 * surviving StrictMode mount subscribe to the SAME cache entry — whichever
 * one actually issues the request, the query cache dedupes a second
 * subscriber's fetch for an identical, non-stale key rather than starting a
 * new one, and every subscriber (including the one still mounted when the
 * fetch settles) reads from that same entry. `retry: false` plus
 * `staleTime: Infinity` keep this to exactly one network call for the
 * token's entire single-use lifetime — no retries on the expected 400, and
 * no accidental refetch from a window-focus/reconnect event while the user
 * is reading the result.
 */
export default function VerifyEmailPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  // Read once, verbatim — URLSearchParams already decodes; a second
  // decodeURIComponent() would corrupt a token containing a literal '%'.
  const [token] = useState(() => searchParams.get('token'));

  const isInitialized = useAuthStore((s) => s.isInitialized);
  const isAuthenticated = useAuthStore((s) => s.isAuthenticated);
  const user = useAuthStore((s) => s.user);
  const setUser = useAuthStore((s) => s.setUser);
  const logout = useAuthStore((s) => s.logout);

  const verifyQuery = useQuery({
    queryKey: ['verify-email', token],
    queryFn: async (): Promise<VerifyMutationResult> => {
      // Non-null: this queryFn only ever runs when `enabled` (below) is true.
      const verified = await verifyEmail(token as string);

      const firstName = verified.firstName?.trim() ?? '';

      if (isClientOnly(verified.roles)) {
        return { staleSession: false, firstName, client: { email: verified.email ?? '' } };
      }

      if (!isAuthenticated) {
        return { staleSession: false, firstName, client: null };
      }

      try {
        const profile = await getMyProfile();
        setUser({
          publicId: profile.userId ?? '',
          email: profile.email ?? '',
          firstName: profile.firstName ?? '',
          lastName: profile.lastName ?? '',
          roles: profile.roles ?? [],
          emailConfirmed: profile.emailConfirmed ?? true,
          avatarBlobUrl: profile.avatarBlobUrl ?? null,
        });
        return { staleSession: false, firstName, client: null };
      } catch {
        // Verification itself already succeeded server-side; a failed
        // profile refresh just means the actions fall back to "/login"
        // instead of "/clients" so a stale store can't loop the user.
        return { staleSession: true, firstName, client: null };
      }
    },
    enabled: !!token,
    retry: false,
    staleTime: Infinity,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
  });

  const resendMutation = useMutation({
    mutationFn: (email: string) => resendVerificationAnonymous(email),
  });

  const resolveErrorMessage = (error: unknown): string => {
    if (axios.isAxiosError(error)) {
      if (error.response?.status === 429) {
        return t('errors.rateLimitRefresh');
      }
      if (!error.response) {
        return t('entry.verifyEmail.errors.network');
      }
      return getApiErrorMessage(error, 'entry.verifyEmail.errors.generic');
    }
    return t('entry.verifyEmail.errors.generic');
  };

  const resendErrorMessage = (): string | null => {
    if (!resendMutation.isError) return null;
    if (axios.isAxiosError(resendMutation.error)) {
      if (resendMutation.error.response?.status === 429) {
        return t('errors.rateLimitRefresh');
      }
      if (!resendMutation.error.response) {
        return t('entry.verifyEmail.errors.network');
      }
    }
    return t('entry.verifyEmail.errors.generic');
  };

  const brandRow = (
    <CardHeader>
      <span className="flex size-7.5 items-center justify-center rounded-md bg-marker text-caption font-bold tracking-wide text-on-dark">
        {t('entry.brandMark')}
      </span>
      <span>{t('entry.brand')}</span>
    </CardHeader>
  );

  const shell = (children: ReactNode) => (
    <div className="relative flex min-h-screen items-center justify-center bg-background p-6">
      <div
        aria-hidden="true"
        className="pointer-events-none absolute inset-0 [background:radial-gradient(120%_70%_at_50%_0%,var(--color-sunken)_0%,transparent_60%)]"
      />
      <Card className="relative">{children}</Card>
    </div>
  );

  // Not initialized yet — we cannot tell caller 2 (session, no token) apart
  // from caller 3 (no session, no token) until the auth store has settled.
  if (!isInitialized) {
    return shell(brandRow);
  }

  // Caller 1: cold link with a token.
  if (token) {
    if (verifyQuery.isSuccess && verifyQuery.data.client) {
      return (
        <RegisterShell showSignIn={false}>
          <GetAppState
            email={verifyQuery.data.client.email}
            firstName={verifyQuery.data.firstName}
          />
        </RegisterShell>
      );
    }

    if (verifyQuery.isSuccess) {
      return (
        <RegisterShell showSignIn={false}>
          <CoachVerifiedState
            firstName={verifyQuery.data.firstName}
            planTo={verifyQuery.data.staleSession ? '/login' : REGISTER_PLAN_PATH}
            skipTo={verifyQuery.data.staleSession ? '/login' : '/clients'}
          />
        </RegisterShell>
      );
    }

    if (verifyQuery.isError) {
      const errorCode = getErrorCode(verifyQuery.error);
      const canResendHere =
        errorCode === 'VERIFICATION_TOKEN_EXPIRED' && isAuthenticated && !!user?.email;

      return shell(
        <>
          {brandRow}
          <div className="flex size-11.5 items-center justify-center rounded-full bg-error-soft text-destructive">
            <TriangleAlertIcon className="size-5" />
          </div>
          <CardTitle>{t('entry.verifyEmail.invalid.title')}</CardTitle>
          <CardDescription>{resolveErrorMessage(verifyQuery.error)}</CardDescription>
          <CardContent>
            {canResendHere && user?.email && (
              <>
                <p className="text-meta text-muted-foreground">
                  {t('entry.verifyEmail.resendFromExpired.prompt', { email: user.email })}
                </p>
                {resendErrorMessage() && (
                  <p role="alert" className="text-meta text-destructive">
                    {resendErrorMessage()}
                  </p>
                )}
                {resendMutation.isSuccess && !resendMutation.isError && (
                  <p role="status" className="text-meta text-success-ink">
                    {t('entry.verifyEmail.checkInbox.resendConfirmation')}
                  </p>
                )}
                <Button
                  type="button"
                  variant="outline"
                  className="w-full"
                  disabled={resendMutation.isPending}
                  onClick={() => resendMutation.mutate(user.email)}
                >
                  {resendMutation.isPending
                    ? t('entry.verifyEmail.checkInbox.resending')
                    : t('entry.verifyEmail.resendFromExpired.button')}
                </Button>
              </>
            )}
            <Button
              type="button"
              variant={canResendHere ? 'ghost' : 'default'}
              className="w-full"
              onClick={() => navigate('/login')}
            >
              {t('entry.verifyEmail.invalid.backToLogin')}
            </Button>
          </CardContent>
        </>
      );
    }

    // Pending / not-yet-started (StrictMode's first commit before the
    // effect fires).
    return shell(
      <>
        {brandRow}
        <CardDescription>{t('entry.verifyEmail.verifying')}</CardDescription>
      </>
    );
  }

  // Caller 2: no token, active session — ProtectedRoute redirected here for
  // an authenticated-but-unverified user. Must render "check your inbox",
  // never the invalid-link state, or the redirect becomes a dead end.
  if (isAuthenticated && user && !user.emailConfirmed) {
    return shell(
      <>
        {brandRow}
        <div className="flex size-11.5 items-center justify-center rounded-full bg-success-soft text-success-ink">
          <MailIcon className="size-5" />
        </div>
        <CardTitle>{t('entry.verifyEmail.checkInbox.title')}</CardTitle>
        <CardDescription>
          {t('entry.verifyEmail.checkInbox.lede', { email: user.email })}
        </CardDescription>
        <CardContent>
          {resendErrorMessage() && (
            <p role="alert" className="text-meta text-destructive">
              {resendErrorMessage()}
            </p>
          )}
          {resendMutation.isSuccess && !resendMutation.isError && (
            <p role="status" className="text-meta text-success-ink">
              {t('entry.verifyEmail.checkInbox.resendConfirmation')}
            </p>
          )}
          <Button
            type="button"
            variant="outline"
            className="w-full"
            disabled={resendMutation.isPending}
            onClick={() => resendMutation.mutate(user.email)}
          >
            {resendMutation.isPending
              ? t('entry.verifyEmail.checkInbox.resending')
              : t('entry.verifyEmail.checkInbox.resend')}
          </Button>
          <Button
            type="button"
            variant="ghost"
            className="w-full"
            onClick={() => {
              logout();
              navigate('/login', { replace: true });
            }}
          >
            {t('auth.logout')}
          </Button>
        </CardContent>
      </>
    );
  }

  // Caller 3: no token, no session (or an already-confirmed user landing
  // here directly) — invalid-link state, no API call.
  return shell(
    <>
      {brandRow}
      <div className="flex size-11.5 items-center justify-center rounded-full bg-error-soft text-destructive">
        <TriangleAlertIcon className="size-5" />
      </div>
      <CardTitle>{t('entry.verifyEmail.invalid.title')}</CardTitle>
      <CardDescription>{t('entry.verifyEmail.invalid.noSessionLede')}</CardDescription>
      <CardContent>
        <Button type="button" className="w-full" onClick={() => navigate('/login')}>
          {t('entry.verifyEmail.invalid.backToLogin')}
        </Button>
      </CardContent>
    </>
  );
}
