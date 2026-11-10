import { useRef, useState } from 'react';
import { ActivityIndicator, Alert, Linking, StyleSheet, View } from 'react-native';
import { Redirect, useRouter } from 'expo-router';
import { SymbolView } from 'expo-symbols';
import { useTranslation } from 'react-i18next';

import { classifyPhotoRequestError, classifyPhotoUploadError } from '@/api/photoRequests';
import type { PhotoUploadFailureKind } from '@/api/photoRequests';
import type { PickedPhoto, ProgressPhoto } from '@/api/photos';
import { getErrorStatus } from '@/api/errors';
import { Button } from '@/components/Button';
import { Card } from '@/components/Card';
import { Text } from '@/components/Text';
import { DayPhotoGallery } from '@/components/photoRequest/DayPhotoGallery';
import { PhotoNoteRow } from '@/components/photoRequest/PhotoNoteRow';
import type { PhotoRowStatus } from '@/components/photoRequest/PhotoNoteRow';
import { RequestMessage } from '@/components/photoRequest/RequestMessage';
import { coachLabel } from '@/components/photoRequest/format';
import {
  useLeaveRequest,
  usePhotoRequest,
  useRefreshPhotoRequests,
  useRequestCoach,
  useRequestId,
  useSubmitPhotoRequest,
  useUploadRequestPhoto,
} from '@/components/photoRequest/hooks';
import { closedRoute, flowRoute, isAnswerable, requestMode, requestPhotos, sentRoute } from '@/components/photoRequest/model';
import { MAX_REQUEST_PHOTOS, pickRequestPhotos } from '@/components/photoRequest/pickRequestPhotos';
import type { RequestPhotoSource } from '@/components/photoRequest/pickRequestPhotos';
import { PhotoViewer } from '@/components/photos/PhotoViewer';
import { FlowFrame } from '@/components/questionnaire/FlowFrame';
import { useDeletePhoto } from '@/hooks/useDeletePhoto';
import { useProfile } from '@/hooks/useProfile';
import { usePhotos } from '@/hooks/usePhotos';
import { useTheme } from '@/theme';
import type { Theme } from '@/theme';

/** One picked photo. It stays here, with its note, until the request is submitted. */
type PhotoRow = {
  key: number;
  photo: PickedPhoto;
  note: string;
  status: PhotoRowStatus;
  failure?: Exclude<PhotoUploadFailureKind, 'closed'> | undefined;
};

