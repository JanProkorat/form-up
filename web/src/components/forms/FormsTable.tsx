import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate } from 'react-router-dom';
import { FileText, MoreHorizontal } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu';
import { QuestionnaireType, type QuestionnaireSummaryDto } from '@/api/questionnaires';
import DeleteFormDialog from '@/components/forms/DeleteFormDialog';
import FormDefaultBadge from '@/components/forms/FormDefaultBadge';
import FormTypeBadge from '@/components/forms/FormTypeBadge';
import { useDuplicateForm } from '@/hooks/useQuestionnaireQueries';
import { formatRelativeTime } from '@/lib/client-metrics';
import { cn } from '@/lib/utils';

const SKELETON_ROW_COUNT = 5;
const COLUMN_COUNT = 6;
const EMPTY_VALUE = '—';

const HEAD_CLASS = 'px-4 py-3.5 text-meta font-semibold';
const CELL_CLASS = 'px-4 py-2.75';

interface Props {
  forms: QuestionnaireSummaryDto[];
  isPending: boolean;
  isError: boolean;
  onRetry: () => void;
  hasActiveFilter: boolean;
  onClearFilters: () => void;
}

function editedLabel(iso: string, language: string): string {
  const relative = formatRelativeTime(iso, language);
  return relative === '' ? EMPTY_VALUE : relative.charAt(0).toLocaleUpperCase(language) + relative.slice(1);
}

