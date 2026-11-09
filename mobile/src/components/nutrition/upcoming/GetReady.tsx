import { Fragment } from 'react';
import { Pressable, StyleSheet, View } from 'react-native';
import { useRouter } from 'expo-router';
import { SymbolView } from 'expo-symbols';
import type { SFSymbol } from 'expo-symbols';
import { useTranslation } from 'react-i18next';

import type { Collaboration } from '@/api/collaborations';
import { Text } from '@/components/Text';
import { firstName } from '@/components/invites/format';
import { usePhotoStatus } from '@/components/photoRequest/hooks';
import { firstPaymentStep, paymentPhase, photoStep } from '@/components/today/coachSteps';
import type { Step } from '@/components/today/coachSteps';
import { TimelineStep } from '@/components/today/TimelineStep';
import { useClientRecipes } from '@/hooks/useClientRecipes';
import { usePaymentsEnabled } from '@/hooks/usePaymentsEnabled';
import { useTheme } from '@/theme';
import type { Theme } from '@/theme';

type GetReadyProps = {
  planId: string | undefined;
  collaboration: Collaboration | undefined;
  /** True when the client has exactly one covering collaboration, so an unmatched photo request is theirs. */
  onlyCollaboration: boolean;
};

type RowProps = { symbol: SFSymbol; title: string; hint?: string; onPress: () => void };

function PrepareRow({ symbol, title, hint, onPress }: RowProps) {
  const theme = useTheme();
  const styles = makeStyles(theme);
  return (
    <Pressable accessibilityRole="link" accessibilityLabel={title} onPress={onPress} style={styles.row}>
      <View style={styles.rowIcon}>
        <SymbolView name={symbol} tintColor={theme.colors.ink} size={theme.sizes.rowGlyph} />
      </View>
      <View style={styles.rowCopy}>
        <Text variant="link">{title}</Text>
        {hint ? (
          <Text variant="caption" color="muted">
            {hint}
          </Text>
        ) : null}
      </View>
      <SymbolView name="chevron.right" tintColor={theme.colors.muted} size={theme.sizes.coachChevron} />
    </Pressable>
  );
}

/** What the client can do before the plan starts: open setup steps, the shopping list and the plan's recipes. */
export function GetReady({ planId, collaboration, onlyCollaboration }: GetReadyProps) {
  const { t, i18n } = useTranslation();
  const router = useRouter();
  const theme = useTheme();
  const styles = makeStyles(theme);
  const photoStatus = usePhotoStatus(collaboration, onlyCollaboration);
  const { enabled: paymentsEnabled } = usePaymentsEnabled();
  const recipes = useClientRecipes(planId, Boolean(planId));

  const first = firstName(collaboration?.professionalName?.trim()) || t('today.progress.coachFallback');
  const steps: Step[] = [];
  const firstPayment = paymentsEnabled ? collaboration?.firstPayment : undefined;
  if (firstPayment && paymentPhase(firstPayment).due) {
    steps.push(
      firstPaymentStep({
        t,
        language: i18n.language,
        firstPayment,
        name: first,
        onShowQr: () => router.push('/payments'),
      }),
    );
  }
  if (photoStatus) {
    steps.push(photoStep(t, photoStatus, first, (route) => router.push(route)));
  }

  const recipeCount = recipes.data?.total;

  return (
    <View style={styles.section}>
      <Text variant="overline" color="muted" style={styles.overline}>
        {t('nutritionUpcoming.getReady').toUpperCase()}
      </Text>
      <View style={styles.list}>
        {steps.length > 0 ? (
          <View style={styles.steps}>
            {steps.map((step, index) => (
              <Fragment key={step.key}>
                {index > 0 ? <View style={styles.divider} /> : null}
                <TimelineStep
                  state={step.state}
                  title={step.title}
                  hint={step.hint}
                  actionLabel={step.actionLabel}
                  onAction={step.onAction}
                  actionTone={step.actionTone}
                  connector={null}
                />
              </Fragment>
            ))}
          </View>
        ) : null}
        <PrepareRow
          symbol="cart"
          title={t('nutritionUpcoming.shopping.title')}
          hint={t('nutritionUpcoming.shopping.hint')}
          onPress={() => router.push('/nutrition/shopping-list')}
        />
        <PrepareRow
          symbol="book"
          title={t('nutritionUpcoming.recipes.title')}
          hint={recipeCount === undefined ? undefined : t('nutritionUpcoming.recipes.hint', { count: recipeCount })}
          onPress={() => router.push('/nutrition/recipes')}
        />
      </View>
    </View>
  );
}

const makeStyles = (theme: Theme) =>
  StyleSheet.create({
    section: {
      gap: theme.spacing.md,
      paddingHorizontal: theme.spacing.xxs,
    },
    overline: {
      letterSpacing: theme.typography.overline.letterSpacing,
    },
    list: {
      gap: theme.spacing.sm,
    },
    steps: {
      gap: theme.spacing.sm,
      paddingVertical: theme.spacing.md,
      paddingHorizontal: theme.spacing.lg,
      borderRadius: theme.radii.xl,
      borderWidth: theme.sizes.border,
      borderColor: theme.colors.line,
      backgroundColor: theme.colors.surface,
      boxShadow: theme.shadows.card,
    },
    divider: {
      height: theme.sizes.border,
      backgroundColor: theme.colors.lineStrong,
    },
    row: {
      flexDirection: 'row',
      alignItems: 'center',
      gap: theme.spacing.md,
      paddingVertical: theme.spacing.md,
      paddingHorizontal: theme.spacing.lg,
      borderRadius: theme.radii.xl,
      backgroundColor: theme.colors.subtle,
    },
    rowIcon: {
      width: theme.sizes.prepareIcon,
      height: theme.sizes.prepareIcon,
      borderRadius: theme.radii.pill,
      alignItems: 'center',
      justifyContent: 'center',
      backgroundColor: theme.colors.surface,
    },
    rowCopy: {
      flex: 1,
      minWidth: 0,
      gap: theme.spacing.xxs / 2,
    },
  });
