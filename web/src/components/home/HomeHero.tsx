import { Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import HeroMockup from '@/components/home/HeroMockup';
import { IconPlay } from '@/components/home/HomeIcons';
import { Loop } from '@/components/home/MockupParts';
import { scrollToAnchor } from '@/components/home/smoothAnchor';

const STEPS = [
  { key: 'build', fill: 'hm-s1' },
  { key: 'publish', fill: 'hm-s2' },
  { key: 'follow', fill: 'hm-s3' },
  { key: 'see', fill: 'hm-s4' },
] as const;

/** Home hero: headline, calls to action, the 10 s product loop and the four-step strip that fills with it. */
export default function HomeHero() {
  const { t } = useTranslation();

  return (
    <section className="relative flex min-w-0 flex-col justify-center px-4 pt-6 pb-12 sm:px-10 lg:min-h-[calc(100svh-var(--spacing)*19)] lg:px-16">
      <div className="@container home-tier mx-auto flex w-full max-w-home flex-col gap-8 lg:flex-row lg:gap-12">
        <div className="flex max-w-117.5 @min-[1340px]:max-w-122.5 @min-[1472px]:max-w-129 @min-[1592px]:max-w-141 @min-[1760px]:max-w-153 @min-[1940px]:max-w-170.5 shrink-0 flex-col gap-5.5 pt-4 lg:pt-8.5">
          <h1 className="font-display text-home-hero font-semibold tracking-hero text-ink">
            {t('home.hero.headline')} <span className="text-marker">{t('home.hero.headlineAccent')}</span>
          </h1>
          <p className="text-home-sub text-ink-2">{t('home.hero.sub')}</p>
          <div className="flex flex-wrap gap-2.5 pt-1.5">
            <Link
              to="/register"
              className="flex h-12.5 @min-[1340px]:h-13 @min-[1472px]:h-14 @min-[1592px]:h-15 @min-[1760px]:h-16 @min-[1940px]:h-18 items-center rounded-xl bg-primary px-6 text-home-subhead font-bold text-primary-foreground hover:opacity-90"
            >
              {t('home.hero.createAccount')}
            </Link>
            <a
              href="#how-it-works"
              onClick={scrollToAnchor}
              className="flex h-12.5 @min-[1340px]:h-13 @min-[1472px]:h-14 @min-[1592px]:h-15 @min-[1760px]:h-16 @min-[1940px]:h-18 items-center gap-2 rounded-xl border border-border bg-surface px-5 text-home-subhead font-semibold text-ink hover:bg-sunken"
            >
              <IconPlay size={14} />
              {t('home.hero.seeInAction')}
            </a>
          </div>
        </div>

        <Loop className="flex min-w-0 flex-col gap-5.5">
          <div className="hidden @min-[820px]:block">
            <HeroMockup />
          </div>
          <ol
            className="grid max-w-191.5 @min-[1340px]:max-w-199 @min-[1472px]:max-w-210.5 @min-[1592px]:max-w-230 @min-[1760px]:max-w-249 @min-[1940px]:max-w-278 grid-cols-2 gap-x-4.5 gap-y-5 max-lg:@min-[820px]:grid-cols-4 lg:@min-[1060px]:grid-cols-4"
          >
            {STEPS.map((step, index) => (
              <li key={step.key} className="flex flex-col gap-2">
                <div aria-hidden="true" className="h-0.75 overflow-hidden rounded-xs bg-border">
                  <i className={`${step.fill} block h-full origin-left bg-marker`} />
                </div>
                <span className="text-home-body font-bold text-ink">
                  {index + 1} · {t(`home.steps.${step.key}.title`)}
                </span>
              </li>
            ))}
          </ol>
        </Loop>
      </div>
    </section>
  );
}
