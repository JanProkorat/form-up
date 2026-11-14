import { Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';

/** Closing call to action: a dark banner with one Create account link. */
export default function HomeJoinBanner() {
  const { t } = useTranslation();

  return (
    <section className="px-4 sm:px-10 lg:px-16">
      <div className="mx-auto flex w-full max-w-home flex-col items-start gap-6 rounded-3xl border border-border bg-banner px-7 py-10 sm:flex-row sm:items-center sm:gap-8 sm:px-13 sm:py-12">
        <h2 className="font-display text-home-banner font-semibold text-on-dark">
          {t('home.join.title')}
        </h2>
        <Link
          to="/register"
          className="flex h-13 min-[1484px]:h-14 min-[1616px]:h-15 min-[1736px]:h-16 min-[2216px]:h-17 min-[2576px]:h-19 items-center rounded-xl bg-marker-solid px-6.5 text-home-subhead font-bold whitespace-nowrap text-on-dark hover:opacity-90 sm:ml-auto"
        >
          {t('home.join.cta')}
        </Link>
      </div>
    </section>
  );
}
