// Pixel sizes and positions in this file are illustration drawing copied from the board, not layout tokens.
import { useTranslation } from 'react-i18next';
import { PhoneShell, Ring, Row, RowText } from '@/components/home/MockupParts';

const SET_ROWS = [1, 2, 3, 4] as const;
const SET_TICKS = ['hm-f1', 'hm-f2', 'hm-f3', 'hm-f4'] as const;

/** Client app, live workout: sets tick off one by one (coach section, 8 s loop). */
export function WorkoutPhone({ className = '' }: { className?: string }) {
  const { t } = useTranslation();

  return (
    <PhoneShell className={className}>
      <span className="text-caption text-muted-foreground">{t('home.mock.todayMeta')}</span>
      <span className="font-display text-mockup-title font-semibold text-ink">
        {t('home.mock.exercises.benchPress')}
      </span>
      <span className="mb-1.5 text-meta text-muted-foreground">{t('home.mock.blockA')}</span>
      {SET_ROWS.map((set, index) => (
        <Row key={set}>
          <Ring ticked tickClassName={SET_TICKS[index]} />
          <span className="flex-1 text-body font-semibold text-ink">{t('home.mock.setN', { n: set })}</span>
          <span className="text-caption text-muted-foreground">{t('home.mock.setDetail')}</span>
        </Row>
      ))}
      <span className="mt-auto flex h-11 items-center justify-center rounded-xl bg-primary text-copy font-bold text-primary-foreground">
        {t('home.mock.logSet')}
      </span>
    </PhoneShell>
  );
}

/** Client app, home screen (mobile section, "Your client"). */
export function ClientAppPhone() {
  const { t } = useTranslation();

  return (
    <PhoneShell>
      <span className="text-caption text-muted-foreground">{t('home.mock.dayThursday')}</span>
      <span className="mb-2 font-display text-mockup-greeting font-semibold text-ink">
        {t('home.mock.greeting')}
      </span>
      <Row>
        <Ring ticked />
        <span className="flex-1 text-body font-semibold text-ink">{t('home.mock.foods.eggToast')}</span>
        <span className="text-caption text-muted-foreground">460</span>
      </Row>
      <Row>
        <Ring ticked />
        <span className="flex-1 text-body font-semibold text-ink">{t('home.mock.foods.appleAlmonds')}</span>
        <span className="text-caption text-muted-foreground">210</span>
      </Row>
      <Row tone="accent">
        <Ring />
        <span className="flex-1 text-body font-semibold text-ink">{t('home.mock.foods.chickenBowl')}</span>
        <span className="text-caption text-muted-foreground">640</span>
      </Row>
      <Row>
        <Ring />
        <span className="flex-1 text-body font-semibold text-ink">{t('home.mock.foods.beefStirFry')}</span>
        <span className="text-caption text-muted-foreground">590</span>
      </Row>
      <Row tone="dark" className="mt-auto">
        <span className="flex flex-1 flex-col gap-0.5">
          <span className="text-body font-semibold">{t('home.mock.workoutName')}</span>
          <span className="text-caption opacity-70">{t('home.mock.workoutMinutes')}</span>
        </span>
        <span className="rounded-lg bg-marker-solid px-3 py-1.5 text-meta font-bold text-on-dark">
          {t('home.mock.start')}
        </span>
      </Row>
    </PhoneShell>
  );
}

interface ClientRow {
  name: string;
  note: 'checkinWaiting' | 'missedMeals' | 'workoutDone' | 'allMeals';
  avatar: string;
}

const NEEDS_YOU: ClientRow[] = [
  { name: 'Petra N.', note: 'checkinWaiting', avatar: 'bg-marker-solid text-on-dark' },
  { name: 'Tomáš K.', note: 'missedMeals', avatar: 'bg-training text-on-training' },
];

const ON_TRACK: ClientRow[] = [
  { name: 'Eva M.', note: 'workoutDone', avatar: 'bg-nutrition text-on-nutrition' },
  { name: 'Lukáš P.', note: 'allMeals', avatar: 'bg-nutrition text-on-nutrition' },
];

function ClientListRow({ row }: { row: ClientRow }) {
  const { t } = useTranslation();

  return (
    <Row>
      <span
        className={`flex size-[30px] shrink-0 items-center justify-center rounded-full text-meta font-bold ${row.avatar}`}
      >
        {row.name.charAt(0)}
      </span>
      <RowText title={row.name} sub={t(`home.mock.clientNotes.${row.note}`)} />
    </Row>
  );
}

/** Coach app: clients sorted by who needs attention (mobile section, "You"). */
export function CoachAppPhone() {
  const { t } = useTranslation();

  return (
    <PhoneShell>
      <span className="text-caption text-muted-foreground">{t('home.mock.coachClients')}</span>
      <span className="mb-2 font-display text-mockup-greeting font-semibold text-ink">
        {t('home.mock.clientsTitle')}
      </span>
      <span className="text-caption font-bold tracking-caps text-marker-text uppercase">
        {t('home.mock.needsYou')}
      </span>
      {NEEDS_YOU.map((row) => (
        <ClientListRow key={row.name} row={row} />
      ))}
      <span className="pt-1.5 text-caption font-bold tracking-caps text-muted-foreground uppercase">
        {t('home.mock.onTrack')}
      </span>
      {ON_TRACK.map((row) => (
        <ClientListRow key={row.name} row={row} />
      ))}
    </PhoneShell>
  );
}
