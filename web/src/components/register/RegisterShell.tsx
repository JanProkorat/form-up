import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import Wordmark from '@/components/brand/Wordmark';

interface RegisterShellProps {
  children: ReactNode;
  /** Hide the "Already have an account? Sign in" link (the get-the-app page has no portal link). */
  showSignIn?: boolean;
  /** Replaces the sign-in link on the right of the header, e.g. "Signed in as …" once the account exists. */
  trailing?: ReactNode;
}

/**
 * Full-page frame of the register flow: the glow-wash background shared with
 * the home hero, the wordmark and the "Already have an account? Sign in"
 * header. Sign in goes to /login, which opens the landing page's dialog.
 */
export default function RegisterShell({ children, showSignIn = true, trailing }: RegisterShellProps) {
  const { t } = useTranslation();

  return (
    <div className="relative isolate flex min-h-dvh flex-col overflow-x-clip bg-background text-ink">
      <div
        aria-hidden="true"
        className="pointer-events-none absolute inset-0 -z-10 register-page-wash"
      />
      <header className="flex h-19 shrink-0 items-center short:h-13 gap-4 px-4 sm:px-10 lg:px-16">
        <Wordmark to="/" />
        {trailing && <p className="ml-auto text-right text-meta text-muted-foreground sm:text-copy">{trailing}</p>}
        {showSignIn && !trailing && (
          <p className="ml-auto text-right text-meta text-muted-foreground sm:text-copy">
            {t('entry.register.haveAccount')}{' '}
            <Link to="/login" className="font-bold text-ink underline underline-offset-2">
              {t('entry.register.signIn')}
            </Link>
          </p>
        )}
      </header>
      {children}
    </div>
  );
}
