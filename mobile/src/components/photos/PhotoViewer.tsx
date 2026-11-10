import { Alert, ScrollView, StyleSheet, View } from 'react-native';
import { SymbolView } from 'expo-symbols';
import { useTranslation } from 'react-i18next';

import type { ProgressPhoto } from '@/api/photos';
import { GlassButton } from '@/components/profile/GlassButton';
import { Text } from '@/components/Text';
import { ImageViewer } from '@/components/photos/ImageViewer';
import { photoDateLabel } from '@/components/photos/format';
import { useTheme } from '@/theme';
import type { Theme } from '@/theme';

type PhotoViewerProps = {
  /** The photo to show; the viewer is closed while this is null. */
  photo: ProgressPhoto | null;
  /** Only photos the signed-in user uploaded can be deleted. */
  canDelete: boolean;
  deleting: boolean;
  onClose: () => void;
  onDelete: (photo: ProgressPhoto) => void;
};

/** Full-screen view of one photo with its note and date; a tap on the photo hides everything but the photo. */
export function PhotoViewer({ photo, canDelete, deleting, onClose, onDelete }: PhotoViewerProps) {
  const { t, i18n } = useTranslation();
  const theme = useTheme();
  const styles = makeStyles(theme);

  const confirmDelete = () => {
    if (!photo) return;
    Alert.alert(t('photos.viewer.deleteTitle'), t('photos.viewer.deleteBody'), [
      { text: t('photos.cancel'), style: 'cancel' },
      { text: t('photos.viewer.deleteConfirm'), style: 'destructive', onPress: () => onDelete(photo) },
    ]);
  };

  const dateLabel = photo ? photoDateLabel(i18n.language, photo.takenAt) : '';
  const note = photo?.description?.trim();

  return (
    <ImageViewer
      uri={photo ? photo.displayUrl : null}
      imageLabel={t('photos.photoLabel', { date: dateLabel })}
      closeLabel={t('photos.viewer.close')}
      immersiveHint={t('photos.viewer.toggleHint')}
      onClose={onClose}
      actions={
        canDelete ? (
          <GlassButton label={t('photos.viewer.delete')} onPress={deleting ? undefined : confirmDelete}>
            <SymbolView name="trash" tintColor={theme.colors.error} size={theme.sizes.backIcon} />
          </GlassButton>
        ) : null
      }
      caption={
        <View style={styles.caption}>
          {note ? (
            <ScrollView style={styles.noteScroll} bounces={false}>
              <Text variant="body" color="ink2" style={styles.centered}>
                {note}
              </Text>
            </ScrollView>
          ) : null}
          <Text variant="label" color="muted">
            {dateLabel}
          </Text>
        </View>
      }
    />
  );
}

const makeStyles = (theme: Theme) =>
  StyleSheet.create({
    caption: {
      alignSelf: 'stretch',
      alignItems: 'center',
      gap: theme.spacing.xs,
    },
    noteScroll: {
      maxHeight: theme.sizes.messageField,
    },
    centered: {
      textAlign: 'center',
    },
  });
