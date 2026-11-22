import { useEffect, useMemo, useState, type ReactNode } from 'react';
import { Controller, useForm, useWatch } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { Trans, useTranslation } from 'react-i18next';
import { useMutation, useQuery } from '@tanstack/react-query';
import axios from 'axios';
import { useSearchParams } from 'react-router-dom';
import { EyeIcon, EyeOffIcon } from 'lucide-react';
import { register as registerAccount, resolveInviteToken } from '@/api/auth';
import { getApiErrorMessage, getErrorCode } from '@/lib/api-errors';
import { passwordMeetsAllRules } from '@/lib/password-rules';
import { cn } from '@/lib/utils';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Checkbox } from '@/components/ui/checkbox';
import RoleSelector from '@/components/entry/RoleSelector';
import type { RegistrableRole } from '@/components/register/roles';
import VerificationSentState from '@/components/entry/VerificationSentState';
import PasswordStrengthBar from '@/components/register/PasswordStrengthBar';
import RegisterAside from '@/components/register/RegisterAside';
import RegisterStepper from '@/components/register/RegisterStepper';

interface RegisterFormValues {
  roles: RegistrableRole[];
  firstName: string;
  lastName: string;
  email: string;
  password: string;
  gdprConsent: boolean;
  healthDataConsent: boolean;
}

/**
 * RegisterEndpoint has no machine-readable code for a duplicate email — it
 * pipes ASP.NET Identity's IdentityResult errors through as a bare English
 * `errors[].reason` (e.g. "Email 'x' is already taken."). Match it
 * heuristically rather than looking up an `apiErrors.*` translation key
 * (see `api/auth.ts#register`).
 */
const DUPLICATE_EMAIL_PATTERN = /already taken|already registered/i;

/**
 * Grows/fades its children in and out while staying mounted. Collapsed it is
 * inert, aria-hidden and invisible (not focusable, not announced); the
 * negative margin cancels the parent's flex gap so it takes no space, and
 * visibility is in the transition list so it flips at the end of the collapse.
 */
function Collapse({ open, children }: { open: boolean; children: ReactNode }) {
  return (
    <div
      aria-hidden={!open}
      inert={!open}
      className={cn(
        'grid transition-[grid-template-rows,margin-top,opacity,visibility] duration-(--register-motion) ease-out motion-reduce:transition-none',
        open
          ? 'visible mt-0 grid-rows-[1fr] opacity-100'
          : 'invisible -mt-4.5 grid-rows-[0fr] opacity-0 short:-mt-2.5'
      )}
    >
      <div className="-mx-1 overflow-hidden px-1">{children}</div>
    </div>
  );
}

/**
 * Body of the `/register` page: the account form beside a role-aware
 * explainer, or the "check your email" card once `registerMutation` succeeds.
 * The sent state is the RESULT of the mutation, not a route, and nothing
 * about the submitted email is persisted — a reload degrades to this empty
 * form. Both live in one component so "Change email" returns to the form
 * with the typed values still in place.
 */
