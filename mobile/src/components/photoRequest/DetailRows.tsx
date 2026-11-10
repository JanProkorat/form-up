import { StyleSheet, View } from 'react-native';
import { SymbolView } from 'expo-symbols';
import type { SFSymbol } from 'expo-symbols';

import { Text } from '@/components/Text';
import { useTheme } from '@/theme';
import type { Theme } from '@/theme';

export type DetailRow = { key: string; symbol: SFSymbol; label: string; value: string };

type DetailRowsProps = { rows: readonly DetailRow[] };

/** Label and value rows with an icon each, separated by hairlines; shared by the intro and closed cards. */
export function DetailRows({ rows }: DetailRowsProps) {
  const theme = useTheme();
  const styles = makeStyles(theme);

  return (
    <View>
      {rows.map((row, index) => (
        <View key={row.key} style={[styles.row, index > 0 ? styles.divided : null]}>
          <View style={styles.icon} accessibilityElementsHidden importantForAccessibility="no-hide-descendants">
            <SymbolView name={row.symbol} tintColor={theme.colors.ink2} size={theme.sizes.rowGlyph} />
          </View>
          <Text variant="body" color="muted" style={styles.label}>
            {row.label}
          </Text>
          <Text variant="bodyStrong" style={styles.value}>
            {row.value}
          </Text>
        </View>
      ))}
    </View>
  );
}

const makeStyles = (theme: Theme) =>
  StyleSheet.create({
    row: {
      flexDirection: 'row',
      alignItems: 'center',
      gap: theme.spacing.md,
      minHeight: theme.sizes.rowIcon + theme.spacing.xxxl,
    },
    divided: {
      borderTopWidth: theme.sizes.border,
      borderTopColor: theme.colors.lineStrong,
    },
    icon: {
      width: theme.sizes.rowIcon,
      height: theme.sizes.rowIcon,
      borderRadius: theme.radii.pill,
      alignItems: 'center',
      justifyContent: 'center',
      backgroundColor: theme.colors.subtle,
    },
    label: {
      flexShrink: 0,
    },
    value: {
      flex: 1,
      textAlign: 'right',
    },
  });
