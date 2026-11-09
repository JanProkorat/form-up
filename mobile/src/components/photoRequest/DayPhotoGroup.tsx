import { StyleSheet, View } from 'react-native';
import { Image } from 'expo-image';
import { useTranslation } from 'react-i18next';

import type { ProgressPhoto } from '@/api/photos';
import { Text } from '@/components/Text';
import { dayGroupTitle } from '@/components/photoRequest/format';
import { useTheme } from '@/theme';
import type { Theme } from '@/theme';

type DayPhotoGroupProps = {
  date: Date;
  photos: readonly ProgressPhoto[];
};

/** One day's photos under "date · count": a thumbnail and its note, or "No note". */
export function DayPhotoGroup({ date, photos }: DayPhotoGroupProps) {
  const { t, i18n } = useTranslation();
  const theme = useTheme();
  const styles = makeStyles(theme);

  return (
    <View style={styles.group}>
      <Text variant="captionStrong" color="muted" accessibilityRole="header">
        {dayGroupTitle(t, i18n.language, date, photos.length)}
      </Text>
      <View style={styles.list}>
        {photos.map((photo, index) => {
          const note = photo.description?.trim();
          return (
            <View key={photo.id} style={styles.row}>
              <Image
                source={{ uri: photo.displayUrl }}
                style={styles.thumb}
                contentFit="cover"
                accessibilityLabel={t('photoRequest.once.photoLabel', { position: index + 1 })}
              />
              <Text variant="bodySmall" color={note ? 'ink2' : 'muted'} style={styles.note}>
                {note || t('photoRequest.daily.noNote')}
              </Text>
            </View>
          );
        })}
      </View>
    </View>
  );
}

const makeStyles = (theme: Theme) =>
  StyleSheet.create({
    group: {
      gap: theme.spacing.sm,
    },
    list: {
      gap: theme.spacing.xs,
    },
    row: {
      flexDirection: 'row',
      alignItems: 'flex-start',
      gap: theme.spacing.sm,
    },
    thumb: {
      width: theme.sizes.mealThumb,
      height: (theme.sizes.mealThumb * 4) / 3,
      borderRadius: theme.radii.sm,
      backgroundColor: theme.colors.field,
    },
    note: {
      flex: 1,
      minWidth: 0,
    },
  });
