import { ActivityIndicator, StyleSheet, View } from 'react-native';
import { useTranslation } from 'react-i18next';

import { Button } from '@/components/Button';
import { Text } from '@/components/Text';
import { FlowFrame } from '@/components/questionnaire/FlowFrame';
import { useTheme } from '@/theme';
import type { Theme } from '@/theme';

type RequestMessageProps = { onClose: () => void } & (
  | { kind: 'loading' }
  | { kind: 'retry'; body: string; onRetry: () => void }
);

/** The request screens before there is a request to show: loading, or a failure with a retry. */
export function RequestMessage(props: RequestMessageProps) {
  const { t } = useTranslation();
  const theme = useTheme();
  const styles = makeStyles(theme);

  return (
    <FlowFrame
      header={{ onClose: props.onClose }}
      contentStyle={styles.content}
      footer={
        props.kind === 'retry' ? (
          <Button label={t('photoRequest.actions.retry')} onPress={props.onRetry} />
        ) : null
      }
    >
      {props.kind === 'loading' ? (
        <View accessibilityRole="progressbar" accessibilityLabel={t('photoRequest.loading')}>
          <ActivityIndicator color={theme.colors.accent} />
        </View>
      ) : (
        <Text variant="body" color="muted" style={styles.centered}>
          {props.body}
        </Text>
      )}
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
