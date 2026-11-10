import type { ReactNode } from 'react';
import { RefreshControl, StyleSheet, View } from 'react-native';
import { useTranslation } from 'react-i18next';

import { PlanState } from '@/components/nutrition/plan/PlanState';
import { OfflineBanner } from '@/components/OfflineBanner';
import { Screen } from '@/components/Screen';
import { TabFade } from '@/components/TabFade';
import { Text } from '@/components/Text';
import { AddCoachRows } from '@/components/today/AddCoachRows';
import { AreaPaymentDue } from '@/components/today/AreaPaymentDue';
import { AreaBlocks, hasAreaBlock } from '@/components/today/AreaBlocks';
import { CoachProgressCard } from '@/components/today/CoachProgressCard';
import {
  COACH_ROLES,
  coveredCollaborations,
  offeredInvites,
} from '@/components/today/collaborationRoles';
import type { CoachRole } from '@/components/today/collaborationRoles';
import { FindCoachSection } from '@/components/today/FindCoachSection';
import { GetReadyList } from '@/components/today/GetReadyList';
import { HowItWorks } from '@/components/today/HowItWorks';
import { InvitesSection } from '@/components/today/InvitesSection';
import { PaymentDueCard } from '@/components/today/PaymentDueCard';
import { QuestionnaireBanners } from '@/components/today/QuestionnaireBanners';
import { RoleSlots } from '@/components/today/RoleSlots';
import { StartOnYourOwn } from '@/components/today/StartOnYourOwn';
import { StatTiles } from '@/components/today/StatTiles';
import { hasRunningPlan, selectAreaBlocks } from '@/components/today/todayBlocks';
import { TodayHeader } from '@/components/today/TodayHeader';
import { isActivePlan } from '@/api/plans';
import { useClientPlans } from '@/hooks/useClientPlans';
import { useCollaborations } from '@/hooks/useCollaborations';
import { useInvites } from '@/hooks/useInvites';
import { useLocalDayChange } from '@/hooks/useLocalDayChange';
import { useProfile } from '@/hooks/useProfile';
import { usePullToRefresh } from '@/hooks/usePullToRefresh';
import { isOfflineEmpty, showsError } from '@/lib/queryState';
import { useTheme } from '@/theme';
import type { Theme } from '@/theme';

const unique =<T,>(values: readonly T[]): T[] => Array.from(new Set(values));

export default function TodayScreen() {
  return (
    <TabFade>
      <TodayContent />
    </TabFade>
  );
}

