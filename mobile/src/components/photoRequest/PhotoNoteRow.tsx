import { ActivityIndicator, Pressable, StyleSheet, TextInput, View } from 'react-native';
import { Image } from 'expo-image';
import { SymbolView } from 'expo-symbols';
import { useTranslation } from 'react-i18next';

import { PHOTO_NOTE_MAX_LENGTH } from '@/api/photos';
import { Text } from '@/components/Text';
import { useTheme } from '@/theme';
import type { Theme } from '@/theme';

export type PhotoRowStatus = 'idle' | 'uploading' | 'done' | 'failed';

type PhotoNoteRowProps = {
  uri: string;
  /** 1-based position, for the accessibility labels. */
  position: number;
  /** How the coach is named in the placeholder. */
  name: string;
  note: string;
  onChangeNote: (note: string) => void;
  /** Omit when the photo can no longer be taken back, such as once it is on the request. */
  onRemove?: () => void;
  status?: PhotoRowStatus;
  /** Why the photo failed; shown under the note. */
  error?: string | undefined;
  /** False while the photo is being sent: the note is read-only. */
  editable?: boolean;
};

/** A picked photo with its optional note, a remove button, and the state of its upload. */
export function PhotoNoteRow({
  uri,
  position,
  name,
  note,
  onChangeNote,
  onRemove,
  status = 'idle',
  error,
  editable = true,
}: PhotoNoteRowProps) {
  const { t } = useTranslation();
  const theme = useTheme();
  const styles = makeStyles(theme);
  const label = t('photoRequest.once.noteLabel');

  return (
    <View style={styles.row}>
      <View style={styles.thumb}>
        <Image
          source={{ uri }}
          style={styles.image}
          contentFit="cover"
          accessibilityLabel={t('photoRequest.once.photoLabel', { position })}
        />
        {status === 'uploading' ? (
          <View style={styles.overlay}>
            <ActivityIndicator color={theme.colors.onPrimary} />
          </View>
        ) : null}
        {status === 'done' ? (
          <View style={styles.overlay} accessible accessibilityLabel={t('photoRequest.once.uploaded', { position })}>
            <SymbolView name="checkmark" weight="bold" tintColor={theme.colors.onPrimary} size={theme.sizes.rowGlyph} />
          </View>
        ) : null}
        {onRemove ? (
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={t('photoRequest.once.removeLabel', { position })}
            disabled={!editable}
            hitSlop={theme.spacing.xs}
            onPress={onRemove}
            style={styles.remove}
          >
            <SymbolView name="xmark" weight="semibold" tintColor={theme.colors.onPrimary} size={theme.sizes.chipRemoveGlyph} />
          </Pressable>
        ) : null}
      </View>
      <View style={styles.body}>
        <Text variant="captionStrong" color="muted">
          {label}{' '}
          <Text variant="caption" color="muted">
            {t('photoRequest.once.optional')}
          </Text>
        </Text>
        <TextInput
          accessibilityLabel={`${label} ${position}`}
          multiline
          editable={editable && status !== 'done'}
          value={note}
          onChangeText={onChangeNote}
          maxLength={PHOTO_NOTE_MAX_LENGTH}
          placeholder={t('photoRequest.once.notePlaceholder', { name })}
          placeholderTextColor={theme.colors.muted}
          selectionColor={theme.colors.accent}
          textAlignVertical="top"
          style={[styles.field, error ? styles.fieldInvalid : null]}
        />
        {error ? (
          <Text variant="caption" color="error" accessibilityRole="alert">
            {error}
          </Text>
        ) : null}
      </View>
    </View>
  );
}

const makeStyles = (theme: Theme) =>
  StyleSheet.create({
    row: {
      flexDirection: 'row',
      gap: theme.spacing.md,
      paddingVertical: theme.spacing.md,
    },
    thumb: {
      width: theme.sizes.noteRowThumbWidth,
      height: theme.sizes.noteRowThumbHeight,
      borderRadius: theme.radii.md,
      overflow: 'hidden',
      backgroundColor: theme.colors.field,
    },
    image: {
      flex: 1,
    },
    overlay: {
      position: 'absolute',
      top: 0,
      right: 0,
      bottom: 0,
      left: 0,
      alignItems: 'center',
      justifyContent: 'center',
      backgroundColor: theme.colors.primary,
      opacity: theme.opacity.dimmed,
    },
    remove: {
      position: 'absolute',
      top: theme.spacing.xxs,
      right: theme.spacing.xxs,
      width: theme.sizes.noteRowRemove,
      height: theme.sizes.noteRowRemove,
      borderRadius: theme.radii.pill,
      alignItems: 'center',
      justifyContent: 'center',
      backgroundColor: theme.colors.primary,
    },
    body: {
      flex: 1,
      minWidth: 0,
      gap: theme.spacing.snug,
    },
    field: {
      ...theme.typography.bodySmall,
      minHeight: theme.sizes.metricNoteField,
      paddingVertical: theme.spacing.xs,
      paddingHorizontal: theme.spacing.sm,
      borderRadius: theme.radii.md,
      borderWidth: theme.sizes.border,
      borderColor: theme.colors.fieldBorder,
      backgroundColor: theme.colors.field,
      color: theme.colors.ink,
    },
    fieldInvalid: {
      borderColor: theme.colors.error,
    },
  });
