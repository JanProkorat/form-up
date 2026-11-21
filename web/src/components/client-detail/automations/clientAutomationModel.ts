import type { TFunction } from 'i18next';
import {
  AutomationRuleSource,
  AutomationRunStatus,
  type AutomationRuleDto,
  type ClientAutomationItem,
  type ClientAutomationLastResult,
} from '@/api/automations';
import { formatNextSend, subtitleFor, weekdayEvery, whenLabel } from '@/components/automations/automationFormat';
import { clientItemToRule } from '@/components/automations/automationRuleForm';

const SEPARATOR = ' · ';
const RESULT = 'clientDetail.automations.result';

/** The "Last time" cell: the most recent run, or "Not sent yet". Dates carry no weekday. */
export function lastResultLabel(
  result: ClientAutomationLastResult | undefined,
  t: TFunction,
  language: string,
): string {
  if (!result?.status) {
    return t(`${RESULT}.notSent`);
  }
  const date = result.sentAt ? formatNextSend(result.sentAt, language) : '';
  switch (result.status) {
    case AutomationRunStatus.Sent:
      return result.answeredAt
        ? t(`${RESULT}.sentAnswered`, { date, answered: formatNextSend(result.answeredAt, language) })
        : t(`${RESULT}.sentWaiting`, { date });
    case AutomationRunStatus.Skipped: {
      const base = date ? t(`${RESULT}.skippedOn`, { date }) : t(`${RESULT}.skipped`);
      return result.skipReason ? `${base}${SEPARATOR}${t(`${RESULT}.skipReason.${result.skipReason}`)}` : base;
    }
    default:
      return date ? t(`${RESULT}.failedOn`, { date }) : t(`${RESULT}.failed`);
  }
}

/** "Paused until 1 Nov · knee injury"; the date and the note are both optional. */
export function pauseSubtitle(item: ClientAutomationItem, t: TFunction, language: string): string {
  const prefix = 'clientDetail.automations.row';
  const head = item.pausedUntil
    ? t(`${prefix}.pausedUntil`, { date: formatNextSend(item.pausedUntil, language) })
    : t(`${prefix}.paused`);
  const note = item.pauseNote?.trim();
  return note ? `${head}${SEPARATOR}${note}` : head;
}

/** The second line of a rule row. */
export function rowSubtitle(
  item: ClientAutomationItem,
  parent: AutomationRuleDto | undefined,
  formTitle: string | undefined,
  t: TFunction,
  language: string,
): string {
  if (item.source === AutomationRuleSource.Paused) {
    return pauseSubtitle(item, t, language);
  }
  // A check-in client may have their own form; the row names the one they actually get.
  const effectiveFormTitle = item.checkInQuestionnaireName ?? formTitle;
  if (item.source === AutomationRuleSource.Custom && parent) {
    if (parent.scheduleWeekday != null) {
      const day = weekdayEvery(parent.scheduleWeekday, t);
      return effectiveFormTitle
        ? t('clientDetail.automations.row.changedFromWeekdayForm', { day, form: effectiveFormTitle })
        : t('clientDetail.automations.row.changedFromWeekday', { day });
    }
    return t('clientDetail.automations.row.changedFrom', { when: whenLabel(parent, t) });
  }
  return subtitleFor(clientItemToRule(item), effectiveFormTitle, t);
}
