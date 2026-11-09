import { Pressable, StyleSheet, View } from 'react-native';
import { useRouter } from 'expo-router';
import { SymbolView } from 'expo-symbols';
import { useTranslation } from 'react-i18next';

import { Text } from '@/components/Text';
import { coachLabel, daysLabel, formatRequestDay, pendingCoachLabel } from '@/components/photoRequest/format';
import { useRequestCoach } from '@/components/photoRequest/hooks';
import { flowRoute, introRoute, requestDays, requestEndsAt, requestMode } from '@/components/photoRequest/model';
import type { BannerRequest } from '@/components/photoRequest/model';
import { useTheme } from '@/theme';
import type { Theme } from '@/theme';

type PhotoRequestBannerProps = { entry: BannerRequest };

/** An open photo request on Today: who asks, where it stands, and an Open button that leads to the right screen. */
export function PhotoRequestBanner({ entry }: PhotoRequestBannerProps) {
  const { t, i18n } = useTranslation();
  const router = useRouter();
  const theme = useTheme();
  const styles = makeStyles(theme);
  const coach = useRequestCoach(entry.kind === 'pending' ? undefined : entry.request);

  const id = entry.kind === 'pending' ? entry.item.requestPublicId : entry.request.id;
  if (!id) return null;

  const name = entry.kind === 'pending' ? pendingCoachLabel(t, entry.item) : coachLabel(t, coach);
  let hint: string;
  let route = introRoute(id);
  if (entry.kind === 'pending') {
    hint =
      requestMode(entry.item) === 'everyDay'
        ? t('photoRequest.banner.everyDayHint', {
            days: daysLabel(t, requestDays(entry.item)),
            date: formatRequestDay(i18n.language, requestEndsAt(entry.item) ?? undefined),
          })
        : t('photoRequest.banner.onceHint');
  } else if (entry.kind === 'running') {
    hint = t(entry.sentToday ? 'photoRequest.step.runningSentHint' : 'photoRequest.step.runningHint', {
      day: entry.day,
      total: entry.total,
    });
    route = flowRoute(id, 'everyDay');
  } else {
    hint = t('photoRequest.banner.onceHint');
    route = flowRoute(id, 'once');
  }

  return (
    <View style={styles.banner}>
      <View style={styles.icon} accessibilityElementsHidden importantForAccessibility="no-hide-descendants">
        <SymbolView name="camera" tintColor={theme.colors.accent} size={theme.sizes.coachGlyph} />
      </View>
      <View style={styles.copy}>
        <Text variant="link" numberOfLines={2}>
          {t('photoRequest.title', { name })}
        </Text>
        <Text variant="caption" color="ink2">
          {hint}
        </Text>
      </View>
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={t('photoRequest.banner.openLabel', { name })}
        onPress={() => router.push(route)}
        style={styles.action}
      >
        <Text variant="labelStrong" color="onPrimary">
          {t('photoRequest.banner.open')}
        </Text>
      </Pressable>
    </View>
  );
}

const makeStyles = (theme: Theme) =>
  StyleSheet.create({
    banner: {
      flexDirection: 'row',
      alignItems: 'center',
      gap: theme.spacing.md,
      paddingVertical: theme.spacing.md,
      paddingLeft: theme.spacing.lg,
      paddingRight: theme.spacing.md,
      borderRadius: theme.radii.xl,
      backgroundColor: theme.colors.accentSoft,
    },
    icon: {
      width: theme.sizes.coachIcon,
      height: theme.sizes.coachIcon,
      borderRadius: theme.radii.pill,
      alignItems: 'center',
      justifyContent: 'center',
      backgroundColor: theme.colors.surface,
    },
    copy: {
      flex: 1,
      minWidth: 0,
      gap: theme.spacing.xxs / 2,
    },
    action: {
      height: theme.sizes.stepButton,
      paddingHorizontal: theme.spacing.xl,
      borderRadius: theme.radii.pill,
      alignItems: 'center',
      justifyContent: 'center',
      backgroundColor: theme.colors.primary,
    },
  });
