import { Link, useLocation } from 'react-router-dom';
import { scrollToAnchor } from '@/components/home/smoothAnchor';
import { useTranslation } from 'react-i18next';
import Wordmark from '@/components/brand/Wordmark';
import LanguageSwitcher from '@/components/entry/LanguageSwitcher';
import NavCompactMenu from '@/components/home/NavCompactMenu';
import ThemeToggle from '@/components/home/ThemeToggle';

const NAV_LINKS = [
  { id: 'how-it-works', key: 'howItWorks' },
  { id: 'for-coaches', key: 'forCoaches' },
  { id: 'for-nutritionists', key: 'forNutritionists' },
  { id: 'mobile-app', key: 'mobileApp' },
  { id: 'questions', key: 'questions' },
] as const;

const PRICING_PATH = '/pricing';

/** DOM id of the nav Sign in link; the sign-in dialog returns focus here on close. */
export const SIGN_IN_LINK_ID = 'home-sign-in';

/** Top navigation of the public home page: wordmark, section anchors, sign in / create account. */
export default function HomeNav() {
  const { t } = useTranslation();
  const { pathname } = useLocation();
  // The section anchors only exist on the landing page; elsewhere they link back to it.
  const onLanding = pathname !== PRICING_PATH;
  const onPricing = pathname === PRICING_PATH;

  return (
    <header className="flex h-19 items-center gap-4 px-4 sm:px-10 lg:gap-3 lg:px-16 xl:gap-9">
      <Wordmark to="/" />
      <nav
        aria-label={t('home.nav.label')}
        className="ml-2 hidden gap-4 text-body font-medium text-ink-2 lg:flex xl:ml-6 xl:gap-7 xl:text-copy"
      >
        {NAV_LINKS.map((link) =>
          onLanding ? (
            <a key={link.id} href={`#${link.id}`} onClick={scrollToAnchor} className="hover:text-ink">
              {t(`home.nav.${link.key}`)}
            </a>
          ) : (
            <Link key={link.id} to={{ pathname: '/', hash: link.id }} className="hover:text-ink">
              {t(`home.nav.${link.key}`)}
            </Link>
          ),
        )}
        <Link
          to={PRICING_PATH}
          aria-current={onPricing ? 'page' : undefined}
          className={onPricing ? 'font-semibold text-ink' : 'hover:text-ink'}
        >
          {t('home.nav.pricing')}
        </Link>
      </nav>
      <div className="ml-auto flex items-center gap-1.5 sm:gap-2.5">
        <div className="sm:hidden">
          <NavCompactMenu />
        </div>
        <div className="hidden items-center gap-2.5 sm:flex">
          <LanguageSwitcher />
          <ThemeToggle />
        </div>
        <Link
          id={SIGN_IN_LINK_ID}
          to="/login"
          className="flex h-10 items-center rounded-field border border-border bg-surface px-3 text-body whitespace-nowrap sm:px-4 sm:text-copy font-semibold text-ink hover:bg-sunken"
        >
          {t('home.nav.signIn')}
        </Link>
        <Link
          to="/register"
          className="hidden h-10 items-center rounded-field bg-primary px-3 text-body whitespace-nowrap md:flex sm:px-4.5 sm:text-copy font-bold text-primary-foreground hover:opacity-90"
        >
          {t('home.nav.createAccount')}
        </Link>
      </div>
    </header>
  );
}