export default function RegisterForm() {
  const { t } = useTranslation();
  const [showPassword, setShowPassword] = useState(false);
  const [searchParams] = useSearchParams();
  const inviteToken = searchParams.get('invite');

  // A query, not a mutation fired from an effect: StrictMode would double-fire
  // the effect and burn the endpoint's rate limit.
  const inviteQuery = useQuery({
    queryKey: ['invite-resolve', inviteToken],
    queryFn: () => resolveInviteToken(inviteToken ?? ''),
    enabled: !!inviteToken,
    retry: false,
    staleTime: Infinity,
    refetchOnWindowFocus: false,
  });
  const invitedEmail = inviteQuery.data?.email || null;
  const inviteLocked = !!inviteToken && !!invitedEmail;
  const inviteResolving = !!inviteToken && inviteQuery.isPending;
  const inviteFailed = !!inviteToken && !inviteResolving && !inviteLocked;

  const registerSchema = useMemo(
    () =>
      z
        .object({
          roles: z
            .array(z.enum(['Trainer', 'Nutritionist', 'Client']))
            .min(1, t('entry.register.validation.roleRequired')),
          firstName: z.string().min(1, t('entry.register.validation.firstNameRequired')),
          lastName: z.string().min(1, t('entry.register.validation.lastNameRequired')),
          email: z
            .string()
            .min(1, t('entry.register.validation.emailRequired'))
            .email(t('entry.register.validation.emailInvalid')),
          password: z
            .string()
            .refine(passwordMeetsAllRules, t('entry.register.validation.passwordInvalid')),
          gdprConsent: z.boolean().refine((consented) => consented === true, {
            message: t('entry.register.validation.consentRequired'),
          }),
          healthDataConsent: z.boolean(),
        })
        .superRefine((values, context) => {
          // Health data is only collected from clients, so the box is
          // required for them and never for a coach.
          if (values.roles.includes('Client') && !values.healthDataConsent) {
            context.addIssue({
              code: 'custom',
              path: ['healthDataConsent'],
              message: t('entry.register.validation.healthConsentRequired'),
            });
          }
        }),
    [t]
  );

  const {
    register,
    control,
    handleSubmit,
    setValue,
    setError,
    formState: { errors, isValid },
  } = useForm<RegisterFormValues>({
    resolver: zodResolver(registerSchema),
    // 'onTouched', not 'onBlur': a field's first validation is its blur (no
    // errors mid-keystroke), but afterwards it also revalidates on every
    // change. That is what unlocks the submit button the moment the last
    // checkbox is ticked, with no extra blur/Tab — under plain 'onBlur' the
    // checkbox's change never triggered the validation that recomputes
    // isValid. Every Controller field must forward field.onBlur for this.
    //
    // The disabled submit button is the primary guard against a "six errors
    // at once" dump; the form's onSubmit adds an `!isValid` guard for a
    // submission forced past the disabled attribute, so handleSubmit's
    // full-schema validation never populates every error at once.
    mode: 'onTouched',
    defaultValues: {
      // "?role=client" (from the invite email link) preselects Client.
      roles: searchParams.get('role') === 'client' ? ['Client'] : [],
      firstName: '',
      lastName: '',
      email: '',
      password: '',
      gdprConsent: false,
      healthDataConsent: false,
    },
  });

  useEffect(() => {
    if (!invitedEmail) {
      return;
    }
    setValue('roles', ['Client'], { shouldValidate: true });
    setValue('email', invitedEmail, { shouldValidate: true });
  }, [invitedEmail, setValue]);

  const password = useWatch({ control, name: 'password' });
  const roles = useWatch({ control, name: 'roles' });
  const isClient = roles.includes('Client');

  const registerMutation = useMutation({
    mutationFn: (values: RegisterFormValues) =>
      registerAccount({
        email: values.email,
        password: values.password,
        confirmPassword: values.password,
        firstName: values.firstName,
        lastName: values.lastName,
        roles: values.roles,
        gdprConsent: values.gdprConsent,
        // The backend requires healthDataConsent to be null for coaches, and
        // an absent JSON field binds to the same null, so the key is only
        // sent for a client.
        ...(values.roles.includes('Client') ? { healthDataConsent: true } : {}),
        // Only a token that resolved is sent; a failed one is dropped.
        ...(inviteLocked && inviteToken ? { inviteToken } : {}),
      }),
    onError: (error) => {
      if (axios.isAxiosError(error) && error.response?.status === 400) {
        const reason = getErrorCode(error) ?? '';
        if (DUPLICATE_EMAIL_PATTERN.test(reason)) {
          setError('email', {
            type: 'server',
            message: t('entry.register.errors.duplicateEmail'),
          });
        }
      }
    },
  });

  const resolveBannerErrorMessage = (error: unknown): string | null => {
    if (axios.isAxiosError(error)) {
      const reason = getErrorCode(error) ?? '';
      if (error.response?.status === 400 && DUPLICATE_EMAIL_PATTERN.test(reason)) {
        // Rendered on the email field instead — see registerMutation.onError above.
        return null;
      }
      if (error.response?.status === 429) {
        return t('errors.rateLimitRefresh');
      }
      if (!error.response) {
        return t('entry.register.errors.network');
      }
      return getApiErrorMessage(error, 'entry.register.errors.generic');
    }
    return t('entry.register.errors.generic');
  };

  const bannerErrorMessage = registerMutation.isError
    ? resolveBannerErrorMessage(registerMutation.error)
    : null;

  const onSubmit = (values: RegisterFormValues) => {
    registerMutation.mutate(values);
  };

  if (registerMutation.isSuccess) {
    return (
      <main className="flex flex-1 flex-col items-center px-4 pt-8 pb-16 short:pt-2 short:pb-6 sm:px-10 sm:py-12 short:sm:py-4">
        <VerificationSentState
          email={registerMutation.variables?.email ?? ''}
          isClient={registerMutation.variables?.roles.includes('Client') ?? false}
          onWrongEmail={() => registerMutation.reset()}
        />
      </main>
    );
  }

  const inputClass = 'h-11 px-3.5 text-copy short:h-10';
  const termsEmphasis = <span className="font-semibold text-ink" />;

  return (
    <main className="flex flex-1 flex-col px-4 pt-3 pb-16 short:pt-0 short:pb-6 sm:px-10 lg:py-9 short:lg:py-3">
      <div className="my-auto flex flex-col items-center gap-10 lg:flex-row lg:items-start lg:justify-center lg:gap-15">
        <div className="flex w-full max-w-150 flex-col gap-4.5 rounded-glass border border-border bg-surface px-5 py-8 shadow-card short:gap-3 short:py-5 sm:px-9">
          <RegisterStepper current={1} finalStep={isClient ? 'getApp' : 'profile'} />

          <div className="flex flex-col gap-1.5 pt-1.5 short:gap-0.5 short:pt-0">
            <h1 className="font-display text-display font-semibold tracking-heading text-ink short:text-card-title">
              {t('entry.register.title')}
            </h1>
            <p className="text-copy text-muted-foreground">{t('entry.register.lede')}</p>
            {inviteFailed && (
              <p role="status" className="text-meta text-muted-foreground">
                {t('entry.register.invite.unavailable')}
              </p>
            )}
          </div>

          <form
            onSubmit={(event) => {
              // Defense in depth, see the `mode` comment above: a submission
              // forced past the disabled button must not run handleSubmit's
              // full-schema validation and dump every error at once.
              if (!isValid) {
                event.preventDefault();
                return;
              }
              void handleSubmit(onSubmit)(event);
            }}
            noValidate
            className="flex flex-col gap-4.5 short:gap-2.5"
          >
            <Controller
              control={control}
              name="roles"
              render={({ field }) => (
                <RoleSelector
                  value={field.value}
                  onChange={(next) => {
                    field.onChange(next);
                    if (!next.includes('Client')) {
                      // A hidden, ticked health consent must never linger once
                      // the client card is deselected.
                      setValue('healthDataConsent', false);
                    }
                  }}
                  onBlur={field.onBlur}
                  error={errors.roles?.message}
                  locked={inviteLocked}
                />
              )}
            />

            <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
              <div className="flex flex-col gap-1.5">
                <Label htmlFor="entry-firstName">{t('entry.register.firstNameLabel')}</Label>
                <Input
                  id="entry-firstName"
                  className={inputClass}
                  autoComplete="given-name"
                  placeholder={t('entry.register.firstNamePlaceholder')}
                  aria-invalid={!!errors.firstName}
                  aria-describedby={errors.firstName ? 'entry-firstName-error' : undefined}
                  {...register('firstName')}
                />
                {errors.firstName && (
                  <p id="entry-firstName-error" className="text-meta text-destructive">
                    {errors.firstName.message}
                  </p>
                )}
              </div>

              <div className="flex flex-col gap-1.5">
                <Label htmlFor="entry-lastName">{t('entry.register.lastNameLabel')}</Label>
                <Input
                  id="entry-lastName"
                  className={inputClass}
                  autoComplete="family-name"
                  placeholder={t('entry.register.lastNamePlaceholder')}
                  aria-invalid={!!errors.lastName}
                  aria-describedby={errors.lastName ? 'entry-lastName-error' : undefined}
                  {...register('lastName')}
                />
                {errors.lastName && (
                  <p id="entry-lastName-error" className="text-meta text-destructive">
                    {errors.lastName.message}
                  </p>
                )}
              </div>
            </div>

            <div className="flex flex-col gap-1.5">
              <Label htmlFor="entry-register-email">{t('entry.register.emailLabel')}</Label>
              <Input
                id="entry-register-email"
                className={cn(inputClass, inviteLocked && 'bg-sunken')}
                type="email"
                autoComplete="email"
                placeholder={t('entry.register.emailPlaceholder')}
                aria-invalid={!!errors.email}
                aria-describedby={
                  errors.email
                    ? 'entry-register-email-error'
                    : inviteLocked
                      ? 'entry-register-email-hint'
                      : undefined
                }
                aria-busy={inviteResolving}
                readOnly={inviteLocked}
                aria-readonly={inviteLocked || undefined}
                disabled={inviteResolving}
                {...register('email')}
              />
              {inviteLocked && (
                <p id="entry-register-email-hint" className="text-caption text-muted-foreground">
                  {t('entry.register.invite.emailHint')}
                </p>
              )}
              {errors.email && (
                <p id="entry-register-email-error" className="text-meta text-destructive">
                  {errors.email.message}
                </p>
              )}
            </div>

            <div className="flex flex-col gap-1.5">
              <Label htmlFor="entry-register-password">{t('entry.register.passwordLabel')}</Label>
              <div className="relative">
                <Input
                  id="entry-register-password"
                  type={showPassword ? 'text' : 'password'}
                  autoComplete="new-password"
                  placeholder="••••••••"
                  aria-invalid={!!errors.password}
                  aria-describedby="entry-register-password-hint"
                  className={cn(inputClass, 'pr-16')}
                  {...register('password')}
                />
                <button
                  type="button"
                  onClick={() => setShowPassword((visible) => !visible)}
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
              <PasswordStrengthBar password={password} id="entry-register-password-hint" />
            </div>

            <Controller
              control={control}
              name="gdprConsent"
              render={({ field }) => (
                <label
                  htmlFor="entry-gdpr-consent"
                  className="flex items-start gap-2.5 text-body leading-normal text-ink-2"
                >
                  <Checkbox
                    id="entry-gdpr-consent"
                    className="mt-0.5"
                    checked={field.value}
                    aria-invalid={!!errors.gdprConsent}
                    onCheckedChange={(checked) => field.onChange(checked === true)}
                    onBlur={field.onBlur}
                  />
                  <span>
                    <Trans
                      i18nKey="entry.register.consentLabel"
                      components={{ terms: termsEmphasis, privacy: termsEmphasis }}
                    />
                  </span>
                </label>
              )}
            />
            <Collapse open={!!errors.gdprConsent}>
              <p className="text-meta text-destructive">
                {t('entry.register.validation.consentRequired')}
              </p>
            </Collapse>

            {/* Stays mounted so the health-consent row can collapse and expand. */}
            <Collapse open={isClient}>
              <div className="flex flex-col gap-4.5 short:gap-2.5">
                <Controller
                  control={control}
                  name="healthDataConsent"
                  render={({ field }) => (
                    <label
                      htmlFor="entry-health-consent"
                      className="flex items-start gap-2.5 text-body leading-normal text-ink-2"
                    >
                      <Checkbox
                        id="entry-health-consent"
                        className="mt-0.5"
                        checked={field.value}
                        aria-invalid={!!errors.healthDataConsent}
                        onCheckedChange={(checked) => field.onChange(checked === true)}
                        onBlur={field.onBlur}
                      />
                      <span>{t('entry.register.healthConsentLabel')}</span>
                    </label>
                  )}
                />
                <Collapse open={!!errors.healthDataConsent}>
                  <p className="text-meta text-destructive">
                    {t('entry.register.validation.healthConsentRequired')}
                  </p>
                </Collapse>
              </div>
            </Collapse>

            {bannerErrorMessage && (
              <p role="alert" className="text-meta text-destructive">
                {bannerErrorMessage}
              </p>
            )}

            <Button
              type="submit"
              disabled={!isValid || registerMutation.isPending}
              aria-describedby={!isValid ? 'entry-register-submit-hint' : undefined}
              className="h-12 w-full rounded-xl text-subhead font-bold short:h-10"
            >
              {registerMutation.isPending
                ? t('entry.register.submitting')
                : t('entry.register.submit')}
            </Button>
            {/*
              Always rendered so its line is reserved whether or not it shows —
              the card never grows or shifts as validity changes.
            */}
            <p
              id="entry-register-submit-hint"
              className={cn('-mt-2 text-caption short:-mt-1 text-muted-foreground', isValid && 'invisible')}
            >
              {t('entry.register.submitHint')}
            </p>
          </form>
        </div>

        <div className="w-full max-w-150 lg:w-117.5">
          <RegisterAside variant={isClient ? 'client' : 'coach'} />
        </div>
      </div>
    </main>
  );
}
