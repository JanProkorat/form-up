import { Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { Info } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { KEEP_CLIENTS_PATH, PLAN_CHOICE_PATH, PLAN_PICKER_PATH } from '@/components/subscription/planLinks';

interface Props {
  /** Set on a restricted client's detail: the banner then names that client instead of the plan's limit. */
  clientName?: string;
  planName: string;
  clientLimit: number | null;
  overCapClientCount: number;
  freeSlotCount: number;
  /** Set on the clients list: adds a button that filters the list to the clients outside the plan. */
  onShowClients?: () => void;
  /** The coach has not confirmed which clients the plan keeps: the banner explains the provisional choice. */
  choicePending?: boolean;
}

/** Notice for a coach with clients outside the plan: what happens to them, and the way to a higher plan. */
export default function OutsidePlanBanner({ clientName, planName, clientLimit, overCapClientCount, freeSlotCount, onShowClients, choicePending = false }: Props) {
  const { t } = useTranslation();

  const title = choicePending
    ? t('subscription.outsidePlan.pendingTitle', { plan: planName, count: clientLimit ?? 0 })
    : clientName
    ? t('subscription.outsidePlan.detailTitle', { name: clientName, plan: planName })
    : `${t('subscription.outsidePlan.titleLimit', { plan: planName, count: clientLimit ?? 0 })} ${t(
        'subscription.outsidePlan.titleOutside',
        { count: overCapClientCount },
      )}`;

  return (
    <div role="status" className="flex flex-wrap items-center gap-3.5 rounded-xl bg-training-soft px-4 py-3.5">
      <Info className="size-4.5 shrink-0 text-training-ink" aria-hidden="true" />
      <div className="flex min-w-0 flex-col gap-0.5">
        <span className="text-copy font-semibold text-ink">{title}</span>
        <span className="text-body text-ink-2">{t(choicePending ? 'subscription.outsidePlan.pendingBody' : 'subscription.outsidePlan.body')}</span>
      </div>
      <div className="ml-auto flex flex-wrap items-center gap-2">
        {choicePending && (
          <Button asChild variant="outline" size="lg" className="px-3.5 font-semibold">
            <Link to={PLAN_CHOICE_PATH}>{t('subscription.outsidePlan.chooseClients')}</Link>
          </Button>
        )}
        {!choicePending && onShowClients && (
          <Button type="button" variant="outline" size="lg" className="px-3.5 font-semibold" onClick={onShowClients}>
            {t('subscription.outsidePlan.showClients')}
          </Button>
        )}
        {!choicePending && freeSlotCount > 0 && (
          <Button asChild variant="outline" size="lg" className="px-3.5 font-semibold">
            <Link to={KEEP_CLIENTS_PATH}>{t('subscription.outsidePlan.pickClient')}</Link>
          </Button>
        )}
        <Button asChild size="lg" className="px-3.5 font-semibold">
          <Link to={PLAN_PICKER_PATH}>{t('subscription.outsidePlan.upgrade')}</Link>
        </Button>
      </div>
    </div>
  );
}
