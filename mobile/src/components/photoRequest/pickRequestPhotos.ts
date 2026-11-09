import * as ImagePicker from 'expo-image-picker';

import { PICK_QUALITY } from '@/api/photos';
import type { PickedPhoto } from '@/api/photos';

export type RequestPhotoSource = 'camera' | 'library';

export type PickBatch =
  | { kind: 'picked'; photos: PickedPhoto[] }
  | { kind: 'cancelled' }
  | { kind: 'denied' };

/** Most photos one request takes; the library picker is capped to what is still free. */
export const MAX_REQUEST_PHOTOS = 10;

const toPicked = (asset: ImagePicker.ImagePickerAsset): PickedPhoto => ({
  uri: asset.uri,
  mimeType: asset.mimeType,
  fileSize: asset.fileSize,
});

/**
 * Takes a photo with the camera or picks several from the library; `denied` means camera access was refused.
 * `room` is how many more photos the request can take.
 */
export async function pickRequestPhotos(source: RequestPhotoSource, room: number): Promise<PickBatch> {
  if (source === 'camera') {
    const permission = await ImagePicker.requestCameraPermissionsAsync();
    if (!permission.granted) return { kind: 'denied' };
  }

  const result =
    source === 'camera'
      ? await ImagePicker.launchCameraAsync({ mediaTypes: ['images'], quality: PICK_QUALITY, allowsMultipleSelection: false })
      : await ImagePicker.launchImageLibraryAsync({
          mediaTypes: ['images'],
          quality: PICK_QUALITY,
          allowsMultipleSelection: true,
          selectionLimit: Math.max(room, 1),
          orderedSelection: true,
        });

  if (result.canceled || result.assets.length === 0) return { kind: 'cancelled' };
  return { kind: 'picked', photos: result.assets.slice(0, Math.max(room, 1)).map(toPicked) };
}