/** One-time request: pick photos, add a note to each, send them together. */
export default function PhotoRequestOnceScreen() {
  const { t } = useTranslation();
  const router = useRouter();
  const theme = useTheme();
  const styles = makeStyles(theme);
  const id = useRequestId();
  const leave = useLeaveRequest();
  const query = usePhotoRequest(id);
  const coach = useRequestCoach(query.data);
  const refresh = useRefreshPhotoRequests();
  const upload = useUploadRequestPhoto();
  const submit = useSubmitPhotoRequest();
  const [rows, setRows] = useState<PhotoRow[]>([]);
  const [sending, setSending] = useState(false);
  const [picking, setPicking] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const nextKey = useRef(1);
  // The photos already on the request, read fresh on every visit, so coming back shows them as sent.
  const photos = usePhotos({ fresh: true });
  const [viewerId, setViewerId] = useState<string | null>(null);
  const { data: profile } = useProfile();
  const remove = useDeletePhoto();

  if (query.isPending) return <RequestMessage kind="loading" onClose={leave} />;

  if (query.isError) {
    // A request that is gone (or not the caller's) is a closed request, not a failure.
    if (getErrorStatus(query.error) === 404) return <Redirect href={closedRoute(id)} />;
    return (
      <RequestMessage kind="retry" body={t('photoRequest.errors.load')} onRetry={() => void query.refetch()} onClose={leave} />
    );
  }

  // Narrowed once here: the handlers below run later and must not depend on the query's union.
  const current = query.data;
  if (!isAnswerable(current)) return <Redirect href={closedRoute(id)} />;
  const mode = requestMode(current);
  if (mode !== 'once') return <Redirect href={flowRoute(current.id, mode)} />;

  const name = coachLabel(t, coach);
  const message = current.message?.trim();
  const busy = sending || picking;
  const sentRows = requestPhotos(photos.data, current.id);
  const pending = rows.filter((row) => row.status !== 'done');
  const full = sentRows.length + rows.length >= MAX_REQUEST_PHOTOS;
  // Sending also submits the request, so photos already on it are enough to send even with nothing new picked.
  const canSend = rows.length > 0 || sentRows.length > 0;
  const isOwnPhoto = (photo: ProgressPhoto) =>
    photo.uploadedByUserId !== undefined &&
    profile?.userId !== undefined &&
    photo.uploadedByUserId.toLowerCase() === profile.userId.toLowerCase();
  const deleteViewed = (photo: ProgressPhoto) => {
    // After a delete the viewer moves to the next photo, or the previous one, and closes when none is left.
    const at = sentRows.findIndex((item) => item.id === photo.id);
    const neighbour = sentRows[at + 1] ?? sentRows[at - 1];
    remove.mutate(photo.id, {
      onSuccess: () => setViewerId(neighbour?.id ?? null),
      onError: () => Alert.alert(t('photos.errors.delete')),
    });
  };

  const patchRow = (key: number, patch: Partial<Omit<PhotoRow, 'key' | 'photo'>>) =>
    setRows((list) => list.map((row) => (row.key === key ? { ...row, ...patch } : row)));

  const toClosed = async () => {
    // The request changed under the client (cancelled, gone): reload it, then explain.
    await refresh(current.id);
    router.replace(closedRoute(current.id));
  };

  const pick = async (source: RequestPhotoSource) => {
    setPicking(true);
    try {
      const result = await pickRequestPhotos(source, MAX_REQUEST_PHOTOS - sentRows.length - rows.length);
      if (result.kind === 'denied') {
        Alert.alert(t('photos.permission.title'), t('photos.permission.body'), [
          { text: t('photos.cancel'), style: 'cancel' },
          { text: t('photos.permission.openSettings'), onPress: () => void Linking.openSettings() },
        ]);
      } else if (result.kind === 'picked') {
        const added = result.photos.map<PhotoRow>((photo) => ({ key: nextKey.current++, photo, note: '', status: 'idle' }));
        setRows((list) => [...list, ...added].slice(0, Math.max(MAX_REQUEST_PHOTOS - sentRows.length, 0)));
        setNotice(null);
      }
    } finally {
      setPicking(false);
    }
  };

  const askSource = () => {
    Alert.alert(t('photos.add.title'), undefined, [
      { text: t('photos.add.camera'), onPress: () => void pick('camera') },
      { text: t('photos.add.library'), onPress: () => void pick('library') },
      { text: t('photos.cancel'), style: 'cancel' },
    ]);
  };

  const send = async () => {
    if (busy || !canSend) return;
    setSending(true);
    setNotice(null);
    try {
      // Every photo that uploads is on the request at once, so only the rows not done yet are sent again.
      let failed = false;
      let offline = false;
      for (const row of pending) {
        patchRow(row.key, { status: 'uploading', failure: undefined });
        try {
          await upload.mutateAsync({
            photo: row.photo,
            target: { requestId: current.id, planId: current.planId, note: row.note },
          });
          patchRow(row.key, { status: 'done' });
        } catch (error) {
          const failure = classifyPhotoUploadError(error);
          if (failure === 'closed') {
            await toClosed();
            return;
          }
          patchRow(row.key, { status: 'failed', failure });
          failed = true;
          // The connection is down: the rest would fail the same way.
          if (failure === 'network') {
            offline = true;
            break;
          }
        }
      }
      // Rows left untouched by a stop are idle again.
      setRows((list) => list.map((row) => (row.status === 'uploading' ? { ...row, status: 'idle' } : row)));
      if (failed) {
        setNotice(t('photoRequest.once.errors.someFailed'));
        if (!offline) {
          // The photos that went through now come from the request's own list, so they leave the picked rows.
          await refresh(current.id);
          setRows((list) => list.filter((row) => row.status !== 'done'));
        }
        return;
      }

      try {
        await submit.mutateAsync(current.id);
      } catch (error) {
        const failure = classifyPhotoRequestError(error);
        if (failure === 'closed') {
          await toClosed();
          return;
        }
        setNotice(t(failure === 'network' ? 'photoRequest.once.errors.network' : 'photoRequest.once.errors.submit'));
        return;
      }
      // Leave first: refreshing the request here would show the completed request as closed.
      router.replace(sentRoute(current.id, sentRows.length + rows.length));
      void refresh(current.id);
    } finally {
      setSending(false);
    }
  };

  const sendLabel =
    pending.length === 0 ? t('photoRequest.once.sendNone', { name }) : t('photoRequest.once.send', { count: pending.length, name });
  const addIcon = picking ? (
    <ActivityIndicator color={theme.colors.onPrimary} />
  ) : (
    <SymbolView name="photo.badge.plus" weight="medium" tintColor={theme.colors.onPrimary} size={theme.sizes.rowGlyph} />
  );
  // Picked photos, or photos already on the request, make the footer Send; with neither it adds the first photos.
  const footerSends = canSend;

  return (
    <>
    <FlowFrame
      header={{ onBack: leave, title: t('photoRequest.once.title') }}
      contentStyle={styles.content}
      footer={
        <>
          {notice ? (
            <Text variant="label" color="error" accessibilityRole="alert" style={styles.centered}>
              {notice}
            </Text>
          ) : null}
          {footerSends ? (
            <Button
              label={sending ? t('photoRequest.once.sending') : sendLabel}
              onPress={() => void send()}
              disabled={busy || !canSend}
              icon={
                sending ? (
                  <ActivityIndicator color={theme.colors.onPrimary} />
                ) : (
                  <SymbolView name="arrow.up" weight="semibold" tintColor={theme.colors.onPrimary} size={theme.sizes.rowGlyph} />
                )
              }
            />
          ) : (
            <Button label={t('photoRequest.actions.addPhotos')} onPress={askSource} disabled={busy} icon={addIcon} />
          )}
        </>
      }
    >
      {message ? (
        <Card style={styles.note}>
          <Text variant="captionStrong" color="muted">
            {t('photoRequest.note', { name })}
          </Text>
          <Text variant="body" color="ink2">
            {message}
          </Text>
        </Card>
      ) : null}
      {sentRows.length > 0 ? (
        <View style={styles.sent}>
          <Text variant="caption" color="muted">
            {t('photoRequest.once.alreadySent')}
          </Text>
          <DayPhotoGallery photos={sentRows} onOpen={(photo) => setViewerId(photo.id)} />
        </View>
      ) : null}
      <Card style={styles.photos}>
        <View style={styles.heading}>
          <Text variant="subtitle" accessibilityRole="header">
            {t('photoRequest.once.yourPhotos')}
          </Text>
          {rows.length > 0 ? (
            <Text variant="caption" color="muted">
              {t('photoRequest.once.added', { count: rows.length })}
            </Text>
          ) : null}
        </View>
        {rows.length === 0 ? (
          <Text variant="body" color="muted">
            {t('photoRequest.once.empty', { name })}
          </Text>
        ) : (
          <View>
            {rows.map((row, index) => (
              <View key={row.key} style={index > 0 ? styles.divider : undefined}>
                <PhotoNoteRow
                  uri={row.photo.uri}
                  position={index + 1}
                  name={name}
                  note={row.note}
                  onChangeNote={(note) => patchRow(row.key, { note })}
                  onRemove={row.status === 'done' ? undefined : () => setRows((list) => list.filter((item) => item.key !== row.key))}
                  status={row.status}
                  error={row.failure ? t(`photoRequest.once.errors.${row.failure}`) : undefined}
                  editable={!sending}
                />
              </View>
            ))}
          </View>
        )}
        {/* With nothing picked and nothing sent the footer adds the first photos; otherwise the card does. */}
        {full || !footerSends ? null : (
          <Button
            label={t('photoRequest.once.addMore')}
            onPress={askSource}
            disabled={busy}
            size="small"
            style={styles.addMore}
            icon={addIcon}
          />
        )}
      </Card>
    </FlowFrame>
    <PhotoViewer
      photos={sentRows}
      selectedId={viewerId}
      canDelete={isOwnPhoto}
      deleting={remove.isPending}
      onSelect={setViewerId}
      onClose={() => setViewerId(null)}
      onDelete={deleteViewed}
    />
    </>
  );
}

const makeStyles = (theme: Theme) =>
  StyleSheet.create({
    content: {
      gap: theme.spacing.xl,
    },
    note: {
      gap: theme.spacing.xxs,
      paddingVertical: theme.spacing.md,
      paddingHorizontal: theme.spacing.roomy,
    },
    photos: {
      gap: theme.spacing.md,
      paddingVertical: theme.spacing.md,
      paddingHorizontal: theme.spacing.roomy,
    },
    heading: {
      flexDirection: 'row',
      alignItems: 'baseline',
      justifyContent: 'space-between',
    },
    sent: {
      gap: theme.spacing.md,
    },
    divider: {
      borderTopWidth: theme.sizes.border,
      borderTopColor: theme.colors.line,
    },
    addMore: {
      alignSelf: 'center',
    },
    centered: {
      textAlign: 'center',
    },
  });
