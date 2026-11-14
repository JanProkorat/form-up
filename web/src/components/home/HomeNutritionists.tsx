// Pixel sizes and positions in this file are illustration drawing copied from the board, not layout tokens.
import { useTranslation } from 'react-i18next';
import { Loop, PointItem, Ring, Row, RowText } from '@/components/home/MockupParts';
import { IconCart, IconLines, IconSearch } from '@/components/home/HomeIcons';

const MACROS = [
  { key: 'protein', grams: 142, bar: 'bg-macro-protein', width: 'w-[90%]' },
  { key: 'carbs', grams: 196, bar: 'bg-macro-carbs', width: 'w-[84%]' },
  { key: 'fat', grams: 58, bar: 'bg-macro-fat', width: 'w-[80%]' },
] as const;

const SHOPPING = [
  { item: 'chickenBreast', amount: '900 g', tick: 'hm-s-1' },
  { item: 'basmatiRice', amount: '500 g', tick: 'hm-s-2' },
  { item: 'greekYogurt', amount: '1.2 kg', tick: 'hm-s-3' },
] as const;

const DAY_TARGET = 2100;
const TOTAL_BEFORE = 1220;
const TOTAL_AFTER = 1860;

// Board canvas = 700 wide. Width ladder: the text column (420) plus the gap leave the rest of the
// container; below lg the mockup sits above the text and gets the whole container.
const MOCKUP_ZOOM =
  'home-zoom-mock max-lg:[--mz:0.8] max-lg:@min-[640px]:[--mz:0.9] max-lg:@min-[720px]:[--mz:1] lg:[--mz:0.54] lg:@min-[960px]:[--mz:0.62] lg:@min-[1040px]:[--mz:0.72] lg:@min-[1120px]:[--mz:0.83] lg:@min-[1200px]:[--mz:0.94] lg:@min-[1290px]:[--mz:1] lg:@min-[1340px]:[--mz:1.03] lg:@min-[1472px]:[--mz:1.1] lg:@min-[1592px]:[--mz:1.2] lg:@min-[1760px]:[--mz:1.3] lg:@min-[1940px]:[--mz:1.45]';

function NutritionMockup() {
  const { t, i18n } = useTranslation();
  const format = (value: number) => value.toLocaleString(i18n.language);
  const target = `/ ${format(DAY_TARGET)} ${t('home.mock.kcal')}`;

  return (
    <Loop>
      <div aria-hidden="true" className={`relative h-[540px] w-[700px] ${MOCKUP_ZOOM}`}>
        <div className="absolute top-0 left-0 box-border flex w-[420px] flex-col gap-2.5 rounded-card border border-border bg-surface p-[22px] shadow-card">
          <div className="flex items-baseline gap-2.5">
            <span className="text-copy font-bold text-ink">{t('home.mock.dayWednesday')}</span>
            <span className="text-meta text-muted-foreground">{t('home.mock.planShort')}</span>
          </div>
          <div className="relative h-10">
            <span className="hm-old absolute top-0 left-0 font-display text-[32px] font-semibold text-ink">
              {format(TOTAL_BEFORE)} <span className="font-sans text-copy text-muted-foreground">{target}</span>
            </span>
            <span className="hm-new-total absolute top-0 left-0 font-display text-[32px] font-semibold text-ink">
              {format(TOTAL_AFTER)} <span className="font-sans text-copy text-muted-foreground">{target}</span>
            </span>
            <span className="hm-plus absolute top-2 left-[190px] text-copy font-bold text-nutrition">
              +640
            </span>
          </div>
          <div className="mb-2 grid grid-cols-[64px_minmax(0,1fr)_44px] items-center gap-x-2.5 gap-y-2 text-meta">
            {MACROS.map((macro) => (
              <MacroLine key={macro.key} macro={macro} grams={macro.grams} />
            ))}
          </div>
          <Row>
            <RowText title={t('home.mock.foods.eggToast')} sub={t('home.mock.meals.breakfast')} subFirst />
            <span className="text-caption text-muted-foreground">460</span>
          </Row>
          <Row>
            <RowText title={t('home.mock.foods.appleAlmonds')} sub={t('home.mock.meals.snack')} subFirst />
            <span className="text-caption text-muted-foreground">210</span>
          </Row>
          <div className="relative h-14">
            <div className="hm-nhole absolute inset-0 flex items-center rounded-xl border-[1.5px] border-dashed border-muted-foreground px-3.5 text-meta text-muted-foreground">
              {t('home.mock.dropHere')}
            </div>
            <Row tone="accent" className="hm-drop absolute inset-0 shadow-popover">
              <RowText title={t('home.mock.foods.chickenBowl')} sub={t('home.mock.meals.lunch')} subFirst />
              <span className="text-caption text-muted-foreground">640</span>
            </Row>
          </div>
          <Row>
            <RowText title={t('home.mock.foods.beefStirFry')} sub={t('home.mock.meals.dinner')} subFirst />
            <span className="text-caption text-muted-foreground">550</span>
          </Row>
        </div>

        <div className="absolute top-[250px] left-[380px] box-border flex w-[300px] flex-col gap-2 rounded-card border border-border bg-surface p-[18px] shadow-card">
          <span className="mb-1 text-copy font-bold text-ink">{t('home.mock.shoppingList')}</span>
          {SHOPPING.map((entry) => (
            <Row key={entry.item}>
              <Ring ticked tickClassName={entry.tick} />
              <span className="flex-1 text-body font-semibold text-ink">
                {t(`home.mock.shopping.${entry.item}`)}
              </span>
              <span className="text-caption text-muted-foreground">{entry.amount}</span>
            </Row>
          ))}
        </div>
      </div>
    </Loop>
  );
}

