import { Fragment, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Angry, Frown, Laugh, Meh, Smile, Star, type LucideIcon } from 'lucide-react';
import type { ClientFormAnswer } from '@/api/questionnaires';
import { parseChoices } from '@/components/client-forms/answerValues';
import { cn } from '@/lib/utils';

const CHIP_CLASS =
  'inline-flex h-7 items-center rounded-full border border-border bg-card px-3 text-body font-semibold text-ink';

const DEFAULT_SCALE_MAX = 10;
const RATING_MAX = 5;
const SMILEY_FACES: Record<number, LucideIcon> = { 1: Angry, 2: Frown, 3: Meh, 4: Smile, 5: Laugh };

function AnswerValue({ answer }: { answer: ClientFormAnswer }) {
  const { t } = useTranslation();
  const empty = <span className="text-muted-foreground">{t('clientForms.answers.noAnswer')}</span>;

  switch (answer.questionType) {
    case 'multi_select': {
      const choices = parseChoices(answer.valueJson);
      const other = answer.valueText?.trim();
      const items = other ? [...choices, t('clientForms.answers.other', { text: other })] : choices;
      if (items.length === 0) {
        return empty;
      }
      return (
        <ul className="flex flex-wrap gap-1.5">
          {items.map((choice, index) => (
            <li key={`${index}-${choice}`} className={CHIP_CLASS}>
              {choice}
            </li>
          ))}
        </ul>
      );
    }
    case 'scale':
      return answer.valueNumber == null ? (
        empty
      ) : (
        <span className="inline-flex items-center gap-1.5 text-copy font-semibold text-ink">
          {t('clientForms.answers.ofMax', { value: answer.valueNumber, max: answer.scaleMax ?? DEFAULT_SCALE_MAX })}
        </span>
      );
    case 'star_rating':
      return answer.valueNumber == null ? (
        empty
      ) : (
        <span className="inline-flex items-center gap-1.5 text-copy font-semibold text-ink">
          <Star className="size-4 fill-current text-training" aria-hidden="true" />
          {t('clientForms.answers.ofMax', { value: answer.valueNumber, max: RATING_MAX })}
        </span>
      );
    case 'smiley_rating': {
      if (answer.valueNumber == null) {
        return empty;
      }
      const Face = Number.isInteger(answer.valueNumber) ? (SMILEY_FACES[answer.valueNumber] ?? Smile) : Smile;
      return (
        <span className="inline-flex items-center gap-1.5 text-copy font-semibold text-ink">
          <Face className="size-4 text-training" aria-hidden="true" />
          {t('clientForms.answers.ofMax', { value: answer.valueNumber, max: RATING_MAX })}
        </span>
      );
    }
    case 'number':
      return answer.valueNumber == null ? empty : <span className="text-copy text-ink">{answer.valueNumber}</span>;
    case 'file_upload':
      return answer.fileUrl ? (
        <a
          href={answer.fileUrl}
          target="_blank"
          rel="noopener noreferrer"
          className="text-copy font-semibold text-ink underline underline-offset-2 [overflow-wrap:anywhere]"
        >
          {answer.fileUrl.split('/').pop()}
        </a>
      ) : (
        empty
      );
    default:
      return answer.valueText ? (
        <span className="text-copy text-ink [overflow-wrap:anywhere]">{answer.valueText}</span>
      ) : (
        empty
      );
  }
}

interface Props {
  answers: readonly ClientFormAnswer[];
  /** Skips the built-in section headings and side padding, for a caller that lays out sections itself. */
  bare?: boolean;
  /** Extra content beside an answer's value, e.g. a change against last week. */
  renderExtra?: (answer: ClientFormAnswer) => ReactNode;
}

/** Read-only list of one form's answers: question, then value, with section headings where the form has them. */
export default function ClientFormAnswers({ answers, bare = false, renderExtra }: Props) {
  return (
    <ul className="flex flex-col">
      {answers.map((answer, index) => {
        const previous = answers[index - 1];
        const newSection = !bare && answer.sectionLabel && answer.sectionLabel !== previous?.sectionLabel;
        return (
          <Fragment key={answer.questionPublicId ?? index}>
            {newSection && (
              <li className="px-6 pt-4 text-meta font-bold text-ink-2" role="presentation">
                {answer.sectionLabel}
              </li>
            )}
            <li className={cn('flex flex-col gap-1.5 border-b border-line py-3.5 last:border-b-0', !bare && 'px-6')}>
              <span className="text-body text-muted-foreground">{answer.questionLabel}</span>
              <span className="flex flex-wrap items-baseline gap-x-2.5 gap-y-1">
                <AnswerValue answer={answer} />
                {renderExtra?.(answer)}
              </span>
            </li>
          </Fragment>
        );
      })}
    </ul>
  );
}
