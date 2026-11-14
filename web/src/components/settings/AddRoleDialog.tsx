import { useState } from 'react';
import { Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { ChevronRight, Dumbbell, Info, Leaf } from 'lucide-react';
import { BillingInterval } from '@/api/generated';
import type { CoachRoleKey } from '@/components/settings/RemoveRoleDialog';
import { billingErrorCode, billingErrorMessage } from '@/components/subscription/billingErrors';
import { formatMoney } from '@/components/subscription/money';
import { coachPlanName } from '@/components/subscription/planNames';
import { PLAN_PICKER_PATH } from '@/components/subscription/planLinks';
import { Button } from '@/components/ui/button';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';
import { Skeleton } from '@/components/ui/skeleton';
import {
  useCoachPlans,
  useConfirmSecondRole,
  useSecondRolePreview,
  useSubscription,
} from '@/hooks/useSubscriptionQueries';
import { getErrorStatus } from '@/lib/api-errors';

const DAY_MS = 24 * 60 * 60 * 1000;

interface Props {
  /** The role being added; the dialog is open while this is set. */
  role: CoachRoleKey | null;
  onClose: () => void;
  /** The add-on is paid (or was already): the caller adds the role through POST /users/me/roles. */
  onPaid: () => void;
  /** The role is being added after payment. */
  adding: boolean;
}

/**
 * Add the second role on a plan that charges for it: shows the new bill and today's prorated charge, takes the
 * payment, and only then lets the caller add the role. A declined card leaves the dialog open and the role unadded.
 */
export default function AddRoleDialog({ role, onClose, onPaid, adding }: Props) {
  const { t, i18n } = useTranslation();
  const open = role !== null;
  const subscription = useSubscription();
  const plansQuery = useCoachPlans(open && subscription.isCoach);
  const previewQuery = useSecondRolePreview(open && subscription.isCoach);
  const confirm = useConfirmSecondRole();
  // One id per confirm attempt: a new one each time the dialog opens and after every decline, because
  // the server replays the stored answer for an id it has already seen.
  const [attemptId, setAttemptId] = useState(() => crypto.randomUUID());
  const [openedFor, setOpenedFor] = useState<CoachRoleKey | null>(role);
  const [confirmError, setConfirmError] = useState<string | null>(null);

  // Adjusted during render (own state only): opening the dialog starts a fresh attempt and clears the last error.
  if (role !== openedFor) {
    setOpenedFor(role);
    if (role !== null) {
      setAttemptId(crypto.randomUUID());
      setConfirmError(null);
    }
  }

  const preview = previewQuery.data;
  const language = i18n.language;
  const alreadyPaid = previewQuery.isError && billingErrorCode(previewQuery.error) === 'BILLING_ADD_ON_ALREADY_PAID';
  const currency = preview?.currency ?? 'CZK';
  const money = (amount: number | undefined) => formatMoney(amount ?? 0, currency, language);
  const per = t(
    `subscription.planChoice.card.per.${preview?.billingInterval === BillingInterval.Annual ? 'year' : 'month'}`,
  );
  const RoleIcon = role === 'nutritionist' ? Leaf : Dumbbell;
  const roleLabel = role ? t(`roles.${role}`) : '';

  // Counted from the moment the preview was read, so the number matches the prorated amount beside it.
  const days =
    preview?.periodEndsAt !== undefined
      ? Math.max(1, Math.ceil((new Date(preview.periodEndsAt).getTime() - previewQuery.dataUpdatedAt) / DAY_MS))
      : null;
  const tax = preview?.todayTaxMinorUnits ?? 0;
  const studio = (plansQuery.data?.plans ?? [])
    .filter((plan) => plan.bothRolesIncluded && (plan.priceMinorUnits ?? 0) > 0)
    .sort((a, b) => (a.priceMinorUnits ?? 0) - (b.priceMinorUnits ?? 0))[0];

  const pay = () => {
    setConfirmError(null);
    confirm.mutate(attemptId, {
      onSuccess: onPaid,
      onError: (error) => {
        setConfirmError(billingErrorMessage(error, 'subscription.billing.errors.addRole'));
        // Any 402 is final for this attempt id; the next try needs its own.
        if (getErrorStatus(error) === 402) setAttemptId(crypto.randomUUID());
      },
    });
  };

  const busy = confirm.isPending || adding;
  const loadFailed = previewQuery.isError && !alreadyPaid;

  return (
    <Dialog open={open} onOpenChange={(next) => !next && !busy && onClose()}>
      <DialogContent showCloseButton={false}>
        <DialogHeader>
          <span className="flex size-11 items-center justify-center rounded-md bg-sunken text-ink-2">
            <RoleIcon className="size-5" aria-hidden="true" />
          </span>
          <DialogTitle>{t('subscription.billing.addRole.title', { role: roleLabel })}</DialogTitle>
          <DialogDescription>
            {role ? t(`subscription.billing.addRole.body.${role}`) : null}
          </DialogDescription>
        </DialogHeader>

        {previewQuery.isPending && open ? (
          <Skeleton className="h-40 w-full rounded-xl" />
        ) : loadFailed ? (
          <p role="alert" className="text-body text-error">
            {billingErrorMessage(previewQuery.error, 'subscription.billing.errors.preview')}
          </p>
        ) : alreadyPaid ? (
          <p className="flex gap-2.5 rounded-xl bg-sunken px-3.5 py-3 text-body text-ink-2">
            <Info className="mt-px size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
            {t('subscription.billing.addRole.alreadyPaid')}
          </p>
        ) : preview ? (
          <>
            <div className="flex flex-col gap-2.5 rounded-xl bg-sunken p-4">
              <div className="flex items-baseline gap-3 text-copy text-ink-2">
                <span>{t('subscription.billing.addRole.planLine', { plan: subscription.planName })}</span>
                <span className="ml-auto font-semibold whitespace-nowrap">{money(preview.planAmountMinorUnits)}</span>
              </div>
              <div className="flex items-baseline gap-3 text-copy text-ink-2">
                <span>{t('subscription.billing.addRole.addOnLine', { role: roleLabel })}</span>
                <span className="ml-auto font-semibold whitespace-nowrap">+{money(preview.addOnAmountMinorUnits)}</span>
              </div>
              <div className="flex items-baseline gap-3 border-t border-border pt-2.5 text-subhead font-semibold text-ink">
                <span>{t('subscription.billing.addRole.total')}</span>
                <span className="ml-auto whitespace-nowrap">
                  {money(preview.newTotalMinorUnits)} {per}
                </span>
              </div>
            </div>

            <div className="flex gap-2.5 rounded-xl bg-sunken px-3.5 py-3 text-ink-2">
              <Info className="mt-px size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
              <p className="text-body leading-normal">
                {days !== null && preview.periodEndsAt
                  ? t('subscription.billing.addRole.today', {
                      amount: money(preview.todayAmountMinorUnits),
                      count: days,
                      date: new Intl.DateTimeFormat(language, { dateStyle: 'medium' }).format(new Date(preview.periodEndsAt)),
                    })
                  : t('subscription.billing.addRole.todayNoDate', { amount: money(preview.todayAmountMinorUnits) })}
                {tax > 0 && ` ${t('subscription.billing.addRole.taxIncluded', { tax: money(tax) })}`}
              </p>
            </div>

            {studio && (
              <div className="flex flex-wrap items-center gap-2 text-body text-muted-foreground">
                <span>
                  {t('subscription.billing.addRole.studio', {
                    plan: coachPlanName(studio, language),
                    price: formatMoney(studio.priceMinorUnits ?? 0, studio.currency ?? currency, language),
                  })}
                </span>
                <Link
                  to={PLAN_PICKER_PATH}
                  className="ml-auto inline-flex items-center gap-1 font-semibold text-ink underline underline-offset-2"
                >
                  {t('subscription.billing.addRole.compare')}
                  <ChevronRight className="size-3.5" aria-hidden="true" />
                </Link>
              </div>
            )}
          </>
        ) : null}

        {confirmError && (
          <p role="alert" className="text-body text-error">
            {confirmError}
          </p>
        )}

        <DialogFooter>
          <Button type="button" variant="outline" size="lg" disabled={busy} onClick={onClose}>
            {t('common.cancel')}
          </Button>
          {alreadyPaid ? (
            <Button type="button" size="lg" disabled={busy} onClick={onPaid}>
              {t('settings.roles.add')}
            </Button>
          ) : (
            <Button type="button" size="lg" disabled={busy || !preview || loadFailed} onClick={pay}>
              {preview
                ? t('subscription.billing.addRole.confirm', { amount: money(preview.todayAmountMinorUnits) })
                : t('settings.roles.add')}
            </Button>
          )}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
