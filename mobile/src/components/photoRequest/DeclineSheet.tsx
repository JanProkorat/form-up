import { useState } from 'react';
import { KeyboardAvoidingView, Modal, Platform, Pressable, StyleSheet, TextInput, View } from 'react-native';
import { useTranslation } from 'react-i18next';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import type { EdgeInsets } from 'react-native-safe-area-context';

import { DISMISS_REASON_MAX_LENGTH } from '@/api/photoRequests';
import { Button } from '@/components/Button';
import { Text } from '@/components/Text';
import { useTheme } from '@/theme';
import type { Theme } from '@/theme';

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

/** Decline sheet: an optional reason, up to 500 characters, then Decline request or Keep it. */
export function DeclineSheet({ visible, ...rest }: DeclineSheetProps) {
  return (
    <Modal visible={visible} transparent animationType="fade" statusBarTranslucent onRequestClose={rest.onClose}>
      <SheetContent {...rest} />
    </Modal>
  );
}

function SheetContent({ name, busy, error, onSubmit, onClose }: Omit<DeclineSheetProps, 'visible'>) {
  const { t } = useTranslation();
  const theme = useTheme();
  const insets = useSafeAreaInsets();
  const styles = makeStyles(theme, insets);
  const [reason, setReason] = useState('');
  const label = t('photoRequest.decline.reasonLabel', { name });

  return (
    <KeyboardAvoidingView style={styles.root} behavior={Platform.OS === 'ios' ? 'padding' : undefined}>
      <View accessibilityViewIsModal accessibilityLabel={t('photoRequest.decline.title', { name })} style={styles.sheet}>
        <View style={styles.handle} />
        <View style={styles.copy}>
          <Text variant="heading" accessibilityRole="header">
            {t('photoRequest.decline.title', { name })}
          </Text>
          <Text variant="bodyLarge" color="muted">
            {t('photoRequest.decline.body', { name })}
          </Text>
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
        <Pressable accessibilityRole="button" disabled={busy} onPress={onClose} style={styles.keep}>
          <Text variant="bodyStrong" color="ink2">
            {t('photoRequest.decline.keep')}
          </Text>
        </Pressable>
      </View>
    </KeyboardAvoidingView>
  );
}

const makeStyles = (theme: Theme, insets: EdgeInsets) =>
  StyleSheet.create({
    root: {
      flex: 1,
      justifyContent: 'flex-end',
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
    copy: {
      gap: theme.spacing.xs,
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
    keep: {
      alignItems: 'center',
      justifyContent: 'center',
      minHeight: theme.sizes.buttonMedium,
    },
  });
