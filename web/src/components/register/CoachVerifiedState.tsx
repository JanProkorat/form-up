import { Link } from 'react-router-dom';
import { CalendarIcon, CheckIcon, ChevronRightIcon, MessageSquareIcon } from 'lucide-react';
import type { LucideIcon } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/button';
import RegisterStepper from '@/components/register/RegisterStepper';
import { REGISTER_PLAN_PATH } from '@/components/subscription/planLinks';

interface CoachVerifiedStateProps {
  firstName: string;
  /** Primary action target (the plan step); "/login" (carrying return-to the plan step) when the stored session is stale. */
  planTo: typeof REGISTER_PLAN_PATH | '/login';
  /** Skip link target (start on Free); "/login" when the stored session is stale and cannot pass the auth guard. */
  skipTo: '/clients' | '/login';
}

function NextStep({ icon: Icon, title, hint }: { icon: LucideIcon; title: string; hint: string }) {
  return (
    <li className="flex items-center gap-3 border-t border-border py-3 text-left">
      <span className="flex size-8.5 shrink-0 items-center justify-center rounded-md border border-border bg-surface text-ink-2">
        <Icon className="size-4" aria-hidden="true" />
      </span>
      <span className="flex flex-col gap-0.5">
        <span className="text-copy font-semibold text-ink">{title}</span>
        <span className="text-meta text-muted-foreground">{hint}</span>
      </span>
    </li>
  );
}

/** Final register step for coaches: the account is verified, next they set up a profile or enter the portal. */
export default function CoachVerifiedState({ firstName, planTo, skipTo }: CoachVerifiedStateProps) {
  const { t } = useTranslation();

  return (
    <main className="flex flex-1 flex-col items-center px-4 pt-8 pb-16 short:pt-2 short:pb-6 sm:px-10 sm:py-12 short:sm:py-4">
      <div className="my-auto flex w-full max-w-140 flex-col items-center gap-4.5 rounded-glass border border-border bg-surface px-5 pt-8 pb-9 text-center shadow-card short:gap-3 short:pt-5 short:pb-6 sm:px-10">
        <div className="self-stretch pb-2 text-left">
          <RegisterStepper current={3} finalStep="profile" />
        </div>

        <span className="flex size-21 items-center justify-center rounded-full bg-success-soft text-success-ink short:size-14">
          <CheckIcon className="size-9.5 short:size-7" strokeWidth={2} aria-hidden="true" />
        </span>

        <h1 className="font-display text-display font-semibold tracking-heading text-ink short:text-card-title">
          {t('entry.verifyEmail.success.title')}
        </h1>
        <p className="text-subhead text-ink-2">
          {firstName
            ? t('entry.verifyEmail.getApp.welcomeNamed', { firstName })
            : t('entry.verifyEmail.getApp.welcome')}{' '}
          {t('entry.verifyEmail.coach.ready')}
        </p>

        <Button asChild className="h-12 gap-2 self-stretch rounded-field text-subhead font-bold">
          <Link
            to={planTo}
            replace
            state={planTo === '/login' ? { from: REGISTER_PLAN_PATH } : undefined}
          >
            {t('entry.verifyEmail.coach.choosePlan')}
            <ChevronRightIcon aria-hidden="true" />
          </Link>
        </Button>
        <Link
          to={skipTo}
          replace
          className="text-body font-semibold text-muted-foreground underline hover:text-ink"
        >
          {t('entry.verifyEmail.coach.skip')}
        </Link>

        <section className="flex flex-col self-stretch pt-1.5">
          <h2 className="pb-1 text-left text-caption font-bold tracking-eyebrow text-muted-foreground">
            {t('entry.verifyEmail.coach.nextHeading')}
          </h2>
          <ul className="flex flex-col">
            <NextStep
              icon={MessageSquareIcon}
              title={t('entry.verifyEmail.coach.inviteTitle')}
              hint={t('entry.verifyEmail.coach.inviteHint')}
            />
            <NextStep
              icon={CalendarIcon}
              title={t('entry.verifyEmail.coach.buildTitle')}
              hint={t('entry.verifyEmail.coach.buildHint')}
            />
          </ul>
        </section>
      </div>
    </main>
  );
}
