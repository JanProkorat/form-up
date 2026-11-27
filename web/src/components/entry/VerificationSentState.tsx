import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useMutation } from '@tanstack/react-query';
import axios from 'axios';
import { MailIcon } from 'lucide-react';
import { resendVerificationAnonymous } from '@/api/auth';
import { cn } from '@/lib/utils';
import ChangeEmailForm from '@/components/entry/ChangeEmailForm';
import RegisterStepper from '@/components/register/RegisterStepper';

/**
 * The server enforces no resend cooldown (it only caps sends per 24 h), so
 * this wait is a client-side courtesy: it starts on arrival and after every
 * resend, and is deliberately never persisted.
 */
const RESEND_COOLDOWN_SECONDS = 60;

interface VerificationSentStateProps {
  email: string;
  isClient: boolean;
  /** Registration password, in memory only; used to sign in for an email change. */
  password: string;
  onEmailChanged: (newEmail: string) => void;
}

function formatCountdown(totalSeconds: number): string {
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = String(totalSeconds % 60).padStart(2, '0');
  return `${minutes}:${seconds}`;
}

/**
 * "Check your email" card, rendered by RegisterForm once registration
 * succeeds. It is a result state, not a route: the URL stays `/register`, and
 * the address lives only in props so a reload degrades to the empty form
 * rather than resurrecting a stale screen.
 */
export default function VerificationSentState({
  email,
  isClient,
  password,
  onEmailChanged,
}: VerificationSentStateProps) {
  const { t } = useTranslation();
  const [changing, setChanging] = useState(false);
  const [resendConfirmed, setResendConfirmed] = useState(false);
  const [secondsLeft, setSecondsLeft] = useState(RESEND_COOLDOWN_SECONDS);

  useEffect(() => {
    if (secondsLeft <= 0) {
      return;
    }
    const timer = window.setTimeout(() => setSecondsLeft((seconds) => seconds - 1), 1000);
    return () => window.clearTimeout(timer);
  }, [secondsLeft]);

  const resendMutation = useMutation({
    mutationFn: () => resendVerificationAnonymous(email),
    onSuccess: () => {
      setResendConfirmed(true);
      setSecondsLeft(RESEND_COOLDOWN_SECONDS);
    },
  });

  const resendErrorMessage = (): string | null => {
    if (!resendMutation.isError) return null;
    if (axios.isAxiosError(resendMutation.error)) {
      if (resendMutation.error.response?.status === 429) {
        return t('errors.rateLimitRefresh');
      }
      if (!resendMutation.error.response) {
        return t('entry.register.errors.network');
      }
    }
    return t('entry.register.errors.generic');
  };

  const errorMessage = resendErrorMessage();
  const resendDisabled = resendMutation.isPending || secondsLeft > 0;

  const handleResend = () => {
    setResendConfirmed(false);
    resendMutation.mutate();
  };

  return (
    <div className="my-auto flex w-full max-w-140 flex-col items-center gap-4.5 rounded-glass border border-border bg-surface px-5 pt-8 pb-9 text-center shadow-card short:gap-3 short:pt-5 short:pb-6 sm:px-10">
      <div className="self-stretch pb-2 text-left">
        <RegisterStepper current={2} finalStep={isClient ? 'getApp' : 'profile'} />
      </div>

      <span className="flex size-21 items-center justify-center rounded-full bg-error-soft text-marker short:size-14">
        <MailIcon className="size-9.5 short:size-7" strokeWidth={1.8} aria-hidden="true" />
      </span>

      {changing ? (
        <ChangeEmailForm
          email={email}
          password={password}
          onChanged={onEmailChanged}
          onCancel={() => setChanging(false)}
        />
      ) : (
        <>
        <h1 className="font-display text-display font-semibold tracking-heading text-ink short:text-card-title">
          {t('entry.register.sent.title')}
        </h1>
        <p className="text-subhead text-ink-2">
          {t('entry.register.sent.lede')}
          <br />
          <b className="font-bold break-all text-ink">{email}</b>
        </p>

        <div className="flex flex-col gap-1.5 self-stretch rounded-xl border border-border bg-surface px-4 py-3.5 text-left text-body leading-normal text-muted-foreground">
          <span className="font-bold text-ink">{t('entry.register.sent.noteTitle')}</span>
          <span>{t('entry.register.sent.note')}</span>

          {errorMessage && (
            <p role="alert" className="text-meta text-destructive">
              {errorMessage}
            </p>
          )}
          {resendConfirmed && !errorMessage && (
            <p role="status" className="text-meta text-success-ink">
              {t('entry.register.sent.resendConfirmation')}
            </p>
          )}

          <span className="flex items-center gap-2 pt-1">
            <button
              type="button"
              disabled={resendDisabled}
              onClick={handleResend}
              className={cn(
                'font-semibold',
                resendDisabled
                  ? 'cursor-not-allowed text-muted-foreground opacity-70'
                  : 'text-ink underline underline-offset-2'
              )}
            >
              {resendMutation.isPending
                ? t('entry.register.sent.resending')
                : t('entry.register.sent.resend')}
            </button>
            {secondsLeft > 0 && (
              <span className="text-meta">
                · {t('entry.register.sent.resendIn', { time: formatCountdown(secondsLeft) })}
              </span>
            )}
          </span>
        </div>

        <p className="text-body text-muted-foreground">
          {t('entry.register.sent.wrongEmail')}{' '}
          <button
            type="button"
            onClick={() => setChanging(true)}
            className="font-bold text-ink underline underline-offset-2"
          >
            {t('entry.register.sent.changeEmail')}
          </button>
        </p>
        </>
      )}
    </div>
  );
}
