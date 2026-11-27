import { useEffect, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Feedback, KeyboardSensor, PointerSensor } from '@dnd-kit/dom';
import { DragDropProvider, useDroppable } from '@dnd-kit/react';
import { BookOpen, CalendarDays, Info } from 'lucide-react';
import { QuestionnaireType, type QuestionnaireDto } from '@/api/questionnaires';
import UnsavedChangesDialog from '@/components/layout/UnsavedChangesDialog';
import EditorSidePanel, { type EditorSideTabDef } from '@/components/editor/EditorSidePanel';
import CheckInScheduleLine from '@/components/forms/builder/CheckInScheduleLine';
import FormBuilderHeader from '@/components/forms/builder/FormBuilderHeader';
import FormPreviewDialog from '@/components/forms/builder/FormPreviewDialog';
import FormInfoPanel from '@/components/forms/builder/FormInfoPanel';
import QuestionCard from '@/components/forms/builder/QuestionCard';
import QuestionPalette from '@/components/forms/builder/QuestionPalette';
import SectionCard from '@/components/forms/builder/SectionCard';
import {
  arrayMove,
  CANVAS_ID,
  dragActivationConstraints,
  PALETTE_DRAG,
  QUESTION_DRAG,
  readOptionDrag,
  readPaletteDrag,
  readQuestionDrag,
} from '@/components/forms/builder/form-builder-drag';
import {
  answerableCount,
  createQuestion,
  duplicateQuestion,
  estimateMinutes,
  isSectionKind,
  questionNumbers,
  toUpdatePayload,
  validateDraft,
  type DraftForm,
  type DraftQuestion,
  type PaletteKind,
} from '@/components/forms/builder/form-builder-model';
import { useFormBuilderState } from '@/components/forms/builder/useFormBuilderState';
import { useUnsavedChangesBlocker } from '@/hooks/useUnsavedChangesBlocker';
import { useUpdateQuestionnaire } from '@/hooks/useQuestionnaireQueries';
import { showApiError, showError } from '@/lib/api-errors';

type SideTab = 'info' | 'library';

const DRAG_SENSORS = [PointerSensor.configure({ activationConstraints: dragActivationConstraints }), KeyboardSensor];

interface CanvasProps {
  children: React.ReactNode;
  empty: boolean;
}

/** The droppable question list; also catches a drop below the last card or into an empty form. */
function FormCanvas({ children, empty }: CanvasProps) {
  const { t } = useTranslation();
  const { ref, isDropTarget } = useDroppable({ id: CANVAS_ID, accept: [QUESTION_DRAG, PALETTE_DRAG] });
  return (
    <div ref={ref} data-testid="form-canvas" className="flex min-h-64 flex-1 flex-col gap-4 pb-6">
      {empty ? (
        <div
          className={
            isDropTarget
              ? 'flex flex-1 flex-col items-center justify-center gap-1 rounded-card border-2 border-dashed border-ink p-8 text-center'
              : 'flex flex-1 flex-col items-center justify-center gap-1 rounded-card border-2 border-dashed border-line p-8 text-center'
          }
        >
          <p className="text-copy font-bold text-ink">{t('forms.builder.empty.title')}</p>
          <p className="text-body text-muted-foreground">{t('forms.builder.empty.hint')}</p>
        </div>
      ) : (
        <ol className="flex flex-col gap-4">{children}</ol>
      )}
    </div>
  );
}

interface Props {
  initial: QuestionnaireDto;
  onBack: () => void;
}