/** The Forms table: one row per form with type, question count, usage, edit date and a row menu. */
export default function FormsTable({ forms, isPending, isError, onRetry, hasActiveFilter, onClearFilters }: Props) {
  const { t, i18n } = useTranslation();
  const navigate = useNavigate();
  const duplicateMutation = useDuplicateForm();
  const [deleteTarget, setDeleteTarget] = useState<{ id: string; name: string } | null>(null);

  return (
    <>
      <Table className="min-w-200">
        <TableHeader className="sticky top-0 z-10 bg-card">
          <TableRow>
            <TableHead className={HEAD_CLASS}>{t('forms.table.columnForm')}</TableHead>
            <TableHead className={`w-56 ${HEAD_CLASS}`}>{t('forms.table.columnType')}</TableHead>
            <TableHead className={`w-32 ${HEAD_CLASS}`}>{t('forms.table.columnQuestions')}</TableHead>
            <TableHead className={`w-36 ${HEAD_CLASS}`}>{t('forms.table.columnUsedFor')}</TableHead>
            <TableHead className={`w-32 ${HEAD_CLASS}`}>{t('forms.table.columnEdited')}</TableHead>
            <TableHead className="w-12 px-4 py-3.5">
              <span className="sr-only">{t('forms.table.columnActions')}</span>
            </TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {isPending &&
            Array.from({ length: SKELETON_ROW_COUNT }).map((_, index) => (
              <TableRow key={index}>
                <TableCell colSpan={COLUMN_COUNT}>
                  <Skeleton className="h-10 w-full" />
                </TableCell>
              </TableRow>
            ))}

          {!isPending && isError && (
            <TableRow>
              <TableCell colSpan={COLUMN_COUNT} className="py-10 text-center">
                <div className="flex flex-col items-center gap-3">
                  <p className="text-body text-muted-foreground">{t('forms.loadError')}</p>
                  <Button type="button" variant="outline" size="sm" onClick={onRetry}>
                    {t('forms.retry')}
                  </Button>
                </div>
              </TableCell>
            </TableRow>
          )}

          {!isPending && !isError && forms.length === 0 && (
            <TableRow>
              <TableCell colSpan={COLUMN_COUNT} className="py-10 text-center">
                <div className="flex flex-col items-center gap-2">
                  <p className="text-body text-muted-foreground">{t('forms.noResultsForFilters')}</p>
                  {hasActiveFilter && (
                    <Button type="button" variant="outline" size="sm" onClick={onClearFilters}>
                      {t('forms.clearFilters')}
                    </Button>
                  )}
                </div>
              </TableCell>
            </TableRow>
          )}

          {!isPending &&
            !isError &&
            forms.map((form) => {
              const formId = form.publicId;
              const isOnboarding = form.type === QuestionnaireType.Onboarding;
              return (
                <TableRow
                  key={formId}
                  className="cursor-pointer border-line"
                  onClick={() => navigate(`/forms/${formId}`)}
                >
                  <TableCell className={CELL_CLASS}>
                    <div className="flex items-center gap-3">
                      <span
                        className={cn(
                          'flex size-9 shrink-0 items-center justify-center rounded-xl border border-line bg-card',
                          isOnboarding ? 'text-nutrition' : 'text-marker-text',
                        )}
                      >
                        <FileText className="size-4.5" aria-hidden="true" />
                      </span>
                      <div className="flex min-w-0 max-w-96 flex-col">
                        <Link
                          to={`/forms/${formId}`}
                          className="text-copy font-semibold text-ink outline-none hover:underline focus-visible:ring-3 focus-visible:ring-ring/50"
                          onClick={(event) => event.stopPropagation()}
                        >
                          {form.title}
                        </Link>
                        {form.description && (
                          <span className="truncate text-meta text-muted-foreground">{form.description}</span>
                        )}
                      </div>
                    </div>
                  </TableCell>
                  <TableCell className={CELL_CLASS}>
                    <div className="flex flex-wrap items-center gap-1.5">
                      <FormTypeBadge type={form.type} />
                      {isOnboarding && form.isDefault && <FormDefaultBadge />}
                    </div>
                  </TableCell>
                  <TableCell className={`${CELL_CLASS} text-body text-ink-2`}>
                    {t('forms.table.questions', { count: form.questionCount })}
                  </TableCell>
                  <TableCell className={`${CELL_CLASS} text-body text-ink-2`}>
                    {t('forms.table.clients', { count: form.usedForClientCount })}
                    {form.type === QuestionnaireType.CheckIn && form.lastWeekAnswerRatePercent != null && (
                      <> · {t('forms.table.answeredLastWeek', { percent: form.lastWeekAnswerRatePercent })}</>
                    )}
                  </TableCell>
                  <TableCell className={`${CELL_CLASS} text-body text-muted-foreground`}>
                    {editedLabel(form.editedAt, i18n.language)}
                  </TableCell>
                  <TableCell className={CELL_CLASS}>
                    <DropdownMenu>
                      <DropdownMenuTrigger asChild>
                        <Button
                          type="button"
                          variant="ghost"
                          size="icon-sm"
                          aria-label={t('forms.rowMenu.open', { name: form.title })}
                          onClick={(event) => event.stopPropagation()}
                        >
                          <MoreHorizontal className="size-4" aria-hidden="true" />
                        </Button>
                      </DropdownMenuTrigger>
                      <DropdownMenuContent align="end" onClick={(event) => event.stopPropagation()}>
                        <DropdownMenuItem onSelect={() => navigate(`/forms/${formId}`)}>
                          {t('forms.rowMenu.edit')}
                        </DropdownMenuItem>
                        <DropdownMenuItem
                          disabled={duplicateMutation.isPending}
                          onSelect={() => duplicateMutation.mutate(formId)}
                        >
                          {t('forms.rowMenu.duplicate')}
                        </DropdownMenuItem>
                        <DropdownMenuSeparator />
                        <DropdownMenuItem
                          variant="destructive"
                          onSelect={() => setDeleteTarget({ id: formId, name: form.title })}
                        >
                          {t('forms.rowMenu.delete')}
                        </DropdownMenuItem>
                      </DropdownMenuContent>
                    </DropdownMenu>
                  </TableCell>
                </TableRow>
              );
            })}
        </TableBody>
      </Table>
      {deleteTarget && (
        <DeleteFormDialog
          open
          onOpenChange={(open) => {
            if (!open) {
              setDeleteTarget(null);
            }
          }}
          formId={deleteTarget.id}
          formName={deleteTarget.name}
        />
      )}
    </>
  );
}
