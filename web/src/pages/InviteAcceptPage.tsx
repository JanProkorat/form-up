import { Navigate, useSearchParams } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import Wordmark from '@/components/brand/Wordmark';
import { Button } from '@/components/ui/button';
import { Card, CardHeader, CardTitle, CardDescription, CardContent } from '@/components/ui/card';
import StoreButtons from '@/components/register/StoreButtons';

/** Custom URL scheme of the mobile app (mobile/app.json `scheme`). */
const APP_SCHEME_URL = 'formup://';

const PHONE_USER_AGENT = /iPhone|iPad|iPod|Android/i;

/**
 * "/invite/accept" — target of the invite email link. It never accepts the
 * invite: the invite shows up in the mobile app (banner + notification) after
 * sign-in. On a phone it points to the app; anywhere else it hands over to
 * "/register" with the Client role preselected and the token forwarded as
 * `invite` (which itself forwards a signed-in visitor to the portal).
 */
export default function InviteAcceptPage() {
  const { t } = useTranslation();

  const [searchParams] = useSearchParams();

  if (!PHONE_USER_AGENT.test(navigator.userAgent)) {
    // URLSearchParams encodes `+`, `/` and `=` of a base64 token; never concatenate it.
    const target = new URLSearchParams({ role: 'client' });
    const token = searchParams.get('token');
    if (token) {
      target.set('invite', token);
    }
    return <Navigate to={`/register?${target.toString()}`} replace />;
  }

  return (
    <div className="relative flex min-h-screen items-center justify-center bg-background p-6">
      <div
        aria-hidden="true"
        className="pointer-events-none absolute inset-0 [background:radial-gradient(120%_70%_at_50%_0%,var(--color-sunken)_0%,transparent_60%)]"
      />
      <Card className="relative">
        <CardHeader>
          <Wordmark />
        </CardHeader>
        <CardTitle>{t('entry.inviteAccept.title')}</CardTitle>
        <CardDescription>{t('entry.inviteAccept.lede')}</CardDescription>
        <CardContent>
          <Button asChild className="w-full">
            <a href={APP_SCHEME_URL}>{t('entry.inviteAccept.openApp')}</a>
          </Button>
          <p className="text-meta text-muted-foreground">{t('entry.inviteAccept.getApp')}</p>
          <StoreButtons variant="wide" />
        </CardContent>
      </Card>
    </div>
  );
}
