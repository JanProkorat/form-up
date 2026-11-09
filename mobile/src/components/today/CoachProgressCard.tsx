import { Pressable, StyleSheet, View } from 'react-native';
import { useRouter } from 'expo-router';
import { SymbolView } from 'expo-symbols';
import { useTranslation } from 'react-i18next';

import type { Collaboration } from '@/api/collaborations';
import { LinkCapabilityScope } from '@/api/generated';
import type { PendingItems, PendingQuestionnaire, SubmittedByCoach } from '@/api/questionnaires';
import { Chip } from '@/components/Chip';
import { usePhotoStatus } from '@/components/photoRequest/hooks';
import { firstName, initials } from '@/components/invites/format';
import { Text } from '@/components/Text';
import { useConversations } from '@/hooks/useConversations';
import { usePaymentsEnabled } from '@/hooks/usePaymentsEnabled';
import { usePendingQuestionnaires } from '@/hooks/usePendingQuestionnaires';
import { useSubmittedQuestionnaires } from '@/hooks/useSubmittedQuestionnaires';
import { useTheme } from '@/theme';
import type { Theme } from '@/theme';

import type { CoachRole } from './collaborationRoles';
import { firstPaymentStep, findConversation, paymentPhase, photoStep } from './coachSteps';
import type { Step } from './coachSteps';
import { fillRoute, questionCountHint, questionnaireStepTitle } from './pendingForms';
import { roleLook } from './roleLook';
import { TimelineStep } from './TimelineStep';
import { checkInDaysLabel, sinceLabel } from './todayFormat';

type CoachProgressCardProps = {
  collaboration: Collaboration;
  /** Every role the collaboration covers; one pill each. */
  covered: CoachRole[];
  /** The covered roles that still have no published plan; one in-progress step each. */
  unpublished: CoachRole[];
  /** True when the client has exactly one covering collaboration, so an unmatched photo request is theirs. */
  onlyCollaboration: boolean;
};

/** The role a questionnaire was sent for; null when it has no plan type (or covers both). */
const scopeRole = (scope: LinkCapabilityScope | undefined): CoachRole | null => {
  if (scope === LinkCapabilityScope.TrainingOnly) return 'Trainer';
  if (scope === LinkCapabilityScope.NutritionOnly) return 'Nutritionist';
  return null;
};

type QuestionnaireStatus = { pending: PendingQuestionnaire | undefined; sent: boolean };
const NONE: QuestionnaireStatus = { pending: undefined, sent: false };

/** The oldest questionnaire waiting, or whether one was sent, for this coach link. `role`: a role to match, null for unscoped, undefined for any. */
function questionnaireStatus(
  pending: PendingItems | undefined,
  submitted: readonly SubmittedByCoach[] | undefined,
  linkId: string | undefined,
  role: CoachRole | null | undefined,
): QuestionnaireStatus {
  if (linkId === undefined) return NONE;
  const matches = (scope: LinkCapabilityScope | undefined) => role === undefined || scopeRole(scope) === role;
  const item = (pending?.questionnaires ?? []).find(
    (entry) => entry.linkPublicId === linkId && matches(entry.scope),
  );
  const isSent =
    item === undefined &&
    (submitted ?? []).some(
      (coach) => coach.linkPublicId === linkId && (coach.responses ?? []).some((r) => matches(r.scope)),
    );
  return { pending: item, sent: isSent };
}