function MacroLine({ macro, grams }: { macro: (typeof MACROS)[number]; grams: number }) {
  const { t } = useTranslation();

  return (
    <>
      <span className="text-muted-foreground">{t(`home.mock.macros.${macro.key}`)}</span>
      <span className="h-2 overflow-hidden rounded-xs bg-sunken">
        <span className={`hm-nbar block h-full ${macro.width} ${macro.bar}`} />
      </span>
      <span className="font-bold text-ink">
        {t('home.mock.grams', { value: grams })}
      </span>
    </>
  );
}

/** "For nutritionists": meal-plan pitch with a mockup that plays an 8 s loop. */
export default function HomeNutritionists() {
  const { t } = useTranslation();

  return (
    <section
      id="for-nutritionists"
      tabIndex={-1}
      className="flex flex-col justify-center overflow-x-clip px-4 py-14 outline-none sm:px-10 lg:home-section-pad lg:min-h-svh lg:px-16"
    >
      <div className="@container home-tier mx-auto flex w-full max-w-home flex-col">
        <div className="flex flex-col gap-12 lg:flex-row-reverse lg:items-center lg:gap-16">
          <div className="flex max-w-105 @min-[1340px]:max-w-109 @min-[1472px]:max-w-115.5 @min-[1592px]:max-w-126 @min-[1760px]:max-w-136.5 @min-[1940px]:max-w-152 shrink-0 flex-col gap-4.5">
            <h2 className="font-display text-home-heading font-semibold tracking-heading text-ink">
              {t('home.nutritionists.title')}
            </h2>
            <p className="text-home-lead text-ink-2">{t('home.nutritionists.lead')}</p>
            <div className="flex flex-col gap-3.5 pt-1.5">
              <PointItem icon={<IconSearch />} tone="bg-nutrition-soft text-nutrition-ink">
                {t('home.nutritionists.points.foods')}
              </PointItem>
              <PointItem icon={<IconLines />} tone="bg-nutrition-soft text-nutrition-ink">
                {t('home.nutritionists.points.macros')}
              </PointItem>
              <PointItem icon={<IconCart />} tone="bg-nutrition-soft text-nutrition-ink">
                {t('home.nutritionists.points.shopping')}
              </PointItem>
            </div>
          </div>
          <div className="hidden min-w-0 grow justify-center @min-[640px]:flex">
            <NutritionMockup />
          </div>
        </div>
      </div>
    </section>
  );
}
