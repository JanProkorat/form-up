// Pixel sizes and positions in this file are illustration drawing copied from the board, not layout tokens.
import { useTranslation } from 'react-i18next';
import { Cell, GridLabel, PhoneShell, Ring, Row } from '@/components/home/MockupParts';

type FoodKey =
  | 'oatBowl'
  | 'yogurtBowl'
  | 'eggToast'
  | 'lentilCurry'
  | 'tunaSalad'
  | 'chickenBowl'
  | 'salmonPotatoes'
  | 'beefStirFry'
  | 'tofuNoodles';

interface PlanItem {
  food: FoodKey;
  kcal: number;
}

const DAYS = ['mon', 'tue', 'wed', 'thu'] as const;

const BREAKFAST: PlanItem[] = [
  { food: 'oatBowl', kcal: 420 },
  { food: 'yogurtBowl', kcal: 380 },
  { food: 'eggToast', kcal: 460 },
  { food: 'oatBowl', kcal: 420 },
];

const DINNER: PlanItem[] = [
  { food: 'salmonPotatoes', kcal: 610 },
  { food: 'beefStirFry', kcal: 590 },
  { food: 'tofuNoodles', kcal: 540 },
  { food: 'beefStirFry', kcal: 590 },
];

const CHECK_IN_SWATCHES = ['bg-photo-sand', 'bg-photo-tan', 'bg-photo-clay'] as const;

