import { StyleSheet, View } from 'react-native';
import { SymbolView } from 'expo-symbols';
import { useTranslation } from 'react-i18next';

import { PhotoDiaryStatus } from '@/api/generated';
import type { PhotoRequest } from '@/api/photoRequests';
import { getErrorStatus } from '@/api/errors';
import { Button } from '@/components/Button';
import { Card } from '@/components/Card';
import { Text } from '@/components/Text';
import { CoachHeader } from '@/components/photoRequest/CoachHeader';
import { DetailRows } from '@/components/photoRequest/DetailRows';
import type { DetailRow } from '@/components/photoRequest/DetailRows';
import { RequestMessage } from '@/components/photoRequest/RequestMessage';
import { coachLabel, formatRequestDay, modeLabel } from '@/components/photoRequest/format';
import {
  useLeaveRequest,
  usePhotoRequest,
  useRequestCoach,
  useRequestId,
} from '@/components/photoRequest/hooks';
import { hasEnded, requestEndsAt, requestMode } from '@/components/photoRequest/model';
import { FlowFrame } from '@/components/questionnaire/FlowFrame';
import { useTheme } from '@/theme';
import type { Theme } from '@/theme';

type ClosedKind = 'cancelled' | 'ended' | 'declined' | 'completed' | 'notFound';

/** Why the request is closed; one that is still open but cannot be answered is shown as not found. */
function closedKind(request: PhotoRequest | undefined): ClosedKind {
  if (!request) return 'notFound';
  switch (request.status) {
    case PhotoDiaryStatus.Cancelled:
      return 'cancelled';
    case PhotoDiaryStatus.Dismissed:
      return 'declined';
    case PhotoDiaryStatus.Completed:
      return 'completed';
    default:
      return hasEnded(request) ? 'ended' : 'notFound';
  }
}

/** A request that can no longer be answered: cancelled, ended, declined, sent already, or gone. */
export default function PhotoRequestClosedScreen() {
  const { t, i18n } = useTranslation();
  const theme = useTheme();
  const styles = makeStyles(theme);
  const id = useRequestId();
  const leave = useLeaveRequest();
  const query = usePhotoRequest(id);
  const coach = useRequestCoach(query.data);

  if (query.isPending) return <RequestMessage kind="loading" onClose={leave} />;

  const gone = query.isError && getErrorStatus(query.error) === 404;
  if (query.isError && !gone) {
    return (
      <RequestMessage kind="retry" body={t('photoRequest.errors.load')} onRetry={() => void query.refetch()} onClose={leave} />
    );
  }

  const request = query.data;
  const kind = closedKind(request);
  const name = coachLabel(t, coach);
  const language = i18n.language;
  // Cancelling and declining both stamp the update time; an every-day request ends at its own end date.
  const date = formatRequestDay(language, kind === 'ended' ? requestEndsAt(request ?? {}) ?? undefined : request?.updatedAt);

  const rows: DetailRow[] = request
    ? [
        {
          key: 'asked',
          symbol: 'calendar',
          label: t('photoRequest.rows.asked'),
          value: formatRequestDay(language, request.createdAt),
        },
        {
          key: 'howOften',
          symbol: 'arrow.triangle.2.circlepath',
          label: t('photoRequest.rows.howOften'),
          value: modeLabel(t, requestMode(request)),
        },
        {
          key: 'status',
          symbol: 'nosign',
          label: t('photoRequest.rows.status'),
          value: kind === 'notFound' ? t('photoRequest.closed.title') : t(`photoRequest.closed.status.${kind}`, { name, date }),
        },
      ]
    : [];

  return (
    <FlowFrame
      header={{ onClose: leave }}
      contentStyle={styles.content}
      footer={<Button label={t('photoRequest.actions.backToToday')} onPress={leave} />}
    >
      <View style={styles.hero}>
        <View style={styles.seal} accessibilityElementsHidden importantForAccessibility="no-hide-descendants">
          <SymbolView name="nosign" weight="medium" tintColor={theme.colors.ink2} size={theme.sizes.heroGlyph} />
        </View>
        <Text variant="display" accessibilityRole="header" style={styles.centered}>
          {t('photoRequest.closed.title')}
        </Text>
        <Text variant="bodyLarge" color="muted" style={styles.centered}>
          {t(`photoRequest.closed.reason.${kind}`, { name, date })}
        </Text>
      </View>
      {request ? (
        <Card style={styles.card}>
          <CoachHeader coach={coach} />
          <DetailRows rows={rows} />
        </Card>
      ) : null}
    </FlowFrame>
  );
}

const makeStyles = (theme: Theme) =>
  StyleSheet.create({
    content: {
      flexGrow: 1,
      justifyContent: 'center',
      gap: theme.spacing.huge,
    },
    hero: {
      alignItems: 'center',
      gap: theme.spacing.md,
    },
    seal: {
      width: theme.sizes.heroIcon + theme.spacing.huge,
      height: theme.sizes.heroIcon + theme.spacing.huge,
      borderRadius: theme.radii.pill,
      alignItems: 'center',
      justifyContent: 'center',
      backgroundColor: theme.colors.subtle,
    },
    centered: {
      textAlign: 'center',
    },
    card: {
      gap: theme.spacing.md,
    },
  });
