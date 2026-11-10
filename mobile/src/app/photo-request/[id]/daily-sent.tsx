import { StyleSheet, View } from 'react-native';
import { Redirect, Stack, useLocalSearchParams, useRouter } from 'expo-router';
import { SymbolView } from 'expo-symbols';
import { useTranslation } from 'react-i18next';

import { Button } from '@/components/Button';
import { Text } from '@/components/Text';
import { SentCard } from '@/components/photoRequest/SentCard';
import { coachLabel, formatClock } from '@/components/photoRequest/format';
import {
  useBackToToday,
  usePhotoRequest,
  useRequestCoach,
  useRequestId,
} from '@/components/photoRequest/hooks';
import { closedRoute, flowRoute, isAnswerable, readPositiveInt, requestDay } from '@/components/photoRequest/model';
import { FlowFrame } from '@/components/questionnaire/FlowFrame';
import { useTheme } from '@/theme';
import type { Theme } from '@/theme';

/** Confirmation after an every-day request's photos for today went through. */
export default function PhotoRequestDailySentScreen() {
  const { t, i18n } = useTranslation();
  const router = useRouter();
  const theme = useTheme();
  const styles = makeStyles(theme);
  const id = useRequestId();
  const backToToday = useBackToToday();
  const params = useLocalSearchParams<{ count?: string | string[]; at?: string | string[] }>();
  const query = usePhotoRequest(id);
  const coach = useRequestCoach(query.data);

  const count = readPositiveInt(params.count);
  const at = readPositiveInt(params.at);
  // Opened without a send (a stale link): the request's own state explains where it stands.
  if (count === null || at === null) return <Redirect href={closedRoute(id)} />;
  // The backend completes an ended request up to an hour late: until then the app's own clock decides.
  if (query.data && !isAnswerable(query.data)) return <Redirect href={closedRoute(id)} />;

  const name = coachLabel(t, coach);
  const { day, total } = query.data ? requestDay(query.data, at) : { day: 0, total: 0 };
  const lastDay = query.data !== undefined && day >= total;
  const time = formatClock(i18n.language, new Date(at));

  return (
    <FlowFrame
      header={{}}
      contentStyle={styles.content}
      footer={<Button label={t('photoRequest.actions.backToToday')} onPress={backToToday} />}
    >
      <Stack.Screen options={{ gestureEnabled: false }} />
      <View style={styles.hero}>
        <View style={styles.seal} accessibilityElementsHidden importantForAccessibility="no-hide-descendants">
          <SymbolView name="checkmark" weight="bold" tintColor={theme.colors.onNutrition} size={theme.sizes.heroGlyph} />
        </View>
        <Text variant="display" accessibilityRole="header" style={styles.centered}>
          {t('photoRequest.daily.sent.title')}
        </Text>
        <Text variant="bodyLarge" color="muted" style={styles.centered}>
          {t(lastDay ? 'photoRequest.daily.sent.bodyLast' : 'photoRequest.daily.sent.body', { name })}
        </Text>
      </View>
      <SentCard
        title={t('photoRequest.daily.sent.cardTitle')}
        meta={
          query.data
            ? t('photoRequest.daily.sent.cardMeta', { count, day, total, time })
            : t('photoRequest.sent.cardMeta', { count, time })
        }
        actionLabel={t('photoRequest.sent.view')}
        onPress={() => router.replace(flowRoute(id, 'everyDay'))}
      />
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
      backgroundColor: theme.colors.nutrition,
    },
    centered: {
      textAlign: 'center',
    },
  });
