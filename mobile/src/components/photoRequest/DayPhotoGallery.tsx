import { Pressable, StyleSheet, View } from 'react-native';
import { Image } from 'expo-image';
import { useTranslation } from 'react-i18next';

import type { ProgressPhoto } from '@/api/photos';
import { Text } from '@/components/Text';
import { formatRequestDay } from '@/components/photoRequest/format';
import { useTheme } from '@/theme';
import type { Theme } from '@/theme';

const COLUMNS = 2;

type DayPhotoGalleryProps = {
  date: Date;
  photos: readonly ProgressPhoto[];
  onOpen: (photo: ProgressPhoto) => void;
};

/** One day's photos in a two-column grid like Progress photos, each with its note over a dark scrim. */
export function DayPhotoGallery({ date, photos, onOpen }: DayPhotoGalleryProps) {
  const { t, i18n } = useTranslation();
  const theme = useTheme();
  const styles = makeStyles(theme);
  const dateLabel = formatRequestDay(i18n.language, date);

  return (
    <View style={styles.group}>
      {photos.map((photo, index) => {
        const note = photo.description?.trim();
        const label = [t('photos.viewer.position', { index: index + 1, count: photos.length }), dateLabel, note]
          .filter(Boolean)
          .join(', ');
        return (
          <View key={photo.id} style={styles.cell}>
            <Pressable
              accessibilityRole="imagebutton"
              accessibilityLabel={label}
              onPress={() => onOpen(photo)}
              style={({ pressed }) => [styles.photo, pressed ? styles.pressed : null]}
            >
              <Image source={{ uri: photo.displayUrl }} style={styles.image} contentFit="cover" />
              {note ? (
                <View pointerEvents="none" style={styles.scrim}>
                  <Text variant="caption" numberOfLines={2} style={styles.note}>
                    {note}
                  </Text>
                </View>
              ) : null}
            </Pressable>
          </View>
        );
      })}
    </View>
  );
}

const makeStyles = (theme: Theme) =>
  StyleSheet.create({
    // Same grid as the Progress photos gallery (components/photos/MonthSection): the negative margin cancels the
    // cells' outer padding, so the grid sits flush with the content edges.
    group: {
      flexDirection: 'row',
      flexWrap: 'wrap',
      marginHorizontal: -theme.spacing.snug / 2,
    },
    cell: {
      width: `${100 / COLUMNS}%`,
      padding: theme.spacing.snug / 2,
    },
    photo: {
      aspectRatio: 3 / 4,
      borderRadius: theme.radii.md,
      overflow: 'hidden',
      backgroundColor: theme.colors.field,
    },
    pressed: {
      opacity: theme.opacity.pressed,
    },
    image: {
      flex: 1,
    },
    scrim: {
      position: 'absolute',
      left: 0,
      right: 0,
      bottom: 0,
      paddingHorizontal: theme.spacing.xs,
      paddingBottom: theme.spacing.xs,
      paddingTop: theme.spacing.xl,
      experimental_backgroundImage: theme.glass.photoCaption.gradient,
    },
    note: {
      color: theme.glass.photoCaption.text,
    },
  });
