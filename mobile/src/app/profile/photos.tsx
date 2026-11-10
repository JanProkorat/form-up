import { useState } from 'react';
import { ActivityIndicator, Alert, Linking, StyleSheet, View } from 'react-native';
import * as ImagePicker from 'expo-image-picker';
import { SymbolView } from 'expo-symbols';
import { useRouter } from 'expo-router';
import { useTranslation } from 'react-i18next';
import { useSafeAreaInsets } from 'react-native-safe-area-context';

import { PICK_QUALITY, PhotoUploadError } from '@/api/photos';
import type { PickedPhoto, ProgressPhoto } from '@/api/photos';
import { Button } from '@/components/Button';
import { MonthSection } from '@/components/photos/MonthSection';
import { PhotoViewer } from '@/components/photos/PhotoViewer';
import { Screen } from '@/components/Screen';
import { ScreenHeader } from '@/components/ScreenHeader';
import { Text } from '@/components/Text';
import { useDeletePhoto } from '@/hooks/useDeletePhoto';
import { usePhotos } from '@/hooks/usePhotos';
import { useProfile } from '@/hooks/useProfile';
import { useProfileSummary } from '@/hooks/useProfileSummary';
import { useUploadPhoto } from '@/hooks/useUploadPhoto';
import { showsError } from '@/lib/queryState';
import { useTheme } from '@/theme';
import type { Theme } from '@/theme';

const MAX_LIBRARY_SELECTION = 10;

const CAMERA_OPTIONS: ImagePicker.ImagePickerOptions = {
  mediaTypes: ['images'],
  quality: PICK_QUALITY,
  allowsMultipleSelection: false,
};

const LIBRARY_OPTIONS: ImagePicker.ImagePickerOptions = {
  mediaTypes: ['images'],
  quality: PICK_QUALITY,
  allowsMultipleSelection: true,
  selectionLimit: MAX_LIBRARY_SELECTION,
  orderedSelection: true,
  // iOS hands back HEIC by default, which the coach's browser cannot show; this asks for JPEG.
  preferredAssetRepresentationMode: ImagePicker.UIImagePickerPreferredAssetRepresentationMode.Compatible,
};

type FailureKey = 'tooLarge' | 'unsupportedType' | 'upload';

const failureKey = (error: unknown): FailureKey => {
  const failure = error instanceof PhotoUploadError ? error.failure : 'upload';
  return failure === 'tooLarge' || failure === 'unsupportedType' ? failure : 'upload';
};