/** One coach who is still building a plan: who they are, what is done, what is waiting on the client. */
export function CoachProgressCard({
  collaboration,
  covered,
  unpublished,
  onlyCollaboration,
}: CoachProgressCardProps) {
  const { t, i18n } = useTranslation();
  const router = useRouter();
  const theme = useTheme();
  const styles = makeStyles(theme);
  const { data: pending } = usePendingQuestionnaires();
  const { data: submitted } = useSubmittedQuestionnaires();
  const { data: conversations } = useConversations();
  const { enabled: paymentsEnabled } = usePaymentsEnabled();

  const fullName = collaboration.professionalName?.trim() ?? '';
  const first = firstName(fullName) || t('today.progress.coachFallback');
  const single = covered.length === 1;
  const linkId = collaboration.publicId;
  // A single-role collaboration keeps one step; several roles get one step per plan type.
  const generic = questionnaireStatus(pending, submitted, linkId, single ? undefined : null);
  const perRole = new Map<CoachRole, QuestionnaireStatus>(
    single ? [] : covered.map((role) => [role, questionnaireStatus(pending, submitted, linkId, role)]),
  );
  const photoStatus = usePhotoStatus(collaboration, onlyCollaboration);
  const photoOpen = photoStatus !== null && photoStatus.kind !== 'onceSent';
  let waiting = (generic.pending ? 1 : 0) + (photoOpen ? 1 : 0);
  let questionnaireOpen = generic.pending !== undefined;
  for (const status of perRole.values()) {
    if (status.pending) {
      waiting += 1;
      questionnaireOpen = true;
    }
  }

  // First payment of the agreed terms: Upcoming only informs, Requested and Overdue ask the client to pay.
  const firstPayment = paymentsEnabled ? collaboration.firstPayment : undefined;
  const { upcoming: payUpcoming, due: payDue } = paymentPhase(firstPayment);
  if (payDue) waiting += 1;

  const openChat = () => {
    const target = findConversation(conversations, collaboration);
    if (target?.id) router.push({ pathname: '/chat/[id]', params: { id: target.id } });
    else router.push('/messages');
  };

  const steps: Step[] = [{ key: 'accepted', state: 'done', title: t('today.progress.accepted') }];
  const questionnaireStep = (key: string, status: QuestionnaireStatus, role?: CoachRole): Step | null => {
    if (status.pending) {
      const route = fillRoute(status.pending);
      const item = status.pending;
      return {
        key,
        state: 'open',
        title: role ? t(`today.progress.questionnaireFor.${role}`) : questionnaireStepTitle(t, item),
        hint: questionCountHint(t, item, 'step') ?? t('today.progress.questionnairePendingHint'),
        actionLabel: route ? t('today.questionnaire.fill') : undefined,
        onAction: route ? () => router.push(route) : undefined,
        actionTone: 'primary',
      };
    }
    if (status.sent) {
      return {
        key,
        state: 'done',
        title: t(role ? `today.progress.questionnaireSentFor.${role}` : 'today.progress.questionnaireSent'),
      };
    }
    return null;
  };
  const generalStep = questionnaireStep('questionnaire', generic);
  if (generalStep) steps.push(generalStep);
  // A role whose plan is already published has no plan step to sit above, so its questionnaire goes here.
  for (const role of covered) {
    const status = perRole.get(role);
    const step =
      status && !unpublished.includes(role) ? questionnaireStep(`questionnaire-${role}`, status, role) : null;
    if (step) steps.push(step);
  }
  // Board order: questionnaire, then the first payment, then starting photos.
  if (firstPayment && (payUpcoming || payDue)) {
    steps.push(
      firstPaymentStep({
        t,
        language: i18n.language,
        firstPayment,
        name: first,
        onShowQr: () => router.push('/payments'),
        tone: questionnaireOpen ? 'secondary' : 'primary',
      }),
    );
  }
  if (photoStatus) steps.push(photoStep(t, photoStatus, first, (route) => router.push(route)));
  for (const role of unpublished) {
    const look = roleLook(theme, role);
    const status = perRole.get(role);
    const roleStep = status ? questionnaireStep(`questionnaire-${role}`, status, role) : null;
    if (roleStep) steps.push(roleStep);
    steps.push({
      key: `plan-${role}`,
      state: 'current',
      currentColor: look.ink,
      title: single ? t('today.progress.planSingle') : t(`today.progress.plan.${role}`),
      hint: single
        ? t('today.progress.planSingleHint')
        : t(role === 'Trainer' ? 'today.findCoach.trainer.hint' : 'today.findCoach.nutritionist.hint'),
    });
  }

  const headlineKey = unpublished.length > 1 ? 'both' : (unpublished[0] ?? 'Trainer');
  const tellKey = covered.length > 1 ? 'both' : (covered[0] ?? 'Trainer');
  const since = sinceLabel(t, i18n.language, collaboration.since);
  const checkInDays = checkInDaysLabel(i18n.language, collaboration.checkIns);

  return (
    <View style={styles.card}>
      <View style={styles.header}>
        <View style={styles.avatar} accessibilityElementsHidden importantForAccessibility="no-hide-descendants">
          <Text variant="bodyStrong" style={{ color: theme.colors.onAvatar }}>
            {initials(fullName)}
          </Text>
        </View>
        <View style={styles.who}>
          <Text variant="subtitle" numberOfLines={1}>
            {fullName || first}
          </Text>
          <View style={styles.meta}>
            {covered.map((role) => (
              <Chip
                key={role}
                size="sm"
                variant={role === 'Trainer' ? 'training' : 'nutrition'}
                label={t(`invites.roles.${role}`)}
              />
            ))}
            {since ? (
              <Text variant="caption" color="muted">
                {since}
              </Text>
            ) : null}
          </View>
        </View>
        {waiting > 0 ? (
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={t('today.progress.messageLabel', { name: first })}
            onPress={openChat}
            style={styles.iconButton}
          >
            <SymbolView name="bubble.left" tintColor={theme.colors.ink} size={theme.sizes.bellGlyph} />
          </Pressable>
        ) : null}
      </View>
      <View style={styles.headline}>
        <Text variant="heading" accessibilityRole="header">
          {t(`today.progress.headline.${headlineKey}`, { name: first })}
        </Text>
        {waiting > 0 ? (
          <Text variant="label" color="muted">
            {t('today.progress.things', { count: waiting })}
          </Text>
        ) : null}
      </View>
      <View>
        {steps.map((step, index) => {
          const next = steps[index + 1];
          const connector = next ? (step.state === 'done' && next.state === 'done' ? 'done' : 'idle') : null;
          return (
            <TimelineStep
              key={step.key}
              state={step.state}
              title={step.title}
              hint={step.hint}
              currentColor={step.currentColor}
              actionLabel={step.actionLabel}
              onAction={step.onAction}
              actionTone={step.actionTone}
              connector={connector}
            />
          );
        })}
      </View>
      {checkInDays ? (
        <View style={styles.tell}>
          <Text variant="labelStrong">{t('today.progress.checkIn.title', { day: checkInDays })}</Text>
          <Text variant="caption" color="muted">
            {t('today.progress.checkIn.hint')}
          </Text>
        </View>
      ) : null}
      {covered.length > 1 ? (
        <Text variant="caption" color="muted">
          {t(unpublished.length > 1 ? 'today.progress.footnoteBoth' : 'today.progress.footnoteOne')}
        </Text>
      ) : null}
      {waiting === 0 ? (
        <>
          <View style={styles.tell}>
            <Text variant="labelStrong">{t('today.progress.tell.title', { name: first })}</Text>
            <Text variant="caption" color="muted">
              {t(`today.progress.tell.${tellKey}`)}
            </Text>
          </View>
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={t('today.progress.message', { name: first })}
            onPress={openChat}
            style={styles.message}
          >
            <SymbolView name="bubble.left" tintColor={theme.colors.onPrimary} size={theme.sizes.rowGlyph} />
            <Text variant="button" color="onPrimary">
              {t('today.progress.message', { name: first })}
            </Text>
          </Pressable>
        </>
      ) : null}
    </View>
  );
}