/** Hero collage, a 10 s loop: a recipe is dragged into Wednesday lunch, the week is published, the phone ticks off. */
export default function HeroMockup() {
  const { t } = useTranslation();
  const kcal = (value: number) => `${value} ${t('home.mock.kcal')}`;

  return (
    <div
      aria-hidden="true"
      className="h-[580px] w-[816px] lg:[zoom:0.43] lg:@min-[960px]:[zoom:0.53] lg:@min-[1040px]:[zoom:0.63] lg:@min-[1120px]:[zoom:0.73] lg:@min-[1200px]:[zoom:0.83] lg:@min-[1286px]:[zoom:0.94] lg:@min-[1340px]:[zoom:0.97] lg:@min-[1472px]:[zoom:1.03] lg:@min-[1592px]:[zoom:1.13] lg:@min-[1760px]:[zoom:1.22] lg:@min-[1940px]:[zoom:1.36]"
    >
      <div className="relative h-full w-full">
        <div className="absolute top-[50px] left-0 box-border w-[620px] rounded-card border border-border bg-surface p-4 shadow-card">
          <div className="mb-2.5 flex h-10 items-center gap-2.5">
            <span className="text-copy font-bold text-ink">{t('home.mock.planTitle')}</span>
            <span className="text-meta text-muted-foreground">{t('home.mock.weekShort')}</span>
            <span className="hm-badge rounded-md bg-nutrition-soft px-2 py-1 text-mockup-xs font-bold tracking-caps text-nutrition uppercase">
              {t('home.mock.published')}
            </span>
            <span className="hm-press ml-auto flex h-[34px] items-center rounded-[9px] bg-primary px-3.5 text-meta font-bold text-primary-foreground">
              {t('home.mock.publishWeek')}
            </span>
          </div>
          <div className="grid grid-cols-[150px_64px_repeat(4,minmax(0,1fr))] grid-rows-[24px_repeat(3,64px)] gap-1.5">
            <GridLabel className="pl-1">{t('home.mock.recipes')}</GridLabel>
            <span />
            {DAYS.map((day) => (
              <GridLabel key={day}>{t(`home.mock.days.${day}`)}</GridLabel>
            ))}

            <Cell tone="outline" name={t('home.mock.foods.oatBowl')} kcal={kcal(420)} />
            <GridLabel>{t('home.mock.meals.breakfast')}</GridLabel>
            {BREAKFAST.map((item, index) => (
              <Cell key={index} name={t(`home.mock.foods.${item.food}`)} kcal={kcal(item.kcal)} />
            ))}

            <Cell tone="outline" name={t('home.mock.foods.chickenBowl')} kcal={kcal(640)} />
            <GridLabel>{t('home.mock.meals.lunch')}</GridLabel>
            <Cell name={t('home.mock.foods.lentilCurry')} kcal={kcal(560)} />
            <Cell name={t('home.mock.foods.tunaSalad')} kcal={kcal(520)} />
            <div className="relative">
              <div className="hm-hole absolute inset-0 rounded-field border-[1.5px] border-dashed border-muted-foreground" />
              <Cell
                tone="accent"
                name={t('home.mock.foods.chickenBowl')}
                kcal={kcal(640)}
                className="hm-chip absolute inset-0 shadow-popover"
              />
              <svg
                width="22"
                height="22"
                viewBox="0 0 24 24"
                aria-hidden="true"
                className="hm-cursor absolute top-0 left-0 z-10"
              >
                <path
                  d="M4 2l15 9-6.5 1.6L16 20l-3 1.4-3.4-7.4L4 18z"
                  strokeWidth="1.4"
                  strokeLinejoin="round"
                  className="fill-primary stroke-primary-foreground"
                />
              </svg>
            </div>
            <Cell name={t('home.mock.foods.lentilCurry')} kcal={kcal(560)} />

            <Cell tone="outline" name={t('home.mock.foods.tofuNoodles')} kcal={kcal(540)} />
            <GridLabel>{t('home.mock.meals.dinner')}</GridLabel>
            {DINNER.map((item, index) => (
              <Cell key={index} name={t(`home.mock.foods.${item.food}`)} kcal={kcal(item.kcal)} />
            ))}
          </div>
        </div>

        <PhoneShell size="hero" className="absolute top-0 right-0">
          <div className="hm-notif absolute inset-x-2 top-2 z-[2] flex items-center gap-2.5 rounded-2xl border border-border bg-sunken px-3 py-2.5">
            <span className="size-[26px] shrink-0 rounded-thumb bg-marker" />
            <span className="flex flex-col gap-px">
              <span className="text-mockup-notif font-bold text-ink">{t('home.mock.notifReadyTitle')}</span>
              <span className="text-mockup-xs text-muted-foreground">{t('home.mock.notifReadyBody')}</span>
            </span>
          </div>
          <span className="text-caption text-muted-foreground">{t('home.mock.dayWednesday')}</span>
          <span className="mb-1.5 font-display text-mockup-title font-semibold text-ink">
            {t('home.mock.todaysMeals')}
          </span>
          <PhoneMeal kind="breakfast" food="eggToast" kcal={460} tickClassName="hm-t1" />
          <PhoneMeal kind="snack" food="appleAlmonds" kcal={210} tickClassName="hm-t2" />
          <PhoneMeal kind="lunch" food="chickenBowl" kcal={640} tickClassName="hm-t3" highlighted />
          <PhoneMeal kind="dinner" food="beefStirFry" kcal={590} />
        </PhoneShell>

        <div className="hm-checkin absolute top-[440px] left-10 box-border flex w-[400px] items-center gap-3.5 rounded-2xl border border-border bg-surface px-4 py-3.5 shadow-card">
          <span className="flex size-9 shrink-0 items-center justify-center rounded-full bg-marker-solid text-body font-bold text-on-dark">
            {t('home.mock.petraInitial')}
          </span>
          <span className="flex flex-1 flex-col gap-[3px]">
            <span className="text-body font-bold text-ink">{t('home.mock.checkinSent')}</span>
            <span className="text-meta text-muted-foreground">
              {t('home.mock.weightValue')} {t('home.mock.kg')} ·{' '}
              <span className="font-semibold text-nutrition">{t('home.mock.weightDelta')}</span> ·{' '}
              {t('home.mock.checkinEnergy')}
            </span>
          </span>
          <span className="flex gap-1">
            {CHECK_IN_SWATCHES.map((tint) => (
              <span key={tint} className={`h-10 w-[30px] rounded-md ${tint}`} />
            ))}
          </span>
        </div>
      </div>
    </div>
  );
}

interface PhoneMealProps {
  kind: 'breakfast' | 'snack' | 'lunch' | 'dinner';
  food: 'eggToast' | 'appleAlmonds' | 'chickenBowl' | 'beefStirFry';
  kcal: number;
  tickClassName?: string;
  /** The meal the coach changed; pulses in with the notification. */
  highlighted?: boolean;
}

function PhoneMeal({ kind, food, kcal, tickClassName, highlighted = false }: PhoneMealProps) {
  const { t } = useTranslation();

  return (
    <Row tone={highlighted ? 'accent' : 'subtle'} className={highlighted ? 'hm-new' : ''}>
      <Ring ticked={tickClassName !== undefined} tickClassName={tickClassName} />
      <span className="flex min-w-0 flex-1 flex-col gap-0.5">
        <span className="text-mockup-xs text-muted-foreground">{t(`home.mock.meals.${kind}`)}</span>
        <span className="text-body font-semibold text-ink">{t(`home.mock.foods.${food}`)}</span>
      </span>
      <span className="text-caption text-muted-foreground">{kcal}</span>
    </Row>
  );
}
