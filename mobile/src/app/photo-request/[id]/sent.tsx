import { StyleSheet, View } from 'react-native';
import { Redirect, Stack, useLocalSearchParams, useRouter } from 'expo-router';
import { SymbolView } from 'expo-symbols';
import { useTranslation } from 'react-i18next';

import { Button } from '@/components/Button';
import { Text } from '@/components/Text';
import { SentCard } from '@/components/photoRequest/SentCard';
import { coachLabel } from '@/components/photoRequest/format';
import { useLeaveRequest, usePhotoRequest, useRequestCoach, useRequestId } from '@/components/photoRequest/hooks';
import { closedRoute, readPositiveInt } from '@/components/photoRequest/model';
import { FlowFrame } from '@/components/questionnaire/FlowFrame';
import { useTheme } from '@/theme';
import type { Theme } from '@/theme';

/** Sent: confirmation after a one-time request's photos went through. */
export default function PhotoRequestSentScreen() {
  const { t, i18n } = useTranslation();
  const router = useRouter();
  const theme = useTheme();
  const styles = makeStyles(theme);
  const id = useRequestId();
  const leave = useLeaveRequest();
  const params = useLocalSearchParams<{ count?: string | string[]; at?: string | string[] }>();
  const coach = useRequestCoach(usePhotoRequest(id).data);

  const count = readPositiveInt(params.count);
  const at = readPositiveInt(params.at);
  // Opened without a send (a stale link): the request's own state explains where it stands.
  if (count === null || at === null) return <Redirect href={closedRoute(id)} />;

  const name = coachLabel(t, coach);
  const time = new Date(at).toLocaleTimeString(i18n.language, { hour: 'numeric', minute: '2-digit' });

  return (
    <FlowFrame
      header={{ onClose: leave }}
      contentStyle={styles.content}
      footer={<Button label={t('photoRequest.actions.backToToday')} onPress={leave} />}
    >
      <Stack.Screen options={{ gestureEnabled: false }} />
      <View style={styles.hero}>
        <View style={styles.seal} accessibilityElementsHidden importantForAccessibility="no-hide-descendants">
          <SymbolView name="checkmark" weight="bold" tintColor={theme.colors.onNutrition} size={theme.sizes.heroGlyph} />
        </View>
        <Text variant="display" accessibilityRole="header" style={styles.centered}>
          {t('photoRequest.sent.title', { name })}
        </Text>
        <Text variant="bodyLarge" color="muted" style={styles.centered}>
          {t('photoRequest.sent.body', { name })}
        </Text>
      </View>
      <SentCard
        title={t('photoRequest.sent.cardTitle')}
        meta={t('photoRequest.sent.cardMeta', { count, time })}
        actionLabel={t('photoRequest.sent.view')}
        onPress={() => router.push('/profile/photos')}
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
