import { useState, type ReactNode } from 'react';
import { Controller, useForm, useWatch } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { useTranslation } from 'react-i18next';
import { Ban, CalendarX, Gift, Tag } from 'lucide-react';
import { AdminCoachStatus, type GetAdminCoachResponse } from '@/api/admin-coaches';
import type { SubscriptionPlanDto } from '@/api/admin-subscription-plans';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { Input } from '@/components/ui/input';
import { Textarea } from '@/components/ui/textarea';
import { SelectChevron, SELECT_CLASS } from '@/components/automations/automationFormParts';
import { Field } from '@/components/package-catalog/packageFormParts';
import { planName } from '@/components/admin-plans/planForm';
import {
  REASON_MAX,
  endOfLocalDay,
  giveFreeSchema,
  reasonSchema,
  tomorrowInputValue,
  type GiveFreeFormValues,
  type ReasonFormValues,
} from '@/components/admin-coaches/coachActionForms';
import {
  useCancelCoachRenewal,
  useDisableCoachAccount,
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

const ERROR_NO_ACTIVE_ROLE = 'NO_ACTIVE_COACH_ROLE';
const ERROR_PROFESSIONAL_NOT_FOUND = 'PROFESSIONAL_NOT_FOUND';

const errorCodeOf = (error: unknown): string | null => getRfc7807ErrorCode(error) ?? getErrorCode(error);

interface ActionCardProps {
  icon: ReactNode;
  title: string;
  description: string;
  children?: ReactNode;
}

function ActionCard({ icon, title, description, children }: ActionCardProps) {
  return (
    <div className="flex flex-col gap-3 rounded-xl border border-line px-4 py-3.5">
      <div className="flex items-start gap-3">
        <span className="mt-0.5 text-muted-foreground" aria-hidden="true">
          {icon}
        </span>
        <div className="flex flex-col gap-0.5">
          <h4 className="text-copy font-semibold text-ink">{title}</h4>
          <p className="text-body text-muted-foreground">{description}</p>
        </div>
      </div>
      {children}
    </div>
  );
}

interface Props {
  coach: GetAdminCoachResponse;
  coachId: string;
  plans: SubscriptionPlanDto[];
  /** Called when the coach is gone (404), so the drawer can close. */
  onGone: () => void;
}

/** Reports a failed action; the shared apiErrors text for NO_ACTIVE_COACH_ROLE speaks to the coach, so the admin gets its own. */
function reportFailure(error: unknown, fallbackKey: string, onGone: () => void) {
  if (getErrorStatus(error) === 400 && errorCodeOf(error) === ERROR_NO_ACTIVE_ROLE) {
    showError('adminCoaches.errors.noActiveRole');
    return;
  }
  showApiError(error, fallbackKey);
  if (getErrorStatus(error) === 404 && errorCodeOf(error) === ERROR_PROFESSIONAL_NOT_FOUND) {
    onGone();
  }
}

/** The drawer's actions: give a plan free, apply a promotion (not yet), cancel renewal, disable account. */
export default function CoachActions({ coach, coachId, plans, onGone }: Props) {
  const { t } = useTranslation();
  const renewing =
    (coach.status === AdminCoachStatus.Paying ||
      coach.status === AdminCoachStatus.PaymentFailed ||
      coach.status === AdminCoachStatus.Cancelling) &&
    coach.currentPeriodEndsAt !== undefined;

  return (
    <section className="flex flex-col gap-3" aria-labelledby="coach-actions-heading">
      <h3 id="coach-actions-heading" className="text-copy font-semibold text-ink">
        {t('adminCoaches.drawer.actions')}
      </h3>
      <GiveFreeCard coachId={coachId} plans={plans} onGone={onGone} />
      <ActionCard
        icon={<Tag className="size-4" />}
        title={t('adminCoaches.actions.promotion.title')}
        description={t('adminCoaches.actions.promotion.description')}
      >
        <div>
          <Button type="button" variant="outline" size="lg" disabled title={t('shell.comingSoon')}>
            {t('adminCoaches.actions.promotion.button')}
          </Button>
        </div>
      </ActionCard>
      <CancelRenewalCard coachId={coachId} renewing={renewing} cancelling={coach.cancelAtPeriodEnd === true} onGone={onGone} />
      <DisableCard coachId={coachId} onGone={onGone} />
    </section>
  );
}

function GiveFreeCard({ coachId, plans, onGone }: { coachId: string; plans: SubscriptionPlanDto[]; onGone: () => void }) {
  const { t, i18n } = useTranslation();
  const give = useGiveCoachPlanFree(coachId);
  const options = plans.filter((plan) => plan.code && plan.code !== 'free' && plan.isActive);
  const {
    register,
    handleSubmit,
    control,
    reset,
    formState: { errors },
  } = useForm<GiveFreeFormValues>({
    resolver: zodResolver(giveFreeSchema),
    defaultValues: { planCode: '', noEnd: false, freeUntil: '', reason: '' },
  });
  const noEnd = useWatch({ control, name: 'noEnd' });

  const submit = handleSubmit(async (values) => {
    const end = values.noEnd ? null : endOfLocalDay(values.freeUntil);
    try {
      await give.mutateAsync({
        planCode: values.planCode,
        freeUntil: end ? end.toISOString() : undefined,
        reason: values.reason.trim(),
      });
      showSuccess('adminCoaches.actions.giveFree.success');
      reset();
    } catch (error) {
      reportFailure(error, 'adminCoaches.actions.giveFree.error', onGone);
    }
  });

  return (
    <ActionCard
      icon={<Gift className="size-4" />}
      title={t('adminCoaches.actions.giveFree.title')}
      description={t('adminCoaches.actions.giveFree.description')}
    >
      <form onSubmit={submit} noValidate className="flex flex-col gap-3">
        <Field label={t('adminCoaches.actions.giveFree.plan')} error={errors.planCode?.message}>
          {(fieldControl) => (
            <div className="relative">
              <select {...register('planCode')} className={SELECT_CLASS} {...fieldControl}>
                <option value="">{t('adminCoaches.actions.giveFree.planPlaceholder')}</option>
                {options.map((plan) => (
                  <option key={plan.code} value={plan.code}>
                    {planName(plan, i18n.language)}
                  </option>
                ))}
              </select>
              <SelectChevron />
            </div>
          )}
        </Field>
        <div className="grid grid-cols-1 items-start gap-3 sm:grid-cols-2">
          <Field label={t('adminCoaches.actions.giveFree.until')} error={errors.freeUntil?.message}>
            {(fieldControl) => (
              <Input
                type="date"
                min={tomorrowInputValue()}
                disabled={noEnd}
                className="h-10"
                {...register('freeUntil')}
                {...fieldControl}
              />
            )}
          </Field>
          <div className="flex items-center gap-2.5 sm:pt-8.5">
            <Controller
              control={control}
              name="noEnd"
              render={({ field }) => (
                <Checkbox
                  id={`give-free-no-end-${coachId}`}
                  checked={field.value}
                  onCheckedChange={(next) => field.onChange(next === true)}
                  className="size-4.5"
                />
              )}
            />
            <label htmlFor={`give-free-no-end-${coachId}`} className="text-copy text-ink">
              {t('adminCoaches.actions.giveFree.noEnd')}
            </label>
          </div>
        </div>
        <Field label={t('adminCoaches.actions.reason')} error={errors.reason?.message}>
          {(fieldControl) => (
            <Textarea rows={2} maxLength={REASON_MAX} {...register('reason')} {...fieldControl} />
          )}
        </Field>
        <div>
          <Button type="submit" size="lg" disabled={give.isPending}>
            {t('adminCoaches.actions.giveFree.button')}
          </Button>
        </div>
      </form>
    </ActionCard>
  );
}

interface ReasonCardProps {
  icon: ReactNode;
  title: string;
  description: string;
  buttonLabel: string;
  /** The action runs on a second click, after this question. */
  confirmText?: string;
  destructive?: boolean;
  /** Why the action is not available right now; the form is replaced by this note. */
  unavailableNote?: string;
  /** Runs the action; resolves true when it succeeded. */
  run: (reason: string) => Promise<boolean>;
}

/** An action that needs only a reason, with an optional confirm step. */
function ReasonCard({
  icon,
  title,
  description,
  buttonLabel,
  confirmText,
  destructive = false,
  unavailableNote,
  run,
}: ReasonCardProps) {
  const { t } = useTranslation();
  const [confirming, setConfirming] = useState(false);
  const [busy, setBusy] = useState(false);
  const {
    register,
    handleSubmit,
    reset,
    formState: { errors },
  } = useForm<ReasonFormValues>({ resolver: zodResolver(reasonSchema), defaultValues: { reason: '' } });

  const execute = async (values: ReasonFormValues) => {
    setBusy(true);
    const ok = await run(values.reason.trim());
    setBusy(false);
    setConfirming(false);
    if (ok) {
      reset();
    }
  };

  const submit = handleSubmit(async (values) => {
    if (confirmText && !confirming) {
      setConfirming(true);
      return;
    }
    await execute(values);
  });

  return (
    <ActionCard icon={icon} title={title} description={description}>
      {unavailableNote ? (
        <p className="text-body text-muted-foreground">{unavailableNote}</p>
      ) : (
        <form onSubmit={submit} noValidate className="flex flex-col gap-3">
          <Field label={t('adminCoaches.actions.reason')} error={errors.reason?.message}>
            {(fieldControl) => (
              <Textarea
                rows={2}
                maxLength={REASON_MAX}
                readOnly={confirming}
                {...register('reason')}
                {...fieldControl}
              />
            )}
          </Field>
          {confirming && confirmText && (
            <p role="alert" className="rounded-lg bg-error-soft px-3 py-2 text-body text-error">
              {confirmText}
            </p>
          )}
          <div className="flex items-center gap-2">
            <Button type="submit" size="lg" variant={destructive ? 'destructive' : 'outline'} disabled={busy}>
              {confirming ? t('adminCoaches.actions.confirm') : buttonLabel}
            </Button>
            {confirming && (
              <Button type="button" size="lg" variant="ghost" disabled={busy} onClick={() => setConfirming(false)}>
                {t('common.cancel')}
              </Button>
            )}
          </div>
        </form>
      )}
    </ActionCard>
  );
}

function CancelRenewalCard({
  coachId,
  renewing,
  cancelling,
  onGone,
}: {
  coachId: string;
  renewing: boolean;
  cancelling: boolean;
  onGone: () => void;
}) {
  const { t } = useTranslation();
  const cancel = useCancelCoachRenewal(coachId);
  const note = cancelling
    ? t('adminCoaches.actions.cancelRenewal.already')
    : renewing
      ? undefined
      : t('adminCoaches.actions.cancelRenewal.none');

  return (
    <ReasonCard
      icon={<CalendarX className="size-4" />}
      title={t('adminCoaches.actions.cancelRenewal.title')}
      description={t('adminCoaches.actions.cancelRenewal.description')}
      buttonLabel={t('adminCoaches.actions.cancelRenewal.button')}
      unavailableNote={note}
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
  );
}

function DisableCard({ coachId, onGone }: { coachId: string; onGone: () => void }) {
  const { t } = useTranslation();
  const disable = useDisableCoachAccount(coachId);

  return (
    <ReasonCard
      icon={<Ban className="size-4" />}
      title={t('adminCoaches.actions.disable.title')}
      description={t('adminCoaches.actions.disable.description')}
      buttonLabel={t('adminCoaches.actions.disable.button')}
      confirmText={t('adminCoaches.actions.disable.confirm')}
      destructive
      run={async (reason) => {
        try {
          await disable.mutateAsync(reason);
          showSuccess('adminCoaches.actions.disable.success');
          return true;
        } catch (error) {
          reportFailure(error, 'adminCoaches.actions.disable.error', onGone);
          return false;
        }
      }}
    />
  );
}
