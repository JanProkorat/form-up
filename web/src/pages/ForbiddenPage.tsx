import { Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { useAuthStore } from '@/stores/auth';
import { defaultLandingPath } from '@/lib/landing';

/** Shown to an admin-only account on a portal route outside the libraries and the Admin section. */
export default function ForbiddenPage() {
  const { t } = useTranslation();
  const roles = useAuthStore((s) => s.user?.roles ?? []);

  return (
    <div className="flex min-h-screen flex-col items-center justify-center gap-4 bg-background p-6 text-center">
      <h1 className="text-title font-bold text-ink">{t('shell.forbiddenTitle')}</h1>
      <p className="text-body text-muted-foreground">{t('shell.forbiddenBody')}</p>
      <Link
        to={defaultLandingPath(roles)}
        className="text-body font-medium text-primary underline underline-offset-4"
      >
        {t('shell.backToAdmin')}
      </Link>
    </div>
  );
}
