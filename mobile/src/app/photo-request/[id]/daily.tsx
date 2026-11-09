import { StyleSheet } from 'react-native';
import { useTranslation } from 'react-i18next';

import { Button } from '@/components/Button';
import { Text } from '@/components/Text';
import { useLeaveRequest } from '@/components/photoRequest/hooks';
import { FlowFrame } from '@/components/questionnaire/FlowFrame';
import { useTheme } from '@/theme';
import type { Theme } from '@/theme';

/** Placeholder: the every-day photo screen replaces this one. */
export default function PhotoRequestDailyScreen() {
  const { t } = useTranslation();
  const theme = useTheme();
  const styles = makeStyles(theme);
  const leave = useLeaveRequest();

  return (
    <FlowFrame
      header={{ onClose: leave }}
      contentStyle={styles.content}
      footer={<Button label={t('photoRequest.actions.backToToday')} onPress={leave} />}
    >
      <Text variant="body" color="muted" style={styles.centered}>
        {t('photoRequest.placeholder')}
      </Text>
    </FlowFrame>
  );
}

const makeStyles = (theme: Theme) =>
  StyleSheet.create({
    content: {
      flexGrow: 1,
      alignItems: 'center',
      justifyContent: 'center',
      paddingHorizontal: theme.spacing.xxl,
    },
    centered: {
      textAlign: 'center',
    },
  });
