import { useMemo, useState } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { useTranslation } from 'react-i18next';
import { Link, useLocation, useNavigate, useSearchParams } from 'react-router-dom';
import { useMutation } from '@tanstack/react-query';
import axios from 'axios';
import { Apple, EyeIcon, EyeOffIcon } from 'lucide-react';
import { login as loginRequest } from '@/api/auth';
import { getMyProfile } from '@/api/profile';
import type { GetProfileResponse } from '@/api/generated';
import { useAuthStore } from '@/stores/auth';
import { getApiErrorMessage } from '@/lib/api-errors';
import { Button } from '@/components/ui/button';
import EntryDialogTitle from '@/components/entry/EntryDialogTitle';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Checkbox } from '@/components/ui/checkbox';

/**
 * Distinguishes "login succeeded but the follow-up profile fetch failed"
 * (design-review error path: tokens are already persisted, the mutation
 * has already logged the half-authenticated session out by the time this
 * is thrown) from a rejected login request itself, which the mutation's
 * error handler resolves via getApiErrorMessage + the network-error check
 * instead.
 */
class ProfileLoadError extends Error {}

/** In-app path only: no protocol-relative, backslash, /login, or control/whitespace characters (browsers strip them). */
function isSafeInAppPath(value: unknown): value is string {
  if (
    typeof value !== 'string' ||
    !value.startsWith('/') ||
    value.startsWith('//') ||
    value.startsWith('/\\') ||
    value.startsWith('/login')
  ) {
    return false;
  }
  return ![...value].some((ch) => {
    const code = ch.charCodeAt(0);
    return code <= 0x20 || (code >= 0x7f && code <= 0x9f) || /\s/.test(ch);
  });
}

/** Query-string `returnTo` first (survives reloads and new tabs), then router state, then /clients. */
function resolveReturnTo(queryValue: string | null, state: unknown): string {
  if (isSafeInAppPath(queryValue)) return queryValue;
  const from = (state as { from?: unknown } | null)?.from;
  return isSafeInAppPath(from) ? from : '/clients';
}

interface LoginFormValues {
  email: string;
  password: string;
}

/**
 * Login form — email/password fields, Google/Apple (disabled, see below),
 * and the links into the register / forgot-password routes. Rendered inside
 * the sign-in dialog at "/login" (App.tsx's pathless layout route under
 * EntryPage).
 *
 * Google and Apple sign-in ship disabled for this issue: there is no Google
 * client id configured in this repo's env, Apple needs a domain-verified
 * Services ID the compose harness cannot exercise, and the social-nonce
 * flow is epic item 2's to own (see design-review finding, issue Notes).
 *
 * "Keep me signed in" picks where the refresh token is stored: ticked =
 * localStorage (survives a browser restart), unticked = sessionStorage
 * (closing the browser signs out). The backend issues the same token pair
 * either way.
 */
