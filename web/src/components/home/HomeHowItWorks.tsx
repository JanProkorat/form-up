// Pixel sizes and positions in this file are illustration drawing copied from the board, not layout tokens.
import { useCallback, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Cell, GridLabel, Loop, PhoneShell, Ring, Row } from '@/components/home/MockupParts';

const STEPS = ['build', 'publish', 'follow', 'see'] as const;

const DESKTOP_QUERY = '(min-width: 1024px)';
/** Ignore the scroll position while a click on a step scrolls the page to it. */
const SCROLL_LOCK_MS = 1100;

// Stage design box = 700 x 660. Below lg the stage takes the whole container, from lg the room beside
// the 380 px step column (plus 48 gap). The ladder sets --mz on the stage card, which derives its height
// from it, so the box zooms and the card follows; the window height caps the zoom.
const STAGE_ZOOM =
  'max-lg:[--mz:0.4] max-lg:@min-[340px]:[--mz:0.46] max-lg:@min-[360px]:[--mz:0.48] max-lg:@min-[420px]:[--mz:0.58] max-lg:@min-[500px]:[--mz:0.7] max-lg:@min-[580px]:[--mz:0.8] max-lg:@min-[660px]:[--mz:0.93] max-lg:@min-[720px]:[--mz:1] lg:[--mz:0.65] lg:@min-[960px]:[--mz:0.74] lg:@min-[1040px]:[--mz:0.85] lg:@min-[1120px]:[--mz:0.97] lg:@min-[1160px]:[--mz:1] lg:@min-[1340px]:[--mz:1.03] lg:@min-[1472px]:[--mz:1.1] lg:@min-[1592px]:[--mz:1.2] lg:@min-[1760px]:[--mz:1.3] lg:@min-[1940px]:[--mz:1.45]';

// Delays are the board's, played 1.5x faster like the durations in home-animations.css.
const delay = (seconds: number) => ({ animationDelay: `${(seconds / 1.5).toFixed(2)}s` });

type FoodKey =
  | 'oatBowl'
  | 'yogurtBowl'
  | 'eggToast'
  | 'lentilCurry'
  | 'tunaSalad'
  | 'chickenBowl'
  | 'salmon'
  | 'beefStirFry'
  | 'tofuNoodles';

const BUILD_ROWS: ReadonlyArray<{
  meal: 'breakfast' | 'lunch' | 'dinner';
  items: ReadonlyArray<{ food: FoodKey; kcal: number; accent?: boolean }>;
}> = [
  {
    meal: 'breakfast',
    items: [
      { food: 'oatBowl', kcal: 420 },
      { food: 'yogurtBowl', kcal: 380 },
      { food: 'eggToast', kcal: 460 },
      { food: 'oatBowl', kcal: 420 },
    ],
  },
  {
    meal: 'lunch',
    items: [
      { food: 'lentilCurry', kcal: 560 },
      { food: 'tunaSalad', kcal: 520 },
      { food: 'chickenBowl', kcal: 640, accent: true },
      { food: 'lentilCurry', kcal: 560 },
    ],
  },
  {
    meal: 'dinner',
    items: [
      { food: 'salmon', kcal: 610 },
      { food: 'beefStirFry', kcal: 590 },
      { food: 'tofuNoodles', kcal: 540 },
      { food: 'beefStirFry', kcal: 590 },
    ],
  },
];

const DAYS = ['mon', 'tue', 'wed', 'thu'] as const;

function BuildPanel() {
  const { t, i18n } = useTranslation();
  const format = (value: number) => value.toLocaleString(i18n.language);

  return (
    <div className="flex w-[640px] flex-col gap-[18px]">
      <div className="flex items-baseline gap-2.5">
        <span className="text-copy font-bold text-ink">{t('home.how.stage.planWeek')}</span>
        <span className="text-meta text-muted-foreground">{t('home.how.stage.draft')}</span>
      </div>
      <div className="grid grid-cols-[70px_repeat(4,minmax(0,1fr))] grid-rows-[22px_repeat(3,62px)] gap-2">
        <span />
        {DAYS.map((day) => (
          <GridLabel key={day} className="self-center">
            {t(`home.mock.days.${day}`)}
          </GridLabel>
        ))}
        {BUILD_ROWS.map((row, rowIndex) => (
          <BuildRow key={row.meal} row={row} rowIndex={rowIndex} />
        ))}
      </div>
      <div className="flex items-center gap-3.5 pt-1.5">
        <span className="w-[70px] text-meta text-muted-foreground">{t('home.mock.days.wed')}</span>
        <span className="h-2 flex-1 overflow-hidden rounded-sm bg-sunken">
          <span className="hm-bar block h-full w-[78%] bg-nutrition" style={delay(0.9)} />
        </span>
        <span className="text-body font-bold text-ink">
          {format(1640)} / {format(2100)} {t('home.mock.kcal')}
        </span>
      </div>
    </div>
  );
}

