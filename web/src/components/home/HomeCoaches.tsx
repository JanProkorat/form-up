// Pixel sizes and positions in this file are illustration drawing copied from the board, not layout tokens.
import { useTranslation } from 'react-i18next';
import { Loop, PointItem, Row } from '@/components/home/MockupParts';
import { WorkoutPhone } from '@/components/home/HomePhones';
import { IconHash, IconPulse, IconVideo } from '@/components/home/HomeIcons';

interface SessionRow {
  exercise: 'benchPress' | 'barbellRow' | 'overheadPress' | 'pullUp';
  /** `null` means "3 × max". */
  detail: string | null;
}

const SESSION_ROWS: SessionRow[] = [
  { exercise: 'benchPress', detail: '4 × 8 · 60 kg' },
  { exercise: 'barbellRow', detail: '4 × 10 · 50 kg' },
  { exercise: 'overheadPress', detail: '3 × 10 · 30 kg' },
  { exercise: 'pullUp', detail: null },
];

const PIPS = ['hm-c1', 'hm-c2', 'hm-c3', 'hm-c4'] as const;

// Board canvas = 810 wide. Width ladder: the text column (420) plus the gap leave the rest of the
// container; below lg the mockup sits under the text and gets the whole container.
const MOCKUP_ZOOM =
  'home-zoom-mock max-lg:[--mz:0.68] max-lg:@min-[640px]:[--mz:0.78] max-lg:@min-[720px]:[--mz:0.88] max-lg:@min-[800px]:[--mz:0.97] max-lg:@min-[830px]:[--mz:1] lg:[--mz:0.5] lg:@min-[960px]:[--mz:0.58] lg:@min-[1040px]:[--mz:0.68] lg:@min-[1120px]:[--mz:0.78] lg:@min-[1200px]:[--mz:0.88] lg:@min-[1290px]:[--mz:0.99] lg:@min-[1340px]:[--mz:1.03] lg:@min-[1472px]:[--mz:1.1] lg:@min-[1592px]:[--mz:1.2] lg:@min-[1760px]:[--mz:1.3] lg:@min-[1940px]:[--mz:1.45]';

function SessionMockup() {
  const { t } = useTranslation();

  return (
    <Loop>
      <div aria-hidden="true" className={`relative h-[550px] w-[810px] ${MOCKUP_ZOOM}`}>
        <div className="absolute top-10 left-0 box-border flex w-[560px] flex-col gap-2.5 rounded-card border border-border bg-surface p-[22px] shadow-card">
          <div className="mb-1.5 flex items-center gap-2.5">
            <span className="text-copy font-bold text-ink">{t('home.mock.workoutName')}</span>
            <span className="text-meta text-muted-foreground">{t('home.mock.sessionTemplate')}</span>
            <span className="ml-auto flex items-center gap-2 rounded-lg bg-error-soft px-2.5 py-1.5 text-caption font-bold tracking-caps text-marker-text uppercase">
              <span className="hm-live size-2 rounded-full bg-marker" />
              {t('home.mock.liveTitle')}
            </span>
          </div>
          {SESSION_ROWS.map((row, index) => (
            <Row key={row.exercise} tone={index === 0 ? 'active' : 'subtle'}>
              <span className="flex min-w-0 flex-1 flex-col gap-0.5">
                <span className="text-body font-semibold text-ink">{t(`home.mock.exercises.${row.exercise}`)}</span>
                <span className="text-caption text-muted-foreground">{row.detail ?? t('home.mock.setsMax')}</span>
              </span>
              {index === 0 && (
                <span className="flex gap-[5px]">
                  {PIPS.map((pip) => (
                    <span key={pip} className="h-2 w-7 overflow-hidden rounded-sm bg-border">
                      <i className={`${pip} block h-full origin-left bg-marker`} />
                    </span>
                  ))}
                </span>
              )}
              {index === 1 && <span className="text-caption text-muted-foreground">{t('home.mock.next')}</span>}
            </Row>
          ))}
        </div>
        <WorkoutPhone className="absolute top-0 right-0" />
      </div>
    </Loop>
  );
}

/** "For coaches": training-plan pitch with a session mockup that plays an 8 s loop. */
export default function HomeCoaches() {
  const { t } = useTranslation();

  return (
    <section
      id="for-coaches"
      tabIndex={-1}
      className="flex flex-col justify-center overflow-x-clip px-4 py-14 outline-none sm:px-10 lg:home-section-pad lg:min-h-svh lg:px-16"
    >
      <div className="@container home-tier mx-auto flex w-full max-w-home flex-col">
        <div className="flex flex-col gap-12 lg:flex-row lg:items-center lg:gap-16">
          <div className="flex max-w-105 @min-[1340px]:max-w-109 @min-[1472px]:max-w-115.5 @min-[1592px]:max-w-126 @min-[1760px]:max-w-136.5 @min-[1940px]:max-w-152 shrink-0 flex-col gap-4.5">
            <h2 className="font-display text-home-heading font-semibold tracking-heading text-ink">
              {t('home.coaches.title')}
            </h2>
            <p className="text-home-lead text-ink-2">{t('home.coaches.lead')}</p>
            <div className="flex flex-col gap-3.5 pt-1.5">
              <PointItem icon={<IconHash />} tone="bg-training-soft text-training">
                {t('home.coaches.points.sessions')}
              </PointItem>
              <PointItem icon={<IconVideo />} tone="bg-training-soft text-training">
                {t('home.coaches.points.library')}
              </PointItem>
              <PointItem icon={<IconPulse />} tone="bg-training-soft text-training">
                {t('home.coaches.points.watch')}
              </PointItem>
            </div>
          </div>
          <div className="hidden min-w-0 grow justify-center @min-[640px]:flex">
            <SessionMockup />
          </div>
        </div>
      </div>
    </section>
  );
}
