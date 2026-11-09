import { useState } from 'react';
import { StyleSheet, View } from 'react-native';
import { useRouter } from 'expo-router';
import { useTranslation } from 'react-i18next';

import type { Collaboration } from '@/api/collaborations';
import { usePhotoStatus } from '@/components/photoRequest/hooks';
import { statusRoute } from '@/components/photoRequest/model';
import { AddMeasurementSheet } from '@/components/measurements/AddMeasurementSheet';
import { firstName } from '@/components/invites/format';
import { Text } from '@/components/Text';
import { useMeasurementHistory } from '@/hooks/useMeasurementHistory';
import { usePayments } from '@/hooks/usePayments';
import { usePaymentsEnabled } from '@/hooks/usePaymentsEnabled';
import { usePendingQuestionnaires } from '@/hooks/usePendingQuestionnaires';
import { usePhotos } from '@/hooks/usePhotos';
import { useTheme } from '@/theme';
import type { Theme } from '@/theme';

import { firstPaymentStep } from './coachSteps';
import type { CoveredCollaboration } from './collaborationRoles';
import { buildGetReadyRows } from './getReady';
import type { GetReadyRow as RowModel } from './getReady';
import { GetReadyRow } from './GetReadyRow';
import { hasUnstartedArea } from './todayBlocks';
import type { AreaBlock } from './todayBlocks';

type GetReadyListProps = {
  blocks: readonly AreaBlock[];
  covering: readonly CoveredCollaboration[];
};

type PhotosRowProps = {
  done: boolean;
  collaboration: Collaboration;
  onlyCollaboration: boolean;
  name: string;
};

/** The starting-photos row: opens the coach's open photo request, or the photo library when there is none. */
function PhotosRow({ done, collaboration, onlyCollaboration, name }: PhotosRowProps) {
  const { t } = useTranslation();
  const router = useRouter();
  const status = usePhotoStatus(collaboration, onlyCollaboration);
  const route = status ? statusRoute(status) : null;
  return (
    <GetReadyRow
      done={done}
      title={t('today.getReady.photos')}
      hint={t('today.getReady.photosHint', { name })}
      action={{
        label: t('today.getReady.addPhotos'),
        onPress: () => (route ? router.push(route) : router.push('/profile/photos')),
        tone: 'primary',
        symbol: 'photo.badge.plus',
      }}
    />
  );
}

/** What the client can still do before a plan starts; only shown while an area has not started. */
export function GetReadyList({ blocks, covering }: GetReadyListProps) {
  const { t, i18n } = useTranslation();
  const router = useRouter();
  const theme = useTheme();
  const styles = makeStyles(theme);
  const [sheetOpen, setSheetOpen] = useState(false);
  const pending = usePendingQuestionnaires();
  const history = useMeasurementHistory();
  const photos = usePhotos();
  const { enabled: paymentsEnabled } = usePaymentsEnabled();
  const payments = usePayments(paymentsEnabled);

  if (!hasUnstartedArea(blocks)) return null;

  const rows = buildGetReadyRows({
    blocks,
    covering,
    paymentsEnabled,
    payments: payments.data,
    pending: pending.data,
    weights: history.data,
    photoMonths: photos.data,
  });
  // Rows that read history or photos wait for them, so a done row does not flash open first.
  const loading =
    pending.isPending ||
    (rows.some((row) => row.kind === 'weight') && history.isPending) ||
    (rows.some((row) => row.kind === 'photos') && photos.isPending);
  if (rows.length === 0 || loading) return null;

  const doneCount = rows.filter((row) => row.done).length;
  const coachName = (row: Extract<RowModel, { collaboration: unknown }>): string =>
    firstName(row.collaboration.professionalName?.trim()) || t('today.progress.coachFallback');

  const renderRow = (row: RowModel) => {
    switch (row.kind) {
      case 'payment': {
        const firstPayment = row.collaboration.firstPayment;
        const step = firstPayment
          ? firstPaymentStep({
              t,
              language: i18n.language,
              firstPayment,
              name: coachName(row),
              onShowQr: () => router.push('/payments'),
            })
          : undefined;
        return (
          <GetReadyRow
            key={row.key}
            done={row.done}
            title={t('today.getReady.payment')}
            hint={step?.hint}
            hintOverdue={row.overdue}
            action={
              row.due && step?.onAction
                ? { label: t('today.progress.firstPayment.showQr'), onPress: step.onAction, tone: 'primary' }
                : undefined
            }
          />
        );
      }
      case 'weight':
        return (
          <GetReadyRow
            key={row.key}
            done={row.done}
            title={t('today.getReady.weight')}
            hint={t('today.getReady.weightHint')}
            action={{ label: t('today.getReady.logWeight'), onPress: () => setSheetOpen(true), tone: 'primary' }}
          />
        );
      case 'photos':
        return (
          <PhotosRow
            key={row.key}
            done={row.done}
            collaboration={row.collaboration}
            onlyCollaboration={covering.length === 1}
            name={coachName(row)}
          />
        );
    }
  };

  return (
    <View style={styles.section}>
      <View style={styles.head}>
        <Text variant="overline" color="muted" style={styles.overline} accessibilityRole="header">
          {t('today.getReady.title').toUpperCase()}
        </Text>
        <Text variant="label" color="muted">
          {t('today.getReady.progress', { done: doneCount, count: rows.length })}
        </Text>
      </View>
      <View style={styles.card}>{rows.map(renderRow)}</View>
      <AddMeasurementSheet visible={sheetOpen} entries={history.data ?? []} onClose={() => setSheetOpen(false)} />
    </View>
  );
}

const makeStyles = (theme: Theme) =>
  StyleSheet.create({
    section: {
      gap: theme.spacing.md,
      paddingHorizontal: theme.spacing.xxs,
    },
    head: {
      flexDirection: 'row',
      alignItems: 'center',
      justifyContent: 'space-between',
    },
    overline: {
      letterSpacing: theme.typography.overline.letterSpacing,
    },
    card: {
      gap: theme.spacing.lg,
      paddingVertical: theme.spacing.lg,
      paddingHorizontal: theme.spacing.lg,
      borderRadius: theme.radii.xl,
      borderWidth: theme.sizes.border,
      borderColor: theme.colors.line,
      backgroundColor: theme.colors.surface,
      boxShadow: theme.shadows.card,
    },
  });
