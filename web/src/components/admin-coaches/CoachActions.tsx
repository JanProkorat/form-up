import { useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Ban, CalendarX, Repeat, CircleMinus, Tag } from 'lucide-react';
import { AdminCoachStatus, type GetAdminCoachResponse } from '@/api/admin-coaches';
import type { SubscriptionPlanDto } from '@/api/admin-subscription-plans';
import { Button } from '@/components/ui/button';
import GiveFreeDialog from '@/components/admin-coaches/GiveFreeDialog';
import ReasonDialog, { type TileTone } from '@/components/admin-coaches/ReasonDialog';
import { coachPlanName, formatCoachDate } from '@/components/admin-coaches/coachFormat';
import {
  useCancelCoachRenewal,
  useDisableCoachAccount,
  useEndCoachPlan,
  useGiveCoachPlanFree,
} from '@/components/admin-coaches/useAdminCoachesQueries';
import {
  getErrorCode,
  getErrorStatus,
  getRfc7807ErrorCode,
  showApiError,
  showError,
  showSuccess,
} from '@/lib/api-errors';
import { cn } from '@/lib/utils';

const ERROR_NO_ACTIVE_ROLE = 'NO_ACTIVE_COACH_ROLE';
const ERROR_NO_PLAN_TO_END = 'NO_PLAN_TO_END';
const ERROR_PROFESSIONAL_NOT_FOUND = 'PROFESSIONAL_NOT_FOUND';

const errorCodeOf = (error: unknown): string | null => getRfc7807ErrorCode(error) ?? getErrorCode(error);

/** Reports a failed action. The shared apiErrors text for NO_ACTIVE_COACH_ROLE speaks to the coach, so the admin gets its own. */
function reportFailure(error: unknown, fallbackKey: string, onGone: () => void) {
  const code = errorCodeOf(error);
  const status = getErrorStatus(error);
  if (status === 400 && code === ERROR_NO_ACTIVE_ROLE) {
    showError('adminCoaches.errors.noActiveRole');
    return;
  }
  if (status === 409 && code === ERROR_NO_PLAN_TO_END) {
    showError('adminCoaches.errors.noPlanToEnd');
    return;
  }
  showApiError(error, fallbackKey);
  if (status === 404 && code === ERROR_PROFESSIONAL_NOT_FOUND) {
    onGone();
  }
}

const ROW_TILE_CLASS: Record<'neutral' | 'red', string> = {
  neutral: 'bg-sunken text-ink-2',
  red: 'bg-error-soft text-error',
};

interface RowProps {
  tone: 'neutral' | 'red';
  icon: ReactNode;
  title: string;
  description: string;
  /** The button that opens the dialog; replaced by the note when the action is unavailable. */
  button: ReactNode;
  /** Why the action is not available right now. */
  unavailableNote?: string;
}

function ActionRow({ tone, icon, title, description, button, unavailableNote }: RowProps) {
  return (
    <li className="flex flex-wrap items-center gap-x-4 gap-y-3 border-t border-line py-3.5">
      <span
        aria-hidden="true"
        className={cn('flex size-9 shrink-0 items-center justify-center rounded-field [&_svg]:size-4.5', ROW_TILE_CLASS[tone])}
      >
        {icon}
      </span>
      <div className="flex min-w-0 flex-1 basis-60 flex-col gap-0.5">
        <span className="text-copy font-semibold text-ink">{title}</span>
        <span className="text-body leading-normal text-muted-foreground">{description}</span>
      </div>
      <div className="ml-auto">
        {unavailableNote ? (
          <p className="max-w-60 text-right text-body leading-normal text-muted-foreground">{unavailableNote}</p>
        ) : (
          button
        )}
      </div>
    </li>
  );
}

/** Outline button of an action row; red text for the destructive ones. */
function RowButton({ danger = false, children, ...props }: { danger?: boolean; children: ReactNode; onClick: () => void }) {
  return (
    <Button
      type="button"
      variant="outline"
      size="lg"
      className={cn('px-3.5 font-semibold', danger && 'text-error')}
      {...props}
    >
      {children}
    </Button>
  );
}

