import { useTranslation } from 'react-i18next';
import { Lock } from 'lucide-react';
import { Tabs, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { useSubscription } from '@/hooks/useSubscriptionQueries';

export type ClientDetailTab = 'overview' | 'nutrition' | 'photos' | 'payment' | 'automations' | 'forms';

const TAB_KEYS = ['overview', 'nutrition', 'workouts', 'storage', 'payment', 'automations', 'forms'] as const;
/** The `storage` label key now names the Photos tab; its route value is `photos`. */
const LIVE_TABS: readonly string[] = ['overview', 'nutrition', 'storage', 'payment', 'automations', 'forms'];

function tabValue(key: (typeof TAB_KEYS)[number]): string {
  return key === 'storage' ? 'photos' : key;
}

interface Props {
  /** The tab the current URL selects. */
  activeTab: ClientDetailTab;
  onTabChange: (tab: ClientDetailTab) => void;
  /** The coach's link grants the nutrition domain; false hides the Nutrition tab. */
  canViewNutritionPlans: boolean;
  /** The coach's link grants the training domain; false hides the Workouts tab. */
  canViewTrainingPlans: boolean;
  /** Any form of the current collaboration is submitted and not yet opened; shows a dot on the Forms tab. */
  hasNewForms?: boolean;
}

/**
 * The tab row on the client-detail page — ClientsPage's underline Tabs
 * treatment, minus the row hairline (the header draws it). The active tab comes from the
 * URL (`/clients/:clientId`, `.../nutrition`, `.../photos`, `.../payment`, `.../automations` or `.../forms`); tabs that are
 * not built yet render disabled with the shell's coming-soon treatment.
 * Nutrition and Workouts show only for a link that grants the matching domain.
 */
export default function ClientDetailTabs({
  activeTab,
  onTabChange,
  canViewNutritionPlans,
  canViewTrainingPlans,
  hasNewForms = false,
}: Props) {
  const { t } = useTranslation();
  // Same rule as the sidebar: forms off the coach's plan show a lock (stays unlocked while loading).
  const formsLocked = !useSubscription().isEntitled('canSendQuestionnaires');
  const tabKeys = TAB_KEYS.filter((key) => {
    if (key === 'nutrition') {
      return canViewNutritionPlans;
    }
    if (key === 'workouts') {
      return canViewTrainingPlans;
    }
    return true;
  });

  return (
    <Tabs value={activeTab} onValueChange={(value) => {
        if (
          value === 'overview' ||
          value === 'nutrition' ||
          value === 'photos' ||
          value === 'payment' ||
          value === 'automations' ||
          value === 'forms'
        ) {
          onTabChange(value);
        }
      }}>
      <div className="overflow-x-auto">
        {/* The hairline under the row belongs to the header, which shares it with the action buttons. */}
        <TabsList variant="underline" className="gap-6.5 border-b-0">
          {tabKeys.map((key) => {
            const isLive = LIVE_TABS.includes(key);
            return (
              <TabsTrigger
                key={key}
                value={tabValue(key)}
                variant="underline"
                className="text-body"
                disabled={!isLive}
                title={isLive ? undefined : t('shell.comingSoon')}
              >
                {t(`clientDetail.overview.tabs.${key}`)}
                {key === 'forms' && formsLocked && (
                  <>
                    <Lock className="ml-1.5 size-3.5 text-muted-foreground" aria-hidden="true" />
                    <span className="sr-only">{t('sidebar.lockedInPlan')}</span>
                  </>
                )}
                {key === 'forms' && hasNewForms && !formsLocked && (
                  <>
                    <span data-testid="forms-tab-dot" className="ml-1.5 size-1.75 rounded-full bg-error" aria-hidden="true" />
                    <span className="sr-only">{t('clientForms.newAnswer')}</span>
                  </>
                )}
              </TabsTrigger>
            );
          })}
        </TabsList>
      </div>
    </Tabs>
  );
}