function BuildRow({ row, rowIndex }: { row: (typeof BUILD_ROWS)[number]; rowIndex: number }) {
  const { t } = useTranslation();

  return (
    <>
      <GridLabel>{t(`home.mock.meals.${row.meal}`)}</GridLabel>
      {row.items.map((item, index) => (
        <Cell
          key={index}
          tone={item.accent ? 'accent' : 'filled'}
          name={t(`home.mock.foods.${item.food}`)}
          kcal={`${item.kcal} ${t('home.mock.kcal')}`}
          className="hm-in"
          style={delay(0.1 + (rowIndex * 4 + index) * 0.15)}
        />
      ))}
    </>
  );
}

function PublishPanel() {
  const { t } = useTranslation();
  const monday: ReadonlyArray<{ food: FoodKey; kcal: number }> = [
    { food: 'oatBowl', kcal: 420 },
    { food: 'lentilCurry', kcal: 560 },
    { food: 'salmon', kcal: 610 },
  ];

  return (
    <div className="flex items-center gap-14">
      <div className="box-border flex w-[280px] flex-col gap-3.5 rounded-card border border-border p-[22px]">
        <span className="text-meta text-muted-foreground">{t('home.mock.weekReady')}</span>
        <span className="font-display text-mockup-weight font-semibold text-ink">{t('home.mock.planName')}</span>
        <span
          className="hm-m-press flex h-11 items-center justify-center rounded-xl bg-primary text-copy font-bold text-primary-foreground"
          style={delay(0.7)}
        >
          {t('home.mock.publishWeek')}
        </span>
        <span className="hm-in text-meta font-bold tracking-caps text-nutrition uppercase" style={delay(1)}>
          {t('home.mock.published')}
        </span>
      </div>
      <svg
        width="40"
        height="16"
        viewBox="0 0 40 16"
        fill="none"
        strokeWidth="2"
        strokeLinecap="round"
        aria-hidden="true"
        className="hm-in stroke-muted-foreground"
        style={delay(1.1)}
      >
        <path d="M2 8h34M30 2l6 6-6 6" />
      </svg>
      <PhoneShell>
        <div
          className="hm-slide flex items-center gap-2.5 rounded-2xl border border-border bg-sunken p-3"
          style={delay(1.3)}
        >
          <span className="size-7 shrink-0 rounded-lg bg-marker" />
          <span className="flex flex-col gap-0.5">
            <span className="text-meta font-bold text-ink">{t('home.mock.notifReadyTitle')}</span>
            <span className="text-caption text-muted-foreground">{t('home.how.stage.readyBody')}</span>
          </span>
        </div>
        <div className="hm-in flex flex-col gap-2 pt-2" style={delay(1.8)}>
          <span className="text-caption text-muted-foreground">{t('home.how.stage.monday')}</span>
          {monday.map((item) => (
            <Row key={item.food}>
              <Ring />
              <span className="flex-1 text-body font-semibold text-ink">{t(`home.mock.foods.${item.food}`)}</span>
              <span className="text-caption text-muted-foreground">{item.kcal}</span>
            </Row>
          ))}
        </div>
      </PhoneShell>
    </div>
  );
}

function FollowPanel() {
  const { t } = useTranslation();
  const rows = [
    { title: t('home.mock.foods.eggToast'), detail: '460', at: 0.3 },
    { title: t('home.mock.foods.appleAlmonds'), detail: '210', at: 0.8 },
    { title: t('home.mock.foods.chickenBowl'), detail: '640', at: 1.3 },
    { title: t('home.mock.workoutName'), detail: t('home.mock.workoutMinutes'), at: 1.8 },
  ];
  const notes = [
    { key: 'mealsOnTrack', at: 1.1 },
    { key: 'workoutDone', at: 2.1 },
  ] as const;

  return (
    <div className="flex items-center gap-12">
      <PhoneShell>
        <span className="text-caption text-muted-foreground">{t('home.mock.dayWednesday')}</span>
        <span className="mb-1 font-display text-mockup-title font-semibold text-ink">{t('home.how.stage.today')}</span>
        {rows.map((row) => (
          <Row key={row.title}>
            <Ring ticked tickClassName="hm-pop" tickStyle={delay(row.at)} />
            <span className="flex-1 text-body font-semibold text-ink">{row.title}</span>
            <span className="text-caption text-muted-foreground">{row.detail}</span>
          </Row>
        ))}
      </PhoneShell>
      <div className="flex w-[300px] flex-col gap-3">
        {notes.map((note) => (
          <div
            key={note.key}
            className="hm-in flex flex-col gap-[3px] rounded-[14px] bg-primary px-4 py-3.5 text-primary-foreground"
            style={delay(note.at)}
          >
            <span className="text-body font-bold">{t(`home.how.stage.${note.key}.title`)}</span>
            <span className="text-meta opacity-75">{t(`home.how.stage.${note.key}.body`)}</span>
          </div>
        ))}
      </div>
    </div>
  );
}

