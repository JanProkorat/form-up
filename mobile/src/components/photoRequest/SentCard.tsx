import { Pressable, StyleSheet, View } from 'react-native';
import { SymbolView } from 'expo-symbols';

import { Card } from '@/components/Card';
import { Text } from '@/components/Text';
import { useTheme } from '@/theme';
import type { Theme } from '@/theme';

type SentCardProps = {
  title: string;
  meta: string;
  actionLabel: string;
  onPress: () => void;
};

/** The "Progress photos" card of a sent confirmation: what arrived, and a link to see it. */
export function SentCard({ title, meta, actionLabel, onPress }: SentCardProps) {
  const theme = useTheme();
  const styles = makeStyles(theme);

  return (
    <Pressable accessibilityRole="link" accessibilityLabel={`${title}, ${meta}. ${actionLabel}`} onPress={onPress}>
      <Card style={styles.card}>
        <View style={styles.icon} accessibilityElementsHidden importantForAccessibility="no-hide-descendants">
          <SymbolView name="camera" tintColor={theme.colors.accent} size={theme.sizes.rowGlyph} />
        </View>
        <View style={styles.copy}>
          <Text variant="bodyStrong">{title}</Text>
          <Text variant="caption" color="muted">
            {meta}
          </Text>
        </View>
        <View style={styles.action} accessibilityElementsHidden importantForAccessibility="no-hide-descendants">
          <Text variant="link" color="ink2">
            {actionLabel}
          </Text>
          <SymbolView name="chevron.right" weight="semibold" tintColor={theme.colors.ink2} size={theme.sizes.coachChevron} />
        </View>
      </Card>
    </Pressable>
  );
}

const makeStyles = (theme: Theme) =>
  StyleSheet.create({
    card: {
      flexDirection: 'row',
      alignItems: 'center',
      gap: theme.spacing.md,
    },
    icon: {
      width: theme.sizes.coachIcon,
      height: theme.sizes.coachIcon,
      borderRadius: theme.radii.pill,
      alignItems: 'center',
      justifyContent: 'center',
      backgroundColor: theme.colors.accentSoft,
    },
    copy: {
      flex: 1,
      minWidth: 0,
      gap: theme.spacing.hair,
    },
    action: {
      flexDirection: 'row',
      alignItems: 'center',
      gap: theme.spacing.xxs,
    },
  });
