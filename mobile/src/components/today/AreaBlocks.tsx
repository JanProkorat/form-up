import { Fragment } from 'react';
import type { ReactNode } from 'react';
import { StyleSheet, View } from 'react-native';
import { useRouter } from 'expo-router';
import type { TFunction } from 'i18next';
import { useTranslation } from 'react-i18next';

import type { ClientOwnPlanItem } from '@/api/generated';
import { firstName } from '@/components/invites/format';
import { isoDay } from '@/components/nutrition/plan/model';
import { calendarParts } from '@/components/nutrition/upcoming/format';
import { useTheme } from '@/theme';
import type { Theme } from '@/theme';

import { CoachBuildingRow } from './CoachBuildingRow';
import type { CoveredCollaboration } from './collaborationRoles';
import { NextPlanStrip } from './NextPlanStrip';
import { RunningNutritionCard } from './RunningNutritionCard';
import { PlanRunsUntilCard } from './PlanRunsUntilCard';
import { planRunsUntil } from './todayBlocks';
import type { AreaBlock } from './todayBlocks';
import { UpcomingPlanRow } from './UpcomingPlanRow';

type AreaBlocksProps = {
  blocks: readonly AreaBlock[];
  /** Collaborations that cover at least one role; the coach's first name for the card comes from here. */
  covering: readonly CoveredCollaboration[];
  /** Local today as `yyyy-MM-dd`. */
  today: string;
};

/**
 * True when the area has something to render. A running training plan has no block of its own here yet,
 * but it still shows the runs-until card while its end date has not passed.
 */
export const hasAreaBlock = (block: AreaBlock, today: string): boolean =>
  block.state.kind !== 'running' || block.role === 'Nutritionist' || planRunsUntil(block, today) !== undefined;

/** "<plan name> · starts <weekday> <day month>" in the app language. */
function nextPlanSubtitle(t: TFunction, language: string, plan: ClientOwnPlanItem): string {
  const start = calendarParts(language, plan.startDate);
  // calendarParts is UTC-parsed (0 = Sunday); the copy keys are ISO weekdays (1 = Monday, 7 = Sunday).
  const isoWeekday = start.weekday === 0 ? 7 : start.weekday;
  const date = new Intl.DateTimeFormat(language, { day: 'numeric', month: 'short', timeZone: 'UTC' }).format(
    new Date(`${isoDay(plan.startDate)}T00:00:00Z`),
  );
  return t('today.nextPlan.subtitle', {
    plan: plan.planName ?? '',
    when: `${t(`today.areaBlocks.weekday.${isoWeekday}`)} ${date}`,
  });
}

/** One block per area, chosen by the area's state; areas without a coach render in AddCoachRows. */
export function AreaBlocks({ blocks, covering, today }: AreaBlocksProps) {
  const { t, i18n } = useTranslation();
  const router = useRouter();
  const theme = useTheme();
  const styles = makeStyles(theme);

  return (
    <View style={styles.list}>
      {blocks.map((block) => {
        const { role, state } = block;
        const runsUntil = planRunsUntil(block, today);
        const coach = covering.find((entry) => entry.roles.includes(role));
        const coachName = firstName(coach?.collaboration.professionalName);
        const card =
          runsUntil !== undefined ? (
            <PlanRunsUntilCard role={role} runsUntil={runsUntil} coachName={coachName || undefined} />
          ) : null;
        const body = ((): ReactNode => {
          switch (state.kind) {
            case 'startsLater':
              return <UpcomingPlanRow role={role} plan={state.plan} today={today} />;
            case 'building':
              return <CoachBuildingRow role={role} collaboration={state.collaboration} />;
            case 'running':
              return role === 'Nutritionist' ? (
                <RunningNutritionCard
                  plan={state.plan}
                  today={today}
                  footer={
                    state.next ? (
                      <NextPlanStrip
                        title={t('today.nextPlan.title')}
                        subtitle={nextPlanSubtitle(t, i18n.language, state.next)}
                        onPress={() =>
                          router.push({ pathname: '/nutrition/plan', params: { planId: state.next?.planId } })
                        }
                      />
                    ) : undefined
                  }
                />
              ) : null;
            default:
              return null;
          }
        })();

        return (
          <Fragment key={role}>
            {card}
            {body}
          </Fragment>
        );
      })}
    </View>
  );
}

const makeStyles = (theme: Theme) =>
  StyleSheet.create({
    list: {
      gap: theme.spacing.sm,
    },
  });