type DialogName = 'giveFree' | 'cancelRenewal' | 'endPlan' | 'disable';

interface Props {
  coach: GetAdminCoachResponse;
  coachId: string;
  plans: SubscriptionPlanDto[];
  /** Called when the coach is gone (404), so the page can leave. */
  onGone: () => void;
}

/** The profile's actions card. Every action opens a dialog with a required reason; nothing runs from the page itself. */
export default function CoachActions({ coach, coachId, plans, onGone }: Props) {
  const { t, i18n } = useTranslation();
  const [open, setOpen] = useState<DialogName | null>(null);
  const give = useGiveCoachPlanFree(coachId);
  const cancel = useCancelCoachRenewal(coachId);
  const endPlan = useEndCoachPlan(coachId);
  const disable = useDisableCoachAccount(coachId);

  const firstName = (coach.name ?? '').trim().split(/\s+/)[0] ?? '';
  const planLabel = coachPlanName(coach.plan, i18n.language);
  const periodEnd = formatCoachDate(coach.currentPeriodEndsAt, i18n.language);
  const used = coach.clientsUsed ?? 0;
  const freeMax = coach.freePlanMaxActiveClients ?? null;
  const overFreeLimit = freeMax !== null && used > freeMax;

  const renewing =
    (coach.status === AdminCoachStatus.Paying ||
      coach.status === AdminCoachStatus.PaymentFailed ||
      coach.status === AdminCoachStatus.Cancelling) &&
    coach.currentPeriodEndsAt !== undefined;
  const cancelNote = coach.cancelAtPeriodEnd
    ? t('adminCoaches.actions.cancelRenewal.already')
    : renewing
      ? undefined
      : t('adminCoaches.actions.cancelRenewal.none');
  const endNote = coach.canEndPlanNow === true ? undefined : t('adminCoaches.actions.endPlan.none');

  const close = (name: DialogName) => (next: boolean) => setOpen(next ? name : null);

  /** Runs a mutation, shows the matching toast and tells the dialog whether to close. */
  const attempt = async (action: () => Promise<unknown>, successKey: string, errorKey: string): Promise<boolean> => {
    try {
      await action();
      showSuccess(successKey);
      return true;
    } catch (error) {
      reportFailure(error, errorKey, onGone);
      return false;
    }
  };

  const reasonDialogProps = (name: Exclude<DialogName, 'giveFree'>, tone: TileTone, icon: ReactNode) => ({
    open: open === name,
    onOpenChange: close(name),
    tone,
    icon,
  });

  return (
    <section
      aria-labelledby="coach-actions-heading"
      className="flex flex-col rounded-2xl border border-line bg-card px-6 pt-5.5 pb-2 shadow-panel"
    >
      <div className="flex flex-col gap-1 pb-3.5">
        <h2 id="coach-actions-heading" className="text-subhead font-semibold text-ink">
          {t('adminCoaches.drawer.actions')}
        </h2>
        <p className="text-body leading-normal text-muted-foreground">{t('adminCoaches.profile.actionsHint')}</p>
      </div>
      <ul className="flex flex-col">
        <ActionRow
          tone="neutral"
          icon={<Repeat />}
          title={t('adminCoaches.actions.giveFree.title')}
          description={t('adminCoaches.actions.giveFree.description')}
          button={<RowButton onClick={() => setOpen('giveFree')}>{t('adminCoaches.actions.giveFree.button')}</RowButton>}
        />
        <ActionRow
          tone="neutral"
          icon={<Tag />}
          title={t('adminCoaches.actions.promotion.title')}
          description={t('adminCoaches.actions.promotion.description')}
          button={
            <Button
              type="button"
              variant="outline"
              size="lg"
              className="px-3.5 font-semibold"
              disabled
              title={t('shell.comingSoon')}
            >
              {t('adminCoaches.actions.promotion.button')}
            </Button>
          }
        />
        <ActionRow
          tone="neutral"
          icon={<CalendarX />}
          title={t('adminCoaches.actions.cancelRenewal.title')}
          description={t('adminCoaches.actions.cancelRenewal.description')}
          unavailableNote={cancelNote}
          button={
            <RowButton onClick={() => setOpen('cancelRenewal')}>{t('adminCoaches.actions.cancelRenewal.button')}</RowButton>
          }
        />
        <ActionRow
          tone="red"
          icon={<CircleMinus />}
          title={t('adminCoaches.actions.endPlan.title')}
          description={t('adminCoaches.actions.endPlan.description')}
          unavailableNote={endNote}
          button={
            <RowButton danger onClick={() => setOpen('endPlan')}>
              {t('adminCoaches.actions.endPlan.button')}
            </RowButton>
          }
        />
        <ActionRow
          tone="red"
          icon={<Ban />}
          title={t('adminCoaches.actions.disable.title')}
          description={t('adminCoaches.actions.disable.description')}
          button={
            <RowButton danger onClick={() => setOpen('disable')}>
              {t('adminCoaches.actions.disable.button')}
            </RowButton>
          }
        />
      </ul>

      <GiveFreeDialog
        open={open === 'giveFree'}
        onOpenChange={close('giveFree')}
        coachId={coachId}
        coachFirstName={firstName}
        plans={plans}
        currentPlanCode={coach.plan?.code}
        run={({ planCode, freeUntil, reason }) =>
          attempt(
            () =>
              give.mutateAsync({
                planCode,
                freeUntil: freeUntil ? freeUntil.toISOString() : undefined,
                reason,
              }),
            'adminCoaches.actions.giveFree.success',
            'adminCoaches.actions.giveFree.error',
          )
        }
      />

      <ReasonDialog
        {...reasonDialogProps('cancelRenewal', 'neutral', <CalendarX />)}
        title={t('adminCoaches.actions.cancelRenewal.dialogTitle')}
        description={
          periodEnd
            ? t('adminCoaches.actions.cancelRenewal.dialogDescription', { plan: planLabel, date: periodEnd })
            : t('adminCoaches.actions.cancelRenewal.dialogDescriptionNoDate', { plan: planLabel })
        }
        placeholder={t('adminCoaches.actions.cancelRenewal.placeholder')}
        cancelLabel={t('adminCoaches.actions.cancelRenewal.keep')}
        confirmLabel={t('adminCoaches.actions.cancelRenewal.button')}
        run={async (reason) => {
          try {
            const result = await cancel.mutateAsync(reason);
            showSuccess(
              result.alreadyCancelled
                ? 'adminCoaches.actions.cancelRenewal.alreadySuccess'
                : 'adminCoaches.actions.cancelRenewal.success',
            );
            return true;
          } catch (error) {
            reportFailure(error, 'adminCoaches.actions.cancelRenewal.error', onGone);
            return false;
          }
        }}
      />

      <ReasonDialog
        {...reasonDialogProps('endPlan', 'red', <CircleMinus />)}
        destructive
        title={t('adminCoaches.actions.endPlan.dialogTitle')}
        description={t('adminCoaches.actions.endPlan.dialogDescription', { name: firstName, plan: planLabel })}
        callout={
          overFreeLimit
            ? t('adminCoaches.actions.endPlan.callout', { name: firstName, count: used, max: freeMax })
            : undefined
        }
        placeholder={t('adminCoaches.actions.endPlan.placeholder')}
        cancelLabel={t('adminCoaches.actions.endPlan.cancel')}
        confirmLabel={t('adminCoaches.actions.endPlan.confirm')}
        run={(reason) =>
          attempt(
            () => endPlan.mutateAsync(reason),
            'adminCoaches.actions.endPlan.success',
            'adminCoaches.actions.endPlan.error',
          )
        }
      />

      <ReasonDialog
        {...reasonDialogProps('disable', 'red', <Ban />)}
        destructive
        title={t('adminCoaches.actions.disable.dialogTitle')}
        description={t('adminCoaches.actions.disable.dialogDescription')}
        placeholder={t('adminCoaches.actions.disable.placeholder')}
        cancelLabel={t('common.cancel')}
        confirmLabel={t('adminCoaches.actions.disable.confirm')}
        run={(reason) =>
          attempt(
            () => disable.mutateAsync(reason),
            'adminCoaches.actions.disable.success',
            'adminCoaches.actions.disable.error',
          )
        }
      />
    </section>
  );
}