/** The client's progress photos by month, with upload from the camera or library and delete. */
export default function ProgressPhotosScreen() {
  const { t } = useTranslation();
  const theme = useTheme();
  const styles = makeStyles(theme);
  const insets = useSafeAreaInsets();
  const router = useRouter();
  const photosQuery = usePhotos();
  const summaryQuery = useProfileSummary();
  const { data: profile } = useProfile();
  const upload = useUploadPhoto();
  const remove = useDeletePhoto();
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [progress, setProgress] = useState<{ done: number; total: number } | null>(null);
  const [picking, setPicking] = useState(false);
  const busy = picking || progress !== null;

  const months = photosQuery.data ?? [];
  const shownCount = months.reduce((total, month) => total + month.photos.length, 0);
  const count = summaryQuery.data?.progressPhotoCount ?? shownCount;
  const selected = months.flatMap((month) => month.photos).find((photo) => photo.id === selectedId) ?? null;
  const isOwnPhoto =
    selected?.uploadedByUserId !== undefined &&
    profile?.userId !== undefined &&
    selected.uploadedByUserId.toLowerCase() === profile.userId.toLowerCase();

  const submitBatch = async (photos: PickedPhoto[]) => {
    const failures: FailureKey[] = [];
    setProgress({ done: 0, total: photos.length });
    for (const [index, photo] of photos.entries()) {
      try {
        await upload.mutateAsync(photo);
      } catch (error) {
        failures.push(failureKey(error));
      }
      setProgress({ done: index + 1, total: photos.length });
    }
    setProgress(null);
    if (failures.length > 0) {
      const reason = failures.every((key) => key === failures[0]) ? failures[0] : 'upload';
      Alert.alert(t('photos.errors.batchFailed', { count: failures.length }), t(`photos.errors.${reason}`));
    }
  };

  const handleResult = (result: ImagePicker.ImagePickerResult) => {
    if (result.canceled || result.assets.length === 0) return;
    void submitBatch(
      result.assets.map((asset) => ({ uri: asset.uri, mimeType: asset.mimeType, fileSize: asset.fileSize })),
    );
  };

  // The picker resolves only after re-encoding the assets, so show the busy state from launch.
  const pickAndSubmit = async (launch: () => Promise<ImagePicker.ImagePickerResult>) => {
    setPicking(true);
    try {
      handleResult(await launch());
    } finally {
      setPicking(false);
    }
  };

  const takePhoto = async () => {
    const permission = await ImagePicker.requestCameraPermissionsAsync();
    if (!permission.granted) {
      Alert.alert(t('photos.permission.title'), t('photos.permission.body'), [
        { text: t('photos.cancel'), style: 'cancel' },
        { text: t('photos.permission.openSettings'), onPress: () => void Linking.openSettings() },
      ]);
      return;
    }
    await pickAndSubmit(() => ImagePicker.launchCameraAsync(CAMERA_OPTIONS));
  };

  const chooseFromLibrary = () => pickAndSubmit(() => ImagePicker.launchImageLibraryAsync(LIBRARY_OPTIONS));

  const askSource = () => {
    Alert.alert(t('photos.add.title'), undefined, [
      { text: t('photos.add.camera'), onPress: () => void takePhoto() },
      { text: t('photos.add.library'), onPress: () => void chooseFromLibrary() },
      { text: t('photos.cancel'), style: 'cancel' },
    ]);
  };

  const deleteSelected = (photo: ProgressPhoto) => {
    remove.mutate(photo.id, {
      onSuccess: () => setSelectedId(null),
      onError: () => Alert.alert(t('photos.errors.delete')),
    });
  };

  return (
    <View style={styles.root}>
      <Screen
        scroll
        gutter={theme.spacing.xl}
        topGap={theme.spacing.safeAreaGap}
        bottomInset={theme.sizes.buttonLarge + theme.spacing.huge * 2}
        contentStyle={styles.content}
      >
        <ScreenHeader
          title={t('photos.title')}
          subtitle={photosQuery.isSuccess ? t('photos.count', { count }) : undefined}
          backLabel={t('onboarding.back')}
          onBack={() => router.back()}
        />

        {photosQuery.isPending ? (
          <View style={styles.state}>
            <ActivityIndicator color={theme.colors.accent} />
          </View>
        ) : showsError(photosQuery) ? (
          <View style={styles.state}>
            <Text variant="body" color="muted" style={styles.centered}>
              {t('photos.errors.load')}
            </Text>
            <Button label={t('photos.errors.retry')} onPress={() => void photosQuery.refetch()} />
          </View>
        ) : months.length === 0 ? (
          <View style={styles.state}>
            <Text variant="bodyStrong" style={styles.centered}>
              {t('photos.empty')}
            </Text>
            <Text variant="body" color="muted" style={styles.centered}>
              {t('photos.emptyHint')}
            </Text>
          </View>
        ) : (
          <View style={styles.months}>
            {months.map((month) => (
              <MonthSection key={month.yearMonth} month={month} onOpen={(photo) => setSelectedId(photo.id)} />
            ))}
          </View>
        )}
      </Screen>

      <View style={[styles.action, { bottom: insets.bottom + theme.spacing.xxxl }]}>
        <Button
          label={progress ? t('photos.uploadProgress', progress) : t('photos.addPhotos')}
          onPress={askSource}
          disabled={busy}
          icon={
            busy ? (
              <ActivityIndicator color={theme.colors.onPrimary} />
            ) : (
              <SymbolView name="photo.badge.plus" tintColor={theme.colors.onPrimary} size={theme.sizes.fieldIcon} />
            )
          }
        />
      </View>

      <PhotoViewer
        photo={selected}
        canDelete={isOwnPhoto}
        deleting={remove.isPending}
        onClose={() => setSelectedId(null)}
        onDelete={deleteSelected}
      />
    </View>
  );
}

const makeStyles = (theme: Theme) =>
  StyleSheet.create({
    root: {
      flex: 1,
      backgroundColor: theme.colors.ground,
    },
    content: {
      gap: theme.spacing.lg,
    },
    months: {
      gap: theme.spacing.xl,
    },
    state: {
      alignItems: 'center',
      gap: theme.spacing.md,
      paddingVertical: theme.spacing.huge,
    },
    centered: {
      textAlign: 'center',
    },
    action: {
      position: 'absolute',
      left: theme.spacing.xxl,
      right: theme.spacing.xxl,
    },
  });
