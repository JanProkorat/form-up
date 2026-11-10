import { StyleSheet, View } from 'react-native';
import { useTranslation } from 'react-i18next';

import { Chip } from '@/components/Chip';
import { initials } from '@/components/invites/format';
import { Text } from '@/components/Text';
import { useTheme } from '@/theme';
import type { Theme } from '@/theme';

import type { RequestCoach } from './hooks';

type CoachHeaderProps = {
  coach: RequestCoach | null;
  /** Sized for a header row: a smaller avatar, centred, read as one element. */
  compact?: boolean;
};

/** The coach behind a request: initials avatar, name and role pill. */
export function CoachHeader({ coach, compact = false }: CoachHeaderProps) {
  const { t } = useTranslation();
  const theme = useTheme();
  const styles = makeStyles(theme);
  const name = coach?.name || t(`photoRequest.coachFallback.${coach?.role ?? 'other'}`);

  const label = coach?.role ? `${name}, ${t(`invites.roles.${coach.role}`)}` : name;

  return (
    <View
      accessible={compact}
      accessibilityLabel={compact ? label : undefined}
      style={[styles.row, compact ? styles.rowCompact : null]}
    >
      <View style={[styles.avatar, compact ? styles.avatarCompact : null]} accessibilityElementsHidden importantForAccessibility="no-hide-descendants">
        <Text variant="bodyStrong" style={{ color: theme.colors.onAvatar }}>
          {coach?.name ? initials(coach.name) : ''}
        </Text>
      </View>
      <View style={[styles.who, compact ? styles.whoCompact : null]}>
        <Text variant={compact ? 'bodyStrong' : 'subtitle'} numberOfLines={1}>
          {name}
        </Text>
        {coach?.role ? (
          <Chip
            size="sm"
            variant={coach.role === 'Trainer' ? 'training' : 'nutrition'}
            label={t(`invites.roles.${coach.role}`)}
          />
        ) : null}
      </View>
    </View>
  );
}

const makeStyles = (theme: Theme) =>
  StyleSheet.create({
    row: {
      flexDirection: 'row',
      alignItems: 'center',
      gap: theme.spacing.md,
    },
    rowCompact: {
      alignSelf: 'center',
      gap: theme.spacing.xs,
    },
    avatar: {
      width: theme.sizes.coachAvatar,
      height: theme.sizes.coachAvatar,
      borderRadius: theme.radii.pill,
      alignItems: 'center',
      justifyContent: 'center',
      backgroundColor: theme.colors.avatar,
    },
    avatarCompact: {
      width: theme.sizes.coachAvatarSmall,
      height: theme.sizes.coachAvatarSmall,
    },
    who: {
      flex: 1,
      minWidth: 0,
      gap: theme.spacing.xxs,
    },
    whoCompact: {
      flex: 0,
      flexShrink: 1,
    },
  });
