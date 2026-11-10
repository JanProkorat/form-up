import { Pressable, StyleSheet, View } from 'react-native';
import { BlurView } from 'expo-blur';
import { SymbolView } from 'expo-symbols';
import { useTranslation } from 'react-i18next';

import { BackButton } from '@/components/BackButton';
import { Text } from '@/components/Text';
import { useTheme } from '@/theme';
import type { Theme } from '@/theme';

type FlowHeaderProps = {
  /** Omit on the first screen: the slot stays empty so the layout does not shift. */
  onBack?: () => void;
  /** Omit for a header with a back button only: the right slot stays empty so the title stays centred. */
  onClose?: () => void;
  /** Omit on Intro and Sent: only the close button shows. */
  progress?: { label: string; fraction: number };
  /** A centred screen title in place of the progress. */
  title?: string;
};

/** Back button, progress label with a thin bar or a centred title, and the close button. */
export function FlowHeader({ onBack, onClose, progress, title }: FlowHeaderProps) {
  const { t } = useTranslation();
  const theme = useTheme();
  const styles = makeStyles(theme);
  const tint = theme.scheme === 'dark' ? 'systemThinMaterialDark' : 'systemThinMaterialLight';

  return (
    <View style={styles.row}>
      <View style={styles.side}>
        {onBack ? <BackButton label={t('questionnaireFill.back')} onPress={onBack} /> : null}
      </View>
      <View style={styles.center}>
        {progress ? (
          <View
            accessible
            accessibilityRole="progressbar"
            accessibilityLabel={progress.label}
            accessibilityValue={{ min: 0, max: 100, now: Math.round(progress.fraction * 100) }}
            style={styles.progress}
          >
            <Text variant="label" color="muted">
              {progress.label}
            </Text>
            <View style={styles.track}>
              <View style={[styles.fill, { width: `${progress.fraction * 100}%` }]} />
            </View>
          </View>
        ) : null}
        {title ? (
          <Text variant="headerTitle" accessibilityRole="header" numberOfLines={1} style={styles.title}>
            {title}
          </Text>
        ) : null}
      </View>
      <View style={styles.side}>
        {onClose ? (
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={t('questionnaireFill.close')}
            onPress={onClose}
            style={styles.close}
          >
            <BlurView intensity={theme.glass.control.intensity} tint={tint} style={styles.blur}>
              <SymbolView
                name="xmark"
                weight="medium"
                tintColor={theme.colors.ink}
                size={theme.sizes.backIcon}
              />
            </BlurView>
          </Pressable>
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
      minHeight: theme.sizes.backButton,
    },
    side: {
      width: theme.sizes.backButton,
      alignItems: 'flex-end',
    },
    center: {
      flex: 1,
      paddingHorizontal: theme.spacing.xxxl,
    },
    title: {
      textAlign: 'center',
    },
    progress: {
      alignItems: 'center',
      gap: theme.spacing.xxs,
    },
    track: {
      alignSelf: 'stretch',
      height: theme.sizes.progressBar,
      borderRadius: theme.radii.pill,
      overflow: 'hidden',
      backgroundColor: theme.colors.lineStrong,
    },
    fill: {
      height: '100%',
      borderRadius: theme.radii.pill,
      backgroundColor: theme.colors.primary,
    },
    close: {
      width: theme.sizes.backButton,
      height: theme.sizes.backButton,
      borderRadius: theme.radii.pill,
      boxShadow: theme.shadows.glass,
    },
    blur: {
      flex: 1,
      overflow: 'hidden',
      alignItems: 'center',
      justifyContent: 'center',
      borderRadius: theme.radii.pill,
      borderWidth: theme.sizes.border,
      borderColor: theme.colors.glassBorder,
      backgroundColor: theme.colors.glassFill,
    },
  });
