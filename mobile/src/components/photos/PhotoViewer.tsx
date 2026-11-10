import { AccessibilityInfo, Alert, ScrollView, StyleSheet, View } from 'react-native';
import { SymbolView } from 'expo-symbols';
import { useTranslation } from 'react-i18next';

import type { ProgressPhoto } from '@/api/photos';
import { GlassButton } from '@/components/profile/GlassButton';
import { Text } from '@/components/Text';
import { ImageViewer } from '@/components/photos/ImageViewer';
import { photoDateLabel } from '@/components/photos/format';
import type { SwipeDirection } from '@/hooks/useHorizontalSwipe';
import { useTheme } from '@/theme';
import type { Theme } from '@/theme';

type PhotoViewerProps = {
  /** The photos that can be swiped through, in the order the gallery shows them. */
  photos: readonly ProgressPhoto[];
  /** The photo to show; the viewer is closed while this is null. */
  selectedId: string | null;
  /** Only photos the signed-in user uploaded can be deleted. */
  canDelete: (photo: ProgressPhoto) => boolean;
  deleting: boolean;
  onSelect: (id: string) => void;
  onClose: () => void;
  onDelete: (photo: ProgressPhoto) => void;
};

/**
 * Full-screen view of one photo with its note and date, with a swipe to the neighbouring photos; a tap on the photo
 * hides everything but the photo.
 */
export function PhotoViewer({ photos, selectedId, canDelete, deleting, onSelect, onClose, onDelete }: PhotoViewerProps) {
  const { t, i18n } = useTranslation();
  const theme = useTheme();
  const styles = makeStyles(theme);

  const index = photos.findIndex((item) => item.id === selectedId);
  const photo = photos[index] ?? null;
  const count = photos.length;

  const swipe = (direction: SwipeDirection) => {
    const target = photos[index + (direction === 'next' ? 1 : -1)];
    if (!target) return;
    onSelect(target.id);
    AccessibilityInfo.announceForAccessibility(
      t('photos.viewer.position', { index: index + (direction === 'next' ? 2 : 0), count }),
    );
  };

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
      imageLabel={
        count > 1
          ? `${t('photos.photoLabel', { date: dateLabel })}, ${t('photos.viewer.position', { index: index + 1, count })}`
          : t('photos.photoLabel', { date: dateLabel })
      }
      closeLabel={t('photos.viewer.close')}
      immersiveHint={t('photos.viewer.toggleHint')}
      onClose={onClose}
      pager={{ onSwipe: swipe, canPrevious: index > 0, canNext: index >= 0 && index < count - 1, contentKey: selectedId ?? '' }}
      actions={
        photo && canDelete(photo) ? (
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
