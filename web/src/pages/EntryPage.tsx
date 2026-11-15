import { useEffect, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { Navigate, useLocation, useNavigate, useOutlet } from 'react-router-dom';
import { XIcon } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { useAuthStore } from '@/stores/auth';
import { Dialog, DialogClose, DialogContent } from '@/components/ui/dialog';
import HomeNav, { SIGN_IN_LINK_ID } from '@/components/home/HomeNav';
import { scrollToId } from '@/components/home/smoothAnchor';
import HomeHero from '@/components/home/HomeHero';
import HomeHowItWorks from '@/components/home/HomeHowItWorks';
import HomeCoaches from '@/components/home/HomeCoaches';
import HomeNutritionists from '@/components/home/HomeNutritionists';
import HomeMobileApp from '@/components/home/HomeMobileApp';
import HomeQuestions from '@/components/home/HomeQuestions';
import HomeJoinBanner from '@/components/home/HomeJoinBanner';
import HomeFooter from '@/components/home/HomeFooter';

const DIALOG_PATHS = ['/login', '/forgot-password', '/auth/reset-password'];
const LOGIN_PATH = '/login';
const RESET_PATH = '/auth/reset-password';

interface DisplayedForm {
  key: string;
  element: ReactNode;
}

/**
 * Public entry layout route ("/", "/login", "/forgot-password",
 * "/auth/reset-password"), a pathless layout route in App.tsx. The landing page always renders; the
 * dialog is open on every path except "/" and shows whichever child route's
 * form is active. This page has no `<Outlet />` of its own: `useOutlet()`
 * resolves the active child as a value so it can be kept mounted while the
 * dialog plays its closing animation (by then the route is "/", whose child
 * renders nothing).
 *
 * One Dialog stays mounted across the three form routes, so switching between
 * them swaps the content in place instead of closing and reopening. Closing
 * navigates to "/" with `replace`, so Back does not reopen a dialog the user
 * just dismissed. Registration is a full page of its own (RegisterPage), not
 * a dialog route.
 *
 * Signed-in visitors are redirected to "/clients", except on the reset route:
 * the emailed link must reach its form, and a successful reset signs the
 * browser out locally.
 *
 * Focus: on open, Email is focused on the login route only; on forgot-password
 * and reset-password the dialog container takes focus, so no field is marked
 * touched and swallows the user's first click. On close, focus returns to the
 * nav's Sign in link.
 */
export default function EntryPage() {
  const { t } = useTranslation();
  const isAuthenticated = useAuthStore((s) => s.isAuthenticated);
  const location = useLocation();
  const navigate = useNavigate();
  const outlet = useOutlet();
  const contentRef = useRef<HTMLDivElement>(null);

  const open = DIALOG_PATHS.includes(location.pathname);
  const [displayed, setDisplayed] = useState<DisplayedForm>({
    key: location.pathname,
    element: outlet,
  });

  // Track the active form only while the dialog is open, so the closing
  // animation still shows the form the user just left. Adjusting state during
  // render is React's pattern for deriving state from changing inputs; the
  // guard goes false once `displayed.key` catches up.
  if (open && displayed.key !== location.pathname) {
    setDisplayed({ key: location.pathname, element: outlet });
  }

  // Arriving from another page via "/#section": the browser cannot find the target before the page renders.
  useEffect(() => {
    if (location.hash.length > 1) {
      scrollToId(decodeURIComponent(location.hash.slice(1)));
    }
  }, [location.hash]);

  if (isAuthenticated && location.pathname !== RESET_PATH) {
    return <Navigate to="/clients" replace />;
  }

  const handleOpenChange = (next: boolean) => {
    if (!next) {
      navigate('/', { replace: true });
    }
  };

  return (
    <div className="home-page-wash isolate bg-background">
      <HomeNav />
      <HomeHero />
      <HomeHowItWorks />
      <HomeCoaches />
      <HomeNutritionists />
      <HomeMobileApp />
      <div className="flex flex-col lg:min-h-svh">
        <HomeQuestions />
        <HomeJoinBanner />
        <HomeFooter />
      </div>

      <Dialog open={open} onOpenChange={handleOpenChange}>
        <DialogContent
          ref={contentRef}
          showCloseButton={false}
          overlayClassName="backdrop-blur-scrim"
          aria-describedby={undefined}
          onOpenAutoFocus={(event) => {
            event.preventDefault();
            const email = location.pathname === LOGIN_PATH ? document.getElementById('entry-email') : null;
            (email ?? contentRef.current)?.focus({ preventScroll: true });
          }}
          onCloseAutoFocus={(event) => {
            event.preventDefault();
            document.getElementById(SIGN_IN_LINK_ID)?.focus({ preventScroll: true });
          }}
          className="max-h-[calc(100dvh-var(--spacing)*8)] w-[calc(100%-var(--spacing)*8)] max-w-110 overflow-y-auto rounded-glass px-6 py-7 sm:px-8"
        >
          <div key={displayed.key} className="flex flex-col gap-4">
            {displayed.element}
          </div>
          <DialogClose className="absolute top-7 right-6 flex size-8.5 items-center justify-center rounded-full bg-background text-ink-2 outline-none hover:bg-sunken focus-visible:ring-3 focus-visible:ring-ring/50 sm:right-8">
            <XIcon className="size-4" aria-hidden="true" />
            <span className="sr-only">{t('common.close')}</span>
          </DialogClose>
        </DialogContent>
      </Dialog>
    </div>
  );
}
