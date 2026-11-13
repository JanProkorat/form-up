import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Loop, StoreLinks } from '@/components/home/MockupParts';
import { ClientAppPhone, CoachAppPhone } from '@/components/home/HomePhones';

// The phones are 250 wide; the pair is 548 wide. Below lg they wrap and keep their size, from lg the
// width ladder zooms them to the room beside the text column (420 + 80 gap), capped by window height.
const PHONES_ZOOM =
  'lg:home-zoom-mock lg:[--mz:0.7] lg:@min-[960px]:[--mz:0.82] lg:@min-[1040px]:[--mz:0.96] lg:@min-[1100px]:[--mz:1] lg:@min-[1340px]:[--mz:1.03] lg:@min-[1472px]:[--mz:1.1] lg:@min-[1592px]:[--mz:1.2] lg:@min-[1760px]:[--mz:1.3] lg:@min-[1940px]:[--mz:1.45]';

function LabelledPhone({
  label,
  floatClassName,
  className = '',
  children,
}: {
  label: string;
  floatClassName: string;
  className?: string;
  children: ReactNode;
}) {
  return (
    <div className={`flex flex-col items-center gap-3.5 ${className}`}>
      <span className="text-home-body font-bold text-muted-foreground">{label}</span>
      <div aria-hidden="true" className={floatClassName}>
        {children}
      </div>
    </div>
  );
}

/** "The mobile app": one app, a client view and a coach view, as two gently floating phones. */
export default function HomeMobileApp() {
  const { t } = useTranslation();

  return (
    <section
      id="mobile-app"
      tabIndex={-1}
      className="flex flex-col justify-center overflow-x-clip px-4 py-14 outline-none sm:px-10 lg:home-section-pad lg:min-h-svh lg:px-16"
    >
      <div className="@container home-tier mx-auto flex w-full max-w-home flex-col">
        <div className="flex flex-col gap-12 lg:flex-row lg:items-center lg:gap-20">
          <div className="flex max-w-105 @min-[1340px]:max-w-109 @min-[1472px]:max-w-115.5 @min-[1592px]:max-w-126 @min-[1760px]:max-w-136.5 @min-[1940px]:max-w-152 shrink-0 flex-col gap-4.5">
            <h2 className="font-display text-home-heading font-semibold tracking-heading text-ink">
              {t('home.app.title')}
            </h2>
            <p className="text-home-lead text-ink-2">{t('home.app.lead')}</p>
            <div className="flex flex-wrap gap-2.5 pt-1.5">
              <StoreLinks appStore={t('home.app.appStore')} googlePlay={t('home.app.googlePlay')} tone="solid" />
            </div>
          </div>
          <Loop className="flex min-w-0 grow justify-center">
            <div className={`flex flex-wrap items-start justify-center gap-x-12 gap-y-8 ${PHONES_ZOOM}`}>
              <LabelledPhone label={t('home.app.phones.client')} floatClassName="hm-float-1">
                <ClientAppPhone />
              </LabelledPhone>
              <LabelledPhone label={t('home.app.phones.you')} floatClassName="hm-float-2" className="lg:pt-[50px]">
                <CoachAppPhone />
              </LabelledPhone>
            </div>
          </Loop>
        </div>
      </div>
    </section>
  );
}
