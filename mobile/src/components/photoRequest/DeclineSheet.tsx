import { useCallback, useEffect, useState } from 'react';
import {
  AccessibilityInfo,
  Animated,
  KeyboardAvoidingView,
  Modal,
  Platform,
  Pressable,
  StyleSheet,
  TextInput,
  useWindowDimensions,
  View,
} from 'react-native';
import { SymbolView } from 'expo-symbols';
import { useTranslation } from 'react-i18next';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import type { EdgeInsets } from 'react-native-safe-area-context';

import { DISMISS_REASON_MAX_LENGTH } from '@/api/photoRequests';
import { Button } from '@/components/Button';
import { Text } from '@/components/Text';
import { useTheme } from '@/theme';
import type { Theme } from '@/theme';
import { requestSheetMotion, sheetDragMotion } from '@/theme/motion';

type DeclineSheetProps = {
  visible: boolean;
  /** How the coach is named in the copy. */
  name: string;
  busy: boolean;
  /** Message about the last failed attempt; the sheet stays open under it. */
  error: string | null;
  onSubmit: (reason: string) => void;
  onClose: () => void;
};

/**
 * Decline sheet: an optional reason, up to 500 characters, then Decline request. It closes with the X, a tap
 * outside it or the Android back button, but not while a decline is being sent. The Modal stays mounted after
 * `visible` turns false, until the content has slid out.
 */
export function DeclineSheet({ visible, ...rest }: DeclineSheetProps) {
  const [mounted, setMounted] = useState(visible);
  if (visible && !mounted) setMounted(true);
  const handleExited = useCallback(() => setMounted(false), []);
  const requestClose = () => {
    if (!rest.busy) rest.onClose();
  };
  return (
    <Modal visible={mounted} transparent animationType="none" statusBarTranslucent onRequestClose={requestClose}>
      <SheetContent {...rest} onClose={requestClose} open={visible} onExited={handleExited} />
    </Modal>
  );
}

function SheetContent({
  name,
  busy,
  error,
  onSubmit,
  onClose,
  open,
  onExited,
}: Omit<DeclineSheetProps, 'visible'> & {
  /** False once the parent hides the sheet: the content slides out, then reports `onExited`. */
  open: boolean;
  onExited: () => void;
}) {
  const { t } = useTranslation();
  const theme = useTheme();
  const insets = useSafeAreaInsets();
  const styles = makeStyles(theme, insets);
  const { height: windowHeight } = useWindowDimensions();
  const [translateY] = useState(() => new Animated.Value(windowHeight));
  const [backdropOpacity] = useState(() => new Animated.Value(0));
  const [reason, setReason] = useState('');
  const label = t('photoRequest.decline.reasonLabel', { name });

  // The sheet slides up while the backdrop fades in, and the reverse on close; Reduce Motion jumps.
  useEffect(() => {
    let cancelled = false;
    void AccessibilityInfo.isReduceMotionEnabled().then((reduced) => {
      if (cancelled) return;
      const sheetTarget = open ? 0 : windowHeight;
      const backdropTarget = open ? 1 : 0;
      if (reduced) {
        translateY.setValue(sheetTarget);
        backdropOpacity.setValue(backdropTarget);
        if (!open) onExited();
        return;
      }
      Animated.parallel([
        Animated.spring(translateY, {
          toValue: sheetTarget,
          ...sheetDragMotion.spring,
          overshootClamping: true,
          useNativeDriver: true,
        }),
        Animated.timing(backdropOpacity, {
          toValue: backdropTarget,
          duration: requestSheetMotion.backdropDuration,
          useNativeDriver: true,
        }),
      ]).start(({ finished }) => {
        if (finished && !open) onExited();
      });
    });
    return () => {
      cancelled = true;
      translateY.stopAnimation();
      backdropOpacity.stopAnimation();
    };
  }, [open, windowHeight, translateY, backdropOpacity, onExited]);

  return (
    <KeyboardAvoidingView
      style={styles.root}
      behavior={Platform.OS === 'ios' ? 'padding' : undefined}
      pointerEvents={open ? 'auto' : 'none'}
    >
      {/* The dim fades on its own; the Pressable sits behind the sheet, so only taps outside it reach it. */}
      <Animated.View style={[StyleSheet.absoluteFill, styles.backdrop, { opacity: backdropOpacity }]}>
        <Pressable accessible={false} onPress={onClose} style={StyleSheet.absoluteFill} />
      </Animated.View>
      <Animated.View
        accessibilityViewIsModal
        accessibilityLabel={t('photoRequest.decline.title', { name })}
        style={[styles.sheet, { transform: [{ translateY }] }]}
      >
        <View style={styles.handle} />
        <View style={styles.header}>
          <View style={styles.copy}>
            <Text variant="heading" accessibilityRole="header">
              {t('photoRequest.decline.title', { name })}
            </Text>
            <Text variant="bodyLarge" color="muted">
              {t('photoRequest.decline.body', { name })}
            </Text>
          </View>
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={t('photoRequest.decline.close')}
            disabled={busy}
            hitSlop={theme.spacing.xs}
            onPress={onClose}
            style={styles.close}
          >
            <SymbolView name="xmark" tintColor={theme.colors.ink} size={theme.sizes.sheetCloseGlyph} />
          </Pressable>
        </View>
        <View style={styles.field}>
          <Text variant="labelStrong">{label}</Text>
          <TextInput
            accessibilityLabel={label}
            multiline
            value={reason}
            onChangeText={setReason}
            maxLength={DISMISS_REASON_MAX_LENGTH}
            placeholderTextColor={theme.colors.muted}
            selectionColor={theme.colors.accent}
            textAlignVertical="top"
            style={[styles.area, error ? styles.areaInvalid : null]}
          />
          <Text variant="caption" color="muted" style={styles.counter}>
            {t('photoRequest.decline.counter', { used: reason.length, max: DISMISS_REASON_MAX_LENGTH })}
          </Text>
        </View>
        {error ? (
          <Text variant="label" color="error" accessibilityRole="alert">
            {error}
          </Text>
        ) : null}
        <Button label={t('photoRequest.decline.submit')} onPress={() => onSubmit(reason)} disabled={busy} />
      </Animated.View>
    </KeyboardAvoidingView>
  );
}

