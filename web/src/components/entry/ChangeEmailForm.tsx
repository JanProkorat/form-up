import { useMemo } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { Trans, useTranslation } from 'react-i18next';
import { useMutation } from '@tanstack/react-query';
import axios from 'axios';
import { changeUnverifiedEmail, login, revokeSession } from '@/api/auth';
import { getApiErrorMessage, getErrorCode } from '@/lib/api-errors';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';

/** Mirrors the server validator's cap on the new address. */
const MAX_EMAIL_LENGTH = 100;

interface ChangeEmailFormProps {
  /** Address the account currently has (the one the dead link went to). */
  email: string;
  /** Registration password, held in memory only, to sign the throwaway session in. */
  password: string;
  onChanged: (newEmail: string) => void;
  onCancel: () => void;
}

interface ChangeEmailValues {
  newEmail: string;
}

/** Marks a failure of the sign-in step so it never gets field-level treatment. */
class SignInFailedError extends Error {
  readonly original: unknown;

  constructor(original: unknown) {
    super('Sign-in for the email change failed.');
    this.original = original;
  }
}

/**
 * Inline "Change your email" content of the check-email card (board:
 * PageRegisterEmailSteps frame "2b Change email"). Signs in with the held
 * registration credentials, moves the unverified account to the new address,
 * then best-effort revokes that throwaway session. Tokens never leave this
 * mutation: not in the auth store, not in state, not in query keys.
 */
export default function ChangeEmailForm({
  email,
  password,
  onChanged,
  onCancel,
}: ChangeEmailFormProps) {
  const { t } = useTranslation();

  const schema = useMemo(
    () =>
      z.object({
        newEmail: z
          .string()
          .min(1, t('entry.register.changeEmail.required'))
          .email(t('entry.register.changeEmail.invalid'))
          .max(MAX_EMAIL_LENGTH, t('entry.register.changeEmail.invalid')),
      }),
    [t]
  );

  const {
    register,
    handleSubmit,
    setError,
    formState: { errors },
  } = useForm<ChangeEmailValues>({
    resolver: zodResolver(schema),
    defaultValues: { newEmail: '' },
  });

  const mutation = useMutation({
    mutationFn: async ({ newEmail }: ChangeEmailValues) => {
      let session;
      try {
        session = await login(email, password);
      } catch (error) {
        throw new SignInFailedError(error);
      }
      if (!session.accessToken) {
        throw new SignInFailedError(null);
      }
      try {
        await changeUnverifiedEmail(session.accessToken, newEmail);
      } finally {
        if (session.refreshToken) {
          // Best effort, also after a failed change: never masks the change result.
          await revokeSession(session.accessToken, session.refreshToken).catch(() => undefined);
        }
      }
      return newEmail;
    },
    onSuccess: (newEmail) => onChanged(newEmail),
    onError: (error) => {
      if (error instanceof SignInFailedError || !axios.isAxiosError(error)) return;
      if (error.response?.status !== 400) return;
      const code = getErrorCode(error);
      if (code === 'VERIFICATION_RESEND_LIMIT_REACHED' || code === 'EMAIL_ALREADY_VERIFIED') {
        return; // shown as a banner
      }
      const key =
        code === 'EMAIL_ALREADY_IN_USE'
          ? 'taken'
          : code === 'EMAIL_UNCHANGED'
            ? 'unchanged'
            : 'invalid';
      setError('newEmail', { type: 'server', message: t(`entry.register.changeEmail.${key}`) });
    },
  });

  const bannerMessage = (): string | null => {
    if (!mutation.isError) return null;
    const error = mutation.error;
    const cause = error instanceof SignInFailedError ? error.original : error;
    if (axios.isAxiosError(cause)) {
      if (cause.response?.status === 429) return t('errors.rateLimitRefresh');
      if (!cause.response) return t('entry.register.errors.network');
      if (error instanceof SignInFailedError) return t('entry.register.changeEmail.generic');
      const code = getErrorCode(cause);
      if (
        cause.response.status === 400 &&
        (code === 'VERIFICATION_RESEND_LIMIT_REACHED' || code === 'EMAIL_ALREADY_VERIFIED')
      ) {
        return getApiErrorMessage(cause, 'entry.register.changeEmail.generic');
      }
      if (cause.response.status === 400) return null; // inline on the field
    }
    return t('entry.register.changeEmail.generic');
  };

  const banner = bannerMessage();
  const fieldError = errors.newEmail?.message;

  return (
    <>
      <h1 className="font-display text-display font-semibold tracking-heading text-ink short:text-card-title">
        {t('entry.register.changeEmail.title')}
      </h1>
      <p className="text-subhead text-ink-2">
        <Trans
          i18nKey="entry.register.changeEmail.lede"
          values={{ email }}
          components={{ br: <br />, old: <b className="font-bold break-all text-ink" /> }}
        />
      </p>

      <form
        noValidate
        onSubmit={(event) => {
          void handleSubmit((values) => mutation.mutateAsync(values).catch(() => undefined))(event);
        }}
        className="flex flex-col gap-4.5 self-stretch text-left short:gap-2.5"
      >
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="entry-change-email">{t('entry.register.changeEmail.label')}</Label>
          <Input
            id="entry-change-email"
            type="email"
            autoComplete="email"
            autoFocus
            className="h-12 px-3.5 text-copy short:h-10"
            aria-invalid={!!fieldError}
            aria-describedby={fieldError ? 'entry-change-email-error' : undefined}
            {...register('newEmail')}
          />
          {fieldError && (
            <p id="entry-change-email-error" className="text-meta text-destructive">
              {fieldError}
            </p>
          )}
        </div>

        {banner && (
          <p role="alert" className="text-meta text-destructive">
            {banner}
          </p>
        )}

        <Button
          type="submit"
          disabled={mutation.isPending}
          aria-busy={mutation.isPending}
          className="h-12 w-full rounded-xl text-subhead font-bold short:h-10"
        >
          {mutation.isPending
            ? t('entry.register.changeEmail.submitting')
            : t('entry.register.changeEmail.submit')}
        </Button>
      </form>

      <button
        type="button"
        onClick={onCancel}
        className="text-body font-bold text-muted-foreground"
      >
        {t('entry.register.changeEmail.cancel')}
      </button>
    </>
  );
}