const makeStyles = (theme: Theme) =>
  StyleSheet.create({
    card: {
      gap: theme.spacing.xl,
      padding: theme.spacing.roomy,
      borderRadius: theme.radii.card,
      borderWidth: theme.sizes.border,
      borderColor: theme.colors.line,
      backgroundColor: theme.colors.surface,
      boxShadow: theme.shadows.card,
    },
    header: {
      flexDirection: 'row',
      alignItems: 'center',
      gap: theme.spacing.md,
    },
    avatar: {
      width: theme.sizes.progressAvatar,
      height: theme.sizes.progressAvatar,
      borderRadius: theme.radii.pill,
      alignItems: 'center',
      justifyContent: 'center',
      backgroundColor: theme.colors.avatar,
    },
    who: {
      flex: 1,
      minWidth: 0,
      gap: theme.spacing.xxs,
    },
    meta: {
      flexDirection: 'row',
      alignItems: 'center',
      flexWrap: 'wrap',
      columnGap: theme.spacing.snug,
      rowGap: theme.spacing.xxs,
    },
    iconButton: {
      width: theme.sizes.messageButton,
      height: theme.sizes.messageButton,
      borderRadius: theme.radii.pill,
      alignItems: 'center',
      justifyContent: 'center',
      borderWidth: theme.sizes.border,
      borderColor: theme.colors.lineStrong,
    },
    headline: {
      gap: theme.spacing.xxs / 2,
    },
    tell: {
      gap: theme.spacing.xxs,
      paddingVertical: theme.spacing.md,
      paddingHorizontal: theme.spacing.lg,
      borderRadius: theme.radii.lg,
      backgroundColor: theme.colors.subtle,
    },
    message: {
      height: theme.sizes.buttonMedium,
      flexDirection: 'row',
      alignItems: 'center',
      justifyContent: 'center',
      gap: theme.spacing.xs,
      borderRadius: theme.radii.pill,
      backgroundColor: theme.colors.primary,
    },
  });
