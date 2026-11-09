import { Pressable, StyleSheet, View } from 'react-native';
import { useRouter } from 'expo-router';
import { SymbolView } from 'expo-symbols';
import { useTranslation } from 'react-i18next';

import type { PendingDiaryRequest } from '@/api/questionnaires';
import { Text } from '@/components/Text';
import { daysLabel, formatRequestDay, pendingCoachLabel } from '@/components/photoRequest/format';
import { introRoute, requestDays, requestEndsAt, requestMode } from '@/components/photoRequest/model';
import { useTheme } from '@/theme';
import type { Theme } from '@/theme';

type PhotoRequestBannerProps = { item: PendingDiaryRequest };

/** A pending photo request on Today: who asks, how often, and an Open button that leads to the intro. */
export function PhotoRequestBanner({ item }: PhotoRequestBannerProps) {
  const { t, i18n } = useTranslation();
  const router = useRouter();
  const theme = useTheme();
  const styles = makeStyles(theme);
  const id = item.requestPublicId;
  if (!id) return null;

  const name = pendingCoachLabel(t, item);
  const endsAt = requestEndsAt(item);
  const hint =
    requestMode(item) === 'everyDay'
      ? t('photoRequest.banner.everyDayHint', {
          days: daysLabel(t, requestDays(item)),
          date: formatRequestDay(i18n.language, endsAt ?? undefined),
        })
      : t('photoRequest.banner.onceHint');

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
        onPress={() => router.push(introRoute(id))}
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