export default function LoginForm() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const location = useLocation();
  const [searchParams] = useSearchParams();
  const setTokens = useAuthStore((s) => s.setTokens);
  const storeLogin = useAuthStore((s) => s.login);
  const storeLogout = useAuthStore((s) => s.logout);
  const [showPassword, setShowPassword] = useState(false);
  const [rememberMe, setRememberMe] = useState(true);

  const loginSchema = useMemo(
    () =>
      z.object({
        email: z
          .string()
          .min(1, t('entry.login.validation.emailRequired'))
          .email(t('entry.login.validation.emailInvalid')),
        password: z.string().min(1, t('entry.login.validation.passwordRequired')),
      }),
    [t]
  );

  const {
    register,
    handleSubmit,
    formState: { errors },
  } = useForm<LoginFormValues>({
    resolver: zodResolver(loginSchema),
    defaultValues: { email: '', password: '' },
  });

  const loginMutation = useMutation({
    mutationFn: async ({ email, password }: LoginFormValues) => {
      const { accessToken, refreshToken } = await loginRequest(email, password);
      if (!accessToken || !refreshToken) {
        throw new ProfileLoadError('missing-tokens');
      }
      setTokens(accessToken, refreshToken, rememberMe);

      let profile: GetProfileResponse;
      try {
        profile = await getMyProfile();
      } catch {
        // Tokens are already persisted — a half-authenticated session must
        // not linger (design-review error path: login ok, /users/me failed).
        storeLogout();
        throw new ProfileLoadError('profile-load-failed');
      }

      storeLogin(
        {
          publicId: profile.userId ?? '',
          email: profile.email ?? '',
          firstName: profile.firstName ?? '',
          lastName: profile.lastName ?? '',
          roles: profile.roles ?? [],
          emailConfirmed: profile.emailConfirmed ?? true,
          avatarBlobUrl: profile.avatarBlobUrl ?? null,
        },
        accessToken,
        refreshToken,
        rememberMe
      );
    },
    onSuccess: () => {
      navigate(resolveReturnTo(searchParams.get('returnTo'), location.state), { replace: true });
    },
  });

  const resolveErrorMessage = (error: unknown): string => {
    if (error instanceof ProfileLoadError) {
      return t('entry.login.errors.profileLoadFailed');
    }
    if (axios.isAxiosError(error)) {
      if (error.response?.status === 429) {
        return t('errors.rateLimitRefresh');
      }
      if (!error.response) {
        return t('entry.login.errors.network');
      }
      return getApiErrorMessage(error, 'entry.login.errors.generic');
    }
    return t('entry.login.errors.generic');
  };

  const errorMessage = loginMutation.isError ? resolveErrorMessage(loginMutation.error) : null;

  const onSubmit = (values: LoginFormValues) => {
    loginMutation.mutate(values);
  };

  return (
    <>
      <div>
        <EntryDialogTitle>{t('entry.login.title')}</EntryDialogTitle>
        <p className="mt-1 text-body text-muted-foreground">{t('entry.login.lede')}</p>
      </div>

      <form onSubmit={handleSubmit(onSubmit)} noValidate className="flex flex-col gap-4">
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="entry-email">{t('entry.login.emailLabel')}</Label>
          <Input
            id="entry-email"
            className="h-11 px-3.5 text-copy"
            type="email"
            autoComplete="email"
            placeholder={t('entry.login.emailPlaceholder')}
            aria-invalid={!!errors.email}
            aria-describedby={errors.email ? 'entry-email-error' : undefined}
            {...register('email')}
          />
          {errors.email && (
            <p id="entry-email-error" className="text-meta text-destructive">
              {errors.email.message}
            </p>
          )}
        </div>

        <div className="flex flex-col gap-1.5">
          <Label htmlFor="entry-password">{t('entry.login.passwordLabel')}</Label>
          <div className="relative">
            <Input
              id="entry-password"
              type={showPassword ? 'text' : 'password'}
              autoComplete="current-password"
              placeholder="••••••••"
              aria-invalid={!!errors.password}
              aria-describedby={errors.password ? 'entry-password-error' : undefined}
              className="h-11 px-3.5 pr-16 text-copy"
              {...register('password')}
            />
            <button
              type="button"
              onClick={() => setShowPassword((v) => !v)}
              aria-label={
                showPassword ? t('entry.login.hidePassword') : t('entry.login.showPassword')
              }
              className="absolute inset-y-0 right-2 inline-flex items-center gap-1 text-caption font-semibold text-muted-foreground"
            >
              {showPassword ? (
                <EyeOffIcon className="size-3.5" />
              ) : (
                <EyeIcon className="size-3.5" />
              )}
              {showPassword ? t('entry.login.hidePassword') : t('entry.login.showPassword')}
            </button>
          </div>
          {errors.password && (
            <p id="entry-password-error" className="text-meta text-destructive">
              {errors.password.message}
            </p>
          )}
        </div>

        <div className="flex items-baseline justify-between gap-3">
          <Label htmlFor="entry-remember" className="text-muted-foreground">
            <Checkbox
              id="entry-remember"
              checked={rememberMe}
              onCheckedChange={(checked) => setRememberMe(checked === true)}
            />
            {t('entry.login.rememberMe')}
          </Label>
          <Link
            to="/forgot-password"
            className="text-meta font-medium text-ink underline underline-offset-2"
          >
            {t('entry.login.forgotPassword')}
          </Link>
        </div>

        {errorMessage && (
          <p role="alert" className="text-meta text-destructive">
            {errorMessage}
          </p>
        )}

        <Button
          type="submit"
          disabled={loginMutation.isPending}
          className="h-11.5 w-full rounded-xl text-subhead font-bold"
        >
          {loginMutation.isPending ? t('entry.login.submitting') : t('entry.login.submit')}
        </Button>
      </form>

      <div className="flex items-center gap-3 text-caption tracking-[.1em] text-faint uppercase">
        <span className="h-px flex-1 bg-border" />
        <span>{t('entry.login.or')}</span>
        <span className="h-px flex-1 bg-border" />
      </div>

      <div className="grid grid-cols-2 gap-2.5">
        <Button type="button" variant="outline" disabled className="h-10.5 font-semibold" title={t('entry.login.googleComingSoon')}>
          {t('entry.login.google')}
        </Button>
        <Button type="button" variant="outline" disabled className="h-10.5 font-semibold" title={t('entry.login.appleComingSoon')}>
          <Apple className="size-4" aria-hidden="true" />
          {t('entry.login.apple')}
        </Button>
      </div>

      <p className="text-center text-body text-muted-foreground">
        {t('entry.login.noAccount')}{' '}
        <Link to="/register" className="font-bold text-ink underline underline-offset-2">
          {t('entry.login.createAccount')}
        </Link>
      </p>
    </>
  );
}