const SWATCHES: ReadonlyArray<{ tint: string; at: number }> = [
  { tint: 'bg-photo-sand', at: 0.6 },
  { tint: 'bg-photo-tan', at: 0.8 },
  { tint: 'bg-photo-clay', at: 1 },
];

function SeePanel() {
  const { t } = useTranslation();

  return (
    <div className="box-border flex w-[560px] flex-col gap-[22px] rounded-[20px] border border-border p-7">
      <div className="flex items-center gap-3">
        <span className="flex size-10 items-center justify-center rounded-full bg-marker-solid font-bold text-on-dark">
          {t('home.mock.petraInitial')}
        </span>
        <span className="flex flex-col gap-0.5">
          <span className="text-copy font-bold text-ink">{t('home.how.stage.checkinTitle')}</span>
          <span className="text-meta text-muted-foreground">{t('home.mock.weekShort')}</span>
        </span>
      </div>
      <div className="flex items-end gap-6">
        <div className="hm-in flex items-baseline gap-2">
          <span className="font-display text-[56px] leading-none font-semibold text-ink">
            {t('home.mock.weightValue')}
          </span>
          <span className="text-home-lead text-muted-foreground">{t('home.mock.kg')}</span>
          <span className="rounded-md bg-nutrition-soft px-2 py-1 text-body font-bold text-nutrition">
            {t('home.mock.weightDeltaShort')}
          </span>
        </div>
        <svg width="220" height="60" viewBox="0 0 220 60" fill="none" aria-hidden="true" className="ml-auto">
          <path
            className="hm-draw stroke-nutrition"
            d="M4 12 L40 16 L76 14 L112 26 L148 30 L184 40 L216 48"
            strokeWidth="3"
            strokeLinecap="round"
            strokeLinejoin="round"
          />
        </svg>
      </div>
      <div className="flex gap-2.5">
        {SWATCHES.map((swatch) => (
          <span
            key={swatch.tint}
            className={`hm-in h-[140px] w-[110px] rounded-xl ${swatch.tint}`}
            style={delay(swatch.at)}
          />
        ))}
      </div>
      <span className="hm-in text-copy text-ink-2" style={delay(1.4)}>
        {t('home.how.stage.checkinNote')}
      </span>
    </div>
  );
}


const PANEL_BY_STEP = [<BuildPanel />, <PublishPanel />, <FollowPanel />, <SeePanel />];

/**
 * "How it works": four steps beside one shared stage. From lg the stage is sticky and the active step
 * follows the scroll (invisible sentinels in a tall track, read through IntersectionObserver); clicking
 * a step selects it and scrolls to its stretch. Below lg there is no sticky stage: the step buttons
 * sit above it and a tap picks the step. The stage panel is remounted per step so its entrance replays.
 */
