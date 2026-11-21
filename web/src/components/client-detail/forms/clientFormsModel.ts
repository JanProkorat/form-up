import {
  FormResponseState,
  QuestionnaireResponseKind,
  type ClientFormAnswer,
  type ClientFormItem,
} from '@/api/questionnaires';
import { isRatingAnswer } from '@/components/client-forms/answerValues';

export type FormsFilter = 'all' | 'onboarding' | 'questionnaire' | 'checkin';

export const FORMS_FILTERS: readonly FormsFilter[] = ['all', 'onboarding', 'questionnaire', 'checkin'];

/** How many check-in weeks the strip shows. */
export const STRIP_WEEKS = 8;

const FILTER_KIND: Record<Exclude<FormsFilter, 'all'>, QuestionnaireResponseKind> = {
  onboarding: QuestionnaireResponseKind.Onboarding,
  questionnaire: QuestionnaireResponseKind.Questionnaire,
  checkin: QuestionnaireResponseKind.CheckIn,
};

const DAY_MS = 24 * 60 * 60 * 1000;

export function matchesFilter(item: ClientFormItem, filter: FormsFilter): boolean {
  return filter === 'all' || item.kind === FILTER_KIND[filter];
}

export function isCheckIn(item: ClientFormItem): boolean {
  return item.kind === QuestionnaireResponseKind.CheckIn;
}

/** Only answered forms open; waiting and missed ones are greyed and not clickable. */
export function isAnswered(item: ClientFormItem): boolean {
  return item.state === FormResponseState.Answered;
}

/** Onboarding first, everything else in server order. */
export function pinOnboarding(items: readonly ClientFormItem[]): ClientFormItem[] {
  const onboarding = items.filter((item) => item.kind === QuestionnaireResponseKind.Onboarding);
  const rest = items.filter((item) => item.kind !== QuestionnaireResponseKind.Onboarding);
  return [...onboarding, ...rest];
}

/** Answered and waiting counts of the current collaboration, for the header line. */
export function countForms(current: readonly ClientFormItem[]): { answered: number; waiting: number } {
  return {
    answered: current.filter(isAnswered).length,
    waiting: current.filter((item) => item.state === FormResponseState.Waiting).length,
  };
}

/** True when any current form is submitted and not yet opened (or reviewed, for a check-in). */
export function hasNewForms(current: readonly ClientFormItem[] | undefined): boolean {
  return (current ?? []).some((item) => item.isNew === true);
}

/** The item to show when nothing is picked: the first answered one in list order. */
export function defaultSelection(items: readonly ClientFormItem[]): ClientFormItem | undefined {
  return items.find(isAnswered);
}

export interface AnswerSection {
  /** Stable anchor id fragment. */
  key: string;
  /** Section heading; empty for answers that belong to no section. */
  label: string;
  answers: ClientFormAnswer[];
}

/** Groups answers into their sections, in section order; answers without a section stay together first. */
export function groupAnswersBySection(answers: readonly ClientFormAnswer[]): AnswerSection[] {
  const sections: { order: number; section: AnswerSection }[] = [];
  const byLabel = new Map<string, AnswerSection>();
  const unlabelled: AnswerSection = { key: 'section-none', label: '', answers: [] };

  answers.forEach((answer, index) => {
    const label = answer.sectionLabel?.trim();
    if (!label) {
      unlabelled.answers.push(answer);
      return;
    }
    let section = byLabel.get(label);
    if (!section) {
      section = { key: `section-${byLabel.size}`, label, answers: [] };
      byLabel.set(label, section);
      sections.push({ order: answer.sectionOrder ?? Number.MAX_SAFE_INTEGER - index, section });
    }
    section.answers.push(answer);
  });

  const ordered = sections.sort((a, b) => a.order - b.order).map((entry) => entry.section);
  return unlabelled.answers.length > 0 ? [unlabelled, ...ordered] : ordered;
}

/** The Monday of a check-in's week as a UTC day number, or null when missing. */
function weekDay(item: ClientFormItem): number | null {
  const source = item.checkInWeekStart;
  if (!source) {
    return null;
  }
  // Only the calendar date counts, so a local or UTC midnight serialisation reads the same.
  const [year, month, day] = source.slice(0, 10).split('-').map(Number);
  if (!year || !month || !day) {
    return null;
  }
  return Math.floor(Date.UTC(year, month - 1, day) / DAY_MS);
}

/** ISO week number of a check-in's week, or null when the week is unknown. */
export function isoWeekNumber(item: ClientFormItem): number | null {
  const day = weekDay(item);
  if (day === null) {
    return null;
  }
  const date = new Date(day * DAY_MS);
  // Move to the Thursday of this ISO week; its year owns the week number.
  const thursday = new Date(Date.UTC(date.getUTCFullYear(), date.getUTCMonth(), date.getUTCDate() + 3 - ((date.getUTCDay() + 6) % 7)));
  const yearStart = Date.UTC(thursday.getUTCFullYear(), 0, 1);
  return Math.floor((thursday.getTime() - yearStart) / DAY_MS / 7) + 1;
}

function checkIns(items: readonly ClientFormItem[]): ClientFormItem[] {
  return items
    .filter((item) => item.kind === QuestionnaireResponseKind.CheckIn && weekDay(item) !== null)
    .sort((a, b) => (weekDay(a) ?? 0) - (weekDay(b) ?? 0));
}

/** The latest check-in weeks, oldest first, ending with the most recent one. */
export function weekStrip(current: readonly ClientFormItem[]): ClientFormItem[] {
  return checkIns(current).slice(-STRIP_WEEKS);
}

/** The answered check-in of the calendar week before this one, when there is one. */
export function previousWeekCheckIn(
  current: readonly ClientFormItem[],
  item: ClientFormItem,
): ClientFormItem | undefined {
  const day = weekDay(item);
  if (day === null) {
    return undefined;
  }
  return checkIns(current).find((candidate) => weekDay(candidate) === day - 7 && isAnswered(candidate));
}

/** The previous week's answer to the same question, matched by question id. */
export function previousAnswer(
  previous: ClientFormItem | undefined,
  answer: ClientFormAnswer,
): ClientFormAnswer | undefined {
  if (!previous || !answer.questionPublicId) {
    return undefined;
  }
  return (previous.answers ?? []).find((candidate) => candidate.questionPublicId === answer.questionPublicId);
}

export interface NumberChange {
  /** Signed difference, rounded to one decimal; 0 means unchanged. */
  diff: number;
}

/** Change of a number answer against the previous week; undefined when either side has no number. */
export function numberChange(answer: ClientFormAnswer, previous: ClientFormAnswer | undefined): NumberChange | undefined {
  if ((answer.questionType !== 'number' && !isRatingAnswer(answer)) || answer.valueNumber == null || previous?.valueNumber == null) {
    return undefined;
  }
  return { diff: Math.round((answer.valueNumber - previous.valueNumber) * 10) / 10 };
}
