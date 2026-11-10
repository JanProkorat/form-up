import * as ImagePicker from 'expo-image-picker';

import { PICK_QUALITY } from '@/api/photos';
import type { PickedPhoto } from '@/api/photos';

export type PhotoSource = 'camera' | 'library';

export type PickResult = { kind: 'picked'; photo: PickedPhoto } | { kind: 'cancelled' } | { kind: 'denied' };

const OPTIONS: ImagePicker.ImagePickerOptions = {
  mediaTypes: ['images'],
  quality: PICK_QUALITY,
  allowsMultipleSelection: false,
  // iOS hands back HEIC by default, which the coach's browser cannot show; this asks for JPEG.
  preferredAssetRepresentationMode: ImagePicker.UIImagePickerPreferredAssetRepresentationMode.Compatible,
};

/** Takes a photo with the camera or picks one from the library; `denied` means camera access was refused. */
export async function pickMealPhoto(source: PhotoSource): Promise<PickResult> {
  if (source === 'camera') {
    const permission = await ImagePicker.requestCameraPermissionsAsync();
    if (!permission.granted) return { kind: 'denied' };
  }
  const result =
    source === 'camera'
      ? await ImagePicker.launchCameraAsync(OPTIONS)
      : await ImagePicker.launchImageLibraryAsync(OPTIONS);
  const asset = result.canceled ? undefined : result.assets[0];
  if (!asset) return { kind: 'cancelled' };
  return { kind: 'picked', photo: { uri: asset.uri, mimeType: asset.mimeType, fileSize: asset.fileSize } };
}