export default function HomeHowItWorks() {
  const { t } = useTranslation();
  const [active, setActive] = useState(0);
  const track = useRef<HTMLDivElement>(null);
  const sentinels = useRef<Array<HTMLDivElement | null>>([]);
  const intersecting = useRef(new Set<number>());
  const locked = useRef(false);
  const settleTimer = useRef<number>(undefined);

  const followScroll = useCallback(() => {
    if (locked.current || intersecting.current.size === 0) {
      return;
    }
    setActive(Math.max(...intersecting.current));
  }, []);

  useEffect(() => {
    if (typeof IntersectionObserver === 'undefined') {
      return;
    }

    // A zero-height band across the middle of the window: the sentinel on it is the current step.
    const observer = new IntersectionObserver(
      (entries) => {
        for (const entry of entries) {
          const index = Number((entry.target as HTMLElement).dataset['step']);
          if (entry.isIntersecting) {
            intersecting.current.add(index);
          } else {
            intersecting.current.delete(index);
          }
        }
        followScroll();
      },
      { rootMargin: '-50% 0px -50% 0px' },
    );
    sentinels.current.forEach((sentinel) => {
      if (sentinel) {
        observer.observe(sentinel);
      }
    });

    return () => {
      observer.disconnect();
      window.clearTimeout(settleTimer.current);
    };
  }, [followScroll]);

  // Scrolls to the middle of the step's stretch, kept inside the range where the stage is pinned.
  const scrollToStep = (index: number) => {
    const element = track.current;
    if (!element) {
      return;
    }
    const rect = element.getBoundingClientRect();
    const pinnedRange = Math.max(rect.height - window.innerHeight, 0);
    const stepMiddle = ((index + 0.5) * rect.height) / STEPS.length;
    const offset = Math.min(Math.max(stepMiddle - window.innerHeight / 2, 0), pinnedRange);
    const reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    window.scrollTo({ top: rect.top + window.scrollY + offset, behavior: reduceMotion ? 'auto' : 'smooth' });
  };

  const selectStep = (index: number) => {
    setActive(index);
    if (!window.matchMedia(DESKTOP_QUERY).matches) {
      return;
    }

    locked.current = true;
    window.clearTimeout(settleTimer.current);
    settleTimer.current = window.setTimeout(() => {
      locked.current = false;
      followScroll();
    }, SCROLL_LOCK_MS);
    scrollToStep(index);
  };

  return (
    <section
      id="how-it-works"
      tabIndex={-1}
      className="px-4 py-14 outline-none sm:px-10 lg:px-16 lg:py-0"
    >
      <div className="@container home-tier mx-auto w-full max-w-home">
        <div ref={track} className="relative lg:h-[calc(100svh*2.65/var(--home-tier,1))]">
          <div className="flex flex-col gap-8 lg:sticky lg:top-0 lg:h-[calc(100svh/var(--home-tier,1))] lg:flex-row lg:items-center lg:gap-12">
            <div className="flex shrink-0 flex-col gap-7 lg:w-95 @min-[1340px]:lg:w-100 @min-[1592px]:lg:w-110 @min-[1940px]:lg:w-130">
              <h2
                id="how-it-works-title"
                className="font-display text-home-heading font-semibold tracking-heading text-ink"
              >
                {t('home.how.title')}
              </h2>
              <div role="group" aria-labelledby="how-it-works-title" className="flex flex-col gap-1.5">
                {STEPS.map((step, index) => {
                  const isActive = index === active;
                  return (
                    <button
                      key={step}
                      type="button"
                      aria-pressed={isActive}
                      onClick={() => selectStep(index)}
                      className={`relative flex items-start gap-4 overflow-hidden rounded-2xl px-5 py-4.5 text-left outline-none focus-visible:ring-3 focus-visible:ring-ring/50 ${
                        isActive ? 'bg-sunken' : 'bg-transparent hover:bg-sunken/60'
                      }`}
                    >
                      <span
                        className={`flex size-[30px] shrink-0 items-center justify-center rounded-full text-home-body font-bold ${
                          isActive ? 'bg-marker-solid text-on-dark' : 'bg-sunken text-muted-foreground'
                        }`}
                      >
                        {index + 1}
                      </span>
                      <span className="flex flex-col gap-1">
                        <span className="text-home-feature-title font-semibold text-ink">
                          {t(`home.steps.${step}.title`)}
                        </span>
                        <span className="text-home-copy text-muted-foreground">{t(`home.how.steps.${step}`)}</span>
                      </span>
                      {isActive && (
                        <span
                          key={active}
                          aria-hidden="true"
                          className="hm-fill absolute bottom-0 left-0 h-[3px] w-full origin-left bg-marker-solid"
                        />
                      )}
                    </button>
                  );
                })}
              </div>
            </div>
            <Loop className="min-w-0 grow">
              <div
                aria-hidden="true"
                className={`relative flex items-center justify-center overflow-hidden rounded-3xl border border-border bg-surface shadow-card h-[calc(660px*min(var(--mz,1),var(--home-cap-mock)))] ${STAGE_ZOOM}`}
              >
                <div key={active} className="home-zoom-mock flex h-[660px] w-[700px] shrink-0 items-center justify-center">
                  {PANEL_BY_STEP[active]}
                </div>
              </div>
            </Loop>
          </div>
          <div aria-hidden="true" className="pointer-events-none absolute inset-0 hidden flex-col lg:flex">
            {STEPS.map((step, index) => (
              <div
                key={step}
                ref={(element) => {
                  sentinels.current[index] = element;
                }}
                data-step={index}
                className="flex-1"
              />
            ))}
          </div>
        </div>
      </div>
    </section>
  );
}