const makeStyles = (theme: Theme, insets: EdgeInsets) =>
  StyleSheet.create({
    root: {
      flex: 1,
      justifyContent: 'flex-end',
    },
    backdrop: {
      backgroundColor: theme.glass.sheet.backdrop[theme.scheme],
    },
    sheet: {
      gap: theme.spacing.lg,
      paddingTop: theme.spacing.xs,
      paddingHorizontal: theme.spacing.xxl,
      paddingBottom: Math.max(insets.bottom, theme.spacing.xl),
      borderTopLeftRadius: theme.glass.sheet.radius,
      borderTopRightRadius: theme.glass.sheet.radius,
      backgroundColor: theme.colors.surface,
    },
    handle: {
      alignSelf: 'center',
      width: theme.glass.sheet.handleWidth,
      height: theme.glass.sheet.handleHeight,
      borderRadius: theme.radii.xs,
      backgroundColor: theme.colors.lineStrong,
    },
    header: {
      flexDirection: 'row',
      alignItems: 'flex-start',
      gap: theme.spacing.md,
    },
    copy: {
      flex: 1,
      minWidth: 0,
      gap: theme.spacing.xs,
    },
    close: {
      width: theme.sizes.sheetClose,
      height: theme.sizes.sheetClose,
      borderRadius: theme.radii.pill,
      alignItems: 'center',
      justifyContent: 'center',
      backgroundColor: theme.colors.lineStrong,
    },
    field: {
      gap: theme.spacing.snug,
    },
    area: {
      ...theme.typography.bodyLarge,
      minHeight: theme.sizes.messageField,
      paddingVertical: theme.spacing.md,
      paddingHorizontal: theme.spacing.lg,
      borderRadius: theme.radii.field,
      borderWidth: theme.sizes.border,
      borderColor: theme.colors.fieldBorder,
      backgroundColor: theme.colors.field,
      color: theme.colors.ink,
    },
    areaInvalid: {
      borderColor: theme.colors.error,
    },
    counter: {
      textAlign: 'right',
    },
  });