function TodayContent() {
  const { t } = useTranslation();
  const theme = useTheme();
  const styles = makeStyles(theme);
  const { data: profile } = useProfile();
  const collaborations = useCollaborations();
  const invites = useInvites();
  const plans = useClientPlans();
  const today = useLocalDayChange();
  const { refreshing, onRefresh } = usePullToRefresh();
  const barClearance = theme.sizes.tabBar + theme.sizes.tabBarBottom + theme.spacing.xxl;

  if (!profile) {
    return <Screen>{null}</Screen>;
  }

  const name = profile.firstName?.trim();
  const greeting = name ? t('today.greeting', { name }) : t('today.greetingAnonymous');
  const screen = (header: ReactNode, content: ReactNode, tight = false) => (
    <Screen
      scroll
      collapseTabBar
      header={
        <View style={styles.header}>
          {header}
          <OfflineBanner />
        </View>
      }
      topGap={theme.spacing.safeAreaGap}
      bottomInset={barClearance}
      contentStyle={tight ? styles.contentTight : styles.content}
      refreshControl={
        <RefreshControl
          refreshing={refreshing}
          onRefresh={onRefresh}
          tintColor={theme.colors.muted}
          colors={[theme.colors.muted]}
        />
      }
    >
      {content}
    </Screen>
  );

  // A failed collaborations lookup falls back to what the profile says, as before.
  // Nothing saved and no connection: say so instead of showing the "find a coach" fallback.
  if (isOfflineEmpty(collaborations)) {
    return <PlanState kind="offline" />;
  }

  const covering = showsError(collaborations) ? null : coveredCollaborations(collaborations.data ?? []);

  if (covering !== null) {
    const waitingForData =
      collaborations.isPending || invites.isPending || (covering.length > 0 && plans.isPending);
    if (waitingForData) {
      return screen(<TodayHeader title={greeting} />, null);
    }

    const coveredRoles = unique(covering.flatMap((entry) => entry.roles));
    const missing: CoachRole[] = COACH_ROLES.filter((role) => !coveredRoles.includes(role));
    const offers = offeredInvites(invites.data ?? [], missing);

    if (covering.length === 0) {
      return screen(
        <TodayHeader title={greeting} />,
        <>
          {offers.length > 0 ? (
            <RoleSlots missing={missing} offers={offers} mode="find" showHeading />
          ) : (
            <FindCoachSection roles={missing} />
          )}
          {/* Offered invites are already listed above, so only the open proposals are added. */}
          <InvitesSection proposalsOnly />
          <HowItWorks />
          <StartOnYourOwn />
        </>,
      );
    }

    if (covering.length > 0 && isOfflineEmpty(plans)) {
      return <PlanState kind="offline" />;
    }

    if (!showsError(plans)) {
      const areaBlocks = selectAreaBlocks(plans.data ?? [], covering, today);
      // Once any area has an Active plan, each area gets its own block instead of the progress card.
      if (areaBlocks !== null && areaBlocks.some((block) => hasAreaBlock(block, today))) {
        return screen(
          <TodayHeader title={t('today.dateTitle')} />,
          <>
            {hasRunningPlan(areaBlocks) ? (
              <>
                <StatTiles />
                <QuestionnaireBanners />
              </>
            ) : null}
            <AreaPaymentDue blocks={areaBlocks} covering={covering} />
            <AreaBlocks blocks={areaBlocks} covering={covering} today={today} />
            <GetReadyList blocks={areaBlocks} covering={covering} />
            <AddCoachRows blocks={areaBlocks} />
            {/* The stat tiles already offer logging weight and adding photos. */}
            {hasRunningPlan(areaBlocks) ? null : <StartOnYourOwn showHeading={false} />}
            <InvitesSection />
          </>,
          true,
        );
      }
    }

    if (!showsError(plans) && !(plans.data ?? []).some(isActivePlan)) {
      // No plan is Active here, so every covered role is still unpublished.
      const progress = covering
        .map((entry) => ({ ...entry, unpublished: entry.roles }))
        .filter((entry) => entry.unpublished.length > 0);

      if (progress.length > 0) {
        return screen(
          <TodayHeader title={t('today.dateTitle')} />,
          <>
            {progress.map((entry) => (
              <CoachProgressCard
                key={entry.collaboration.publicId ?? entry.collaboration.professionalPublicId}
                collaboration={entry.collaboration}
                covered={entry.roles}
                unpublished={entry.unpublished}
                onlyCollaboration={covering.length === 1}
              />
            ))}
            {missing.length > 0 ? (
              <RoleSlots missing={missing} offers={offers} mode="add" />
            ) : null}
            <InvitesSection proposalsOnly />
            <StartOnYourOwn showHeading={false} />
          </>,
          true,
        );
      }
    }
  }

  const linked = profile.linkedRoles ?? [];
  const missingRoles = COACH_ROLES.filter((role) => !linked.includes(role));
  const noCoach = !profile.hasActiveLink && linked.length === 0;
  const title = noCoach
    ? greeting
    : name
      ? t('today.linkedGreeting', { name })
      : t('today.linkedGreetingAnonymous');

  return screen(
    <TodayHeader title={title} />,
    <>
      {missingRoles.length > 0 ? (
        <FindCoachSection roles={missingRoles} />
      ) : (
        <Text variant="bodyLarge" color="muted">
          {t('today.linkedBody')}
        </Text>
      )}
      <PaymentDueCard />
      <InvitesSection />
    </>,
  );
}

const makeStyles = (theme: Theme) =>
  StyleSheet.create({
    header: {
      gap: theme.spacing.lg,
    },
    content: {
      gap: theme.spacing.huge,
    },
    contentTight: {
      gap: theme.spacing.xl,
    },
  });