/** The form builder for one loaded form: question palette, sortable question cards, header and dialogs. */
export default function FormBuilder({ initial, onBack }: Props) {
  const { t } = useTranslation();
  const { doc, server, dirty, edit, setLocked, markSaved } = useFormBuilderState(initial);
  const updateMutation = useUpdateQuestionnaire(initial.publicId);
  const [selectedKey, setSelectedKey] = useState<string | null>(() => doc.questions[0]?.key ?? null);
  const [showErrors, setShowErrors] = useState(false);
  const [panelOpen, setPanelOpen] = useState(true);
  const [sideTab, setSideTab] = useState<SideTab>('library');
  const [previewOpen, setPreviewOpen] = useState(false);
  // Any in-app navigation away (back button, sidebar, in-page link, browser back) asks first while dirty.
  const { blocker: leaveBlocker } = useUnsavedChangesBlocker(dirty);
  const [scrollToKey, setScrollToKey] = useState<string | null>(null);

  const errors = useMemo(() => validateDraft(doc), [doc]);
  const visibleErrors = showErrors ? errors : null;
  // `total` counts every row (sections too) for positions; `count` counts only what a client answers.
  const total = doc.questions.length;
  const count = answerableCount(doc.questions);
  const numbers = questionNumbers(doc.questions);
  const isOnboarding = doc.type === QuestionnaireType.Onboarding;

  useEffect(() => {
    if (!dirty) {
      return;
    }
    const warn = (event: BeforeUnloadEvent) => event.preventDefault();
    window.addEventListener('beforeunload', warn);
    return () => window.removeEventListener('beforeunload', warn);
  }, [dirty]);

  useEffect(() => {
    if (scrollToKey === null) {
      return;
    }
    document.querySelector(`[data-key="${scrollToKey}"]`)?.scrollIntoView({ block: 'nearest' });
    setScrollToKey(null);
  }, [scrollToKey, doc.questions]);

  function patchQuestion(key: string, patch: Partial<DraftQuestion>) {
    edit((current) => ({
      ...current,
      questions: current.questions.map((question) => (question.key === key ? { ...question, ...patch } : question)),
    }));
  }

  function insertQuestion(question: DraftQuestion, at: number) {
    edit((current) => ({
      ...current,
      questions: [...current.questions.slice(0, at), question, ...current.questions.slice(at)],
    }));
    setSelectedKey(question.key);
    setScrollToKey(question.key);
  }

  function addPalette(kind: PaletteKind, at = total) {
    insertQuestion(
      createQuestion(kind, {
        label: t('forms.builder.defaults.question'),
        sectionLabel: t('forms.builder.defaults.section'),
        option: (number) => t('forms.builder.defaults.option', { number }),
        lowLabel: t('forms.builder.defaults.low'),
        highLabel: t('forms.builder.defaults.high'),
      }),
      at,
    );
  }

  function duplicate(index: number) {
    const source = doc.questions[index];
    if (source) {
      insertQuestion(duplicateQuestion(source), index + 1);
    }
  }

  function remove(key: string) {
    edit((current) => ({ ...current, questions: current.questions.filter((question) => question.key !== key) }));
    setSelectedKey((current) => (current === key ? null : current));
  }

  function patchForm(patch: Partial<Pick<DraftForm, 'title' | 'description' | 'type' | 'isDefault'>>) {
    // The default flag is Onboarding-only, so switching to Check-in clears it. Questions stay as they are.
    edit((current) => ({
      ...current,
      ...patch,
      ...(patch.type === QuestionnaireType.CheckIn ? { isDefault: false } : {}),
    }));
  }

  async function save() {
    if (errors.count > 0) {
      setShowErrors(true);
      showError('forms.builder.fixErrors');
      return;
    }
    const sent = doc;
    setLocked(true);
    try {
      const saved = await updateMutation.mutateAsync(toUpdatePayload(sent, server));
      markSaved(saved, sent);
      setShowErrors(false);
    } catch (error) {
      showApiError(error, 'forms.builder.saveError');
    } finally {
      setLocked(false);
    }
  }

  const questionList = (
    <FormCanvas empty={total === 0}>
      {doc.questions.map((question, index) =>
        isSectionKind(question.type) ? (
          <SectionCard
            key={question.key}
            question={question}
            index={index}
            position={doc.questions.slice(0, index + 1).filter((row) => isSectionKind(row.type)).length}
            selected={selectedKey === question.key}
            errors={visibleErrors?.questions[question.key]}
            onSelect={() => setSelectedKey(question.key)}
            onChange={(patch) => patchQuestion(question.key, patch)}
            onDuplicate={() => duplicate(index)}
            onDelete={() => remove(question.key)}
          />
        ) : (
          <QuestionCard
            key={question.key}
            question={question}
            index={index}
            number={numbers[index] ?? index + 1}
            selected={selectedKey === question.key}
            errors={visibleErrors?.questions[question.key]}
            onSelect={() => setSelectedKey(question.key)}
            onChange={(patch) => patchQuestion(question.key, patch)}
            onDuplicate={() => duplicate(index)}
            onDelete={() => remove(question.key)}
          />
        ),
      )}
    </FormCanvas>
  );

  const sideTabs: EditorSideTabDef<SideTab>[] = [
    {
      id: 'info',
      label: t('forms.builder.panel.info'),
      icon: Info,
      content: (
        <FormInfoPanel
          doc={doc}
          titleError={visibleErrors?.title}
          descriptionError={visibleErrors?.description}
          onChange={patchForm}
        />
      ),
    },
    {
      id: 'library',
      label: t('forms.builder.panel.library'),
      icon: BookOpen,
      content: <QuestionPalette formType={doc.type} onAdd={(kind) => addPalette(kind)} />,
    },
  ];

  return (
    <DragDropProvider
      sensors={DRAG_SENSORS}
      plugins={(defaults) =>
        defaults.map((plugin) => (plugin === Feedback ? Feedback.configure({ dropAnimation: null }) : plugin))
      }
      onDragEnd={(event) => {
        if (event.canceled) {
          return;
        }
        const { source, target } = event.operation;
        const targetId = target?.id;
        const palette = readPaletteDrag(source?.data);
        if (palette) {
          const at = targetId === CANVAS_ID ? total : doc.questions.findIndex((question) => question.key === targetId);
          if (targetId !== undefined && at !== -1) {
            addPalette(palette.paletteKind, at);
          }
          return;
        }
        const question = readQuestionDrag(source?.data);
        if (question) {
          const from = doc.questions.findIndex((candidate) => candidate.key === question.key);
          const to = targetId === CANVAS_ID ? total - 1 : doc.questions.findIndex((candidate) => candidate.key === targetId);
          if (from !== -1 && to !== -1 && from !== to) {
            edit((current) => ({ ...current, questions: arrayMove(current.questions, from, to) }));
          }
          return;
        }
        const option = readOptionDrag(source?.data);
        const targetOption = readOptionDrag(target?.data);
        if (option && targetOption && option.questionKey === targetOption.questionKey && option.index !== targetOption.index) {
          patchQuestion(option.questionKey, {
            options: arrayMove(
              doc.questions.find((candidate) => candidate.key === option.questionKey)?.options ?? [],
              option.index,
              targetOption.index,
            ),
          });
        }
      }}
    >
      <div className="flex h-full min-h-0">
        <EditorSidePanel
          tabs={sideTabs}
          tab={sideTab}
          onTabChange={setSideTab}
          open={panelOpen}
          onOpenChange={setPanelOpen}
          labels={{
            tabs: t('forms.builder.panel.tabs'),
            collapse: t('forms.builder.panel.collapse'),
            expand: t('forms.builder.panel.expand'),
            openTab: (name) => t('forms.builder.panel.open', { name }),
          }}
        />

        <div className="flex min-h-0 min-w-0 flex-1 flex-col">
          <FormBuilderHeader
            title={doc.title}
            onTitleChange={(title) => patchForm({ title })}
            titleInvalid={visibleErrors?.title !== undefined}
            type={doc.type}
            isDefault={doc.isDefault}
            dirty={dirty}
            saving={updateMutation.isPending}
            onBack={onBack}
            onPreview={() => setPreviewOpen(true)}
            onSave={() => void save()}
          />

          <div data-testid="form-builder-scroll" className="flex min-h-0 flex-1 flex-col gap-4 overflow-y-auto px-6 py-4">
            <p className="flex flex-wrap items-center gap-x-1.5 gap-y-1 text-body text-muted-foreground">
              {isOnboarding ? (
                <Info className="size-4 shrink-0" aria-hidden="true" />
              ) : (
                <CalendarDays className="size-4 shrink-0" aria-hidden="true" />
              )}
              {isOnboarding ? (
                <span>{t(doc.isDefault ? 'forms.builder.info.onboarding' : 'forms.builder.info.anyTime')}</span>
              ) : (
                <CheckInScheduleLine formId={initial.publicId} />
              )}
              <span aria-hidden="true">·</span>
              <strong data-testid="question-count" className="font-bold text-ink">
                {t('forms.builder.info.questions', { count })}
              </strong>
              {isOnboarding && (
                <>
                  <span aria-hidden="true">·</span>
                  <span>{t('forms.builder.info.minutes', { count: estimateMinutes(count) })}</span>
                </>
              )}
            </p>
            {visibleErrors?.noQuestions && (
              <p role="alert" data-testid="form-only-sections" className="text-body text-destructive">
                {t(visibleErrors.noQuestions)}
              </p>
            )}
            {questionList}
          </div>
        </div>
      </div>

      <FormPreviewDialog open={previewOpen} onOpenChange={setPreviewOpen} doc={doc} />

      <UnsavedChangesDialog blocker={leaveBlocker} i18nPrefix="forms.builder.leave" />
    </DragDropProvider>
  );
}
