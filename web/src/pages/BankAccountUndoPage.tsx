import { useEffect, useState } from 'react';
import type { ReactNode } from 'react';
import { useLocation, useNavigate, useSearchParams } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { useMutation } from '@tanstack/react-query';
import { CheckIcon, HistoryIcon, TriangleAlertIcon } from 'lucide-react';
import { undoBankAccountChange } from '@/api/payment-settings';
import { getErrorCode, getErrorStatus, getRfc7807ErrorCode } from '@/lib/api-errors';
import Wordmark from '@/components/brand/Wordmark';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';

const UNDO_INVALID = 'BANK_ACCOUNT_UNDO_INVALID';
const UNDO_SUPERSEDED = 'BANK_ACCOUNT_UNDO_SUPERSEDED';

type FailureKind = 'invalid' | 'superseded' | 'rateLimited' | 'generic';

function failureKind(error: unknown): FailureKind {
  const code = getErrorCode(error) ?? getRfc7807ErrorCode(error);
  if (code === UNDO_INVALID) {
    return 'invalid';
  }
  if (code === UNDO_SUPERSEDED) {
    return 'superseded';
  }
  return getErrorStatus(error) === 429 ? 'rateLimited' : 'generic';
}

/**
 * "/bank-account/undo" — the page behind the link in the bank-account-change email. It acts only on an
 * explicit "Undo change" click: nothing is sent on load, so a mail scanner that opens the link cannot
 * burn the single-use token. The token is read once into state and removed from the address bar, and the
 * page loads nothing third-party. Lives outside ProtectedRoute and EntryPage, next to "/verify-email".
 */
export default function BankAccountUndoPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { pathname } = useLocation();
  const [searchParams] = useSearchParams();
  // Read once, verbatim: URLSearchParams already decodes, so a second decode would corrupt the token.
  const [token] = useState(() => searchParams.get('token'));

  // Keep the token out of the address bar, the history entry and anything that copies the URL.
  useEffect(() => {
    if (token !== null) {
      navigate(pathname, { replace: true });
    }
  }, [token, navigate, pathname]);

  const undoMutation = useMutation({
    mutationFn: (value: string) => undoBankAccountChange(value),
  });

  const brandRow = (
    <CardHeader>
      <Wordmark />
    </CardHeader>
  );

  const shell = (children: ReactNode) => (
    <div className="relative flex min-h-screen items-center justify-center bg-background p-6">
      <div
        aria-hidden="true"
        className="pointer-events-none absolute inset-0 [background:radial-gradient(120%_70%_at_50%_0%,var(--color-sunken)_0%,transparent_60%)]"
      />
      <Card className="relative" data-testid="bank-account-undo">
        {children}
      </Card>
    </div>
  );

  const signInButton = (
    <Button type="button" className="w-full" onClick={() => navigate('/login')}>
      {t('payments.undo.signIn')}
    </Button>
  );

  const problemCard = (titleKey: string, bodyKey: string) =>
    shell(
      <>
        {brandRow}
        <div className="flex size-11.5 items-center justify-center rounded-full bg-error-soft text-destructive">
          <TriangleAlertIcon className="size-5" aria-hidden="true" />
        </div>
        <CardTitle>{t(titleKey)}</CardTitle>
        <CardDescription role="alert">{t(bodyKey)}</CardDescription>
        <CardContent>{signInButton}</CardContent>
      </>,
    );

  if (undoMutation.isSuccess) {
    return shell(
      <>
        {brandRow}
        <div className="flex size-11.5 items-center justify-center rounded-full bg-success-soft text-success-ink">
          <CheckIcon className="size-5" aria-hidden="true" />
        </div>
        <CardTitle>{t('payments.undo.success.title')}</CardTitle>
        <CardDescription role="status">{t('payments.undo.success.body')}</CardDescription>
        <CardContent>{signInButton}</CardContent>
      </>,
    );
  }

  if (undoMutation.isError) {
    switch (failureKind(undoMutation.error)) {
      case 'invalid':
        return problemCard('payments.undo.invalid.title', 'payments.undo.invalid.body');
      case 'superseded':
        return problemCard('payments.undo.superseded.title', 'payments.undo.superseded.body');
      case 'rateLimited':
        return problemCard('payments.undo.rateLimited.title', 'payments.undo.rateLimited.body');
      default:
        return problemCard('payments.undo.generic.title', 'payments.undo.generic.body');
    }
  }

  if (token === null || token === '') {
    return problemCard('payments.undo.missing.title', 'payments.undo.missing.body');
  }

  return shell(
    <>
      {brandRow}
      <div className="flex size-11.5 items-center justify-center rounded-full bg-sunken text-ink">
        <HistoryIcon className="size-5" aria-hidden="true" />
      </div>
      <CardTitle>{t('payments.undo.confirm.title')}</CardTitle>
      <CardDescription>{t('payments.undo.confirm.body')}</CardDescription>
      <CardContent>
        <Button
          type="button"
          className="w-full"
          disabled={undoMutation.isPending}
          onClick={() => undoMutation.mutate(token)}
        >
          {undoMutation.isPending ? t('payments.undo.confirm.working') : t('payments.undo.confirm.button')}
        </Button>
        <Button type="button" variant="ghost" className="w-full" onClick={() => navigate('/login')}>
          {t('payments.undo.confirm.notNow')}
        </Button>
      </CardContent>
    </>,
  );
}
