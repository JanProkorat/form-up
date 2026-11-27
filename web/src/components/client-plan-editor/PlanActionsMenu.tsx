import { useTranslation } from 'react-i18next';
import { MoreHorizontal } from 'lucide-react';
import { Button } from '@/components/ui/button';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu';

interface MenuProps {
  /** The plan can still be deleted: no published week has started. */
  canDelete: boolean;
  /** The plan is Active, so it can be finished. */
  canFinish: boolean;
  /** Number of the selected week when it can be unpublished; undefined hides the item. */
  unpublishWeek?: number;
  disabled: boolean;
  onDelete: () => void;
  onFinish: () => void;
  onUnpublish: () => void;
}

/** The "⋯" menu in the client plan editor header. Renders nothing when no action applies. */
export default function PlanActionsMenu({
  canDelete,
  canFinish,
  unpublishWeek,
  disabled,
  onDelete,
  onFinish,
  onUnpublish,
}: MenuProps) {
  const { t } = useTranslation();
  if (!canDelete && !canFinish && unpublishWeek === undefined) {
    return null;
  }
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button
          type="button"
          variant="outline"
          className="size-7.5 shrink-0"
          disabled={disabled}
          aria-label={t('clientPlanEditor.actions.label')}
          title={t('clientPlanEditor.actions.label')}
          data-testid="plan-actions-trigger"
        >
          <MoreHorizontal className="size-4" aria-hidden="true" />
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end">
        {canFinish && <DropdownMenuItem onSelect={onFinish}>{t('clientPlanEditor.actions.finish')}</DropdownMenuItem>}
        {unpublishWeek !== undefined && (
          <DropdownMenuItem onSelect={onUnpublish}>
            {t('clientPlanEditor.actions.unpublish', { week: unpublishWeek })}
          </DropdownMenuItem>
        )}
        {canDelete && (
          <DropdownMenuItem variant="destructive" onSelect={onDelete}>
            {t('clientPlanEditor.actions.delete')}
          </DropdownMenuItem>
        )}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

interface DialogProps {
  open: boolean;
  busy: boolean;
  onConfirm: () => void;
  onCancel: () => void;
}

/** Asks before the plan is deleted for good. Escape and Cancel change nothing. */
export function DeletePlanDialog({ open, busy, onConfirm, onCancel }: DialogProps) {
  const { t } = useTranslation();
  return (
    <Dialog open={open} onOpenChange={(next) => !next && !busy && onCancel()}>
      <DialogContent role="alertdialog" showCloseButton={false} className="max-w-115" data-testid="delete-plan-dialog">
        <DialogHeader>
          <DialogTitle>{t('clientPlanEditor.delete.title')}</DialogTitle>
          <DialogDescription>{t('clientPlanEditor.delete.body')}</DialogDescription>
        </DialogHeader>
        <DialogFooter>
          <Button type="button" variant="outline" disabled={busy} onClick={onCancel}>
            {t('common.cancel')}
          </Button>
          <Button type="button" variant="destructive" disabled={busy} onClick={onConfirm}>
            {t('clientPlanEditor.delete.confirm')}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

/** Asks before the plan is finished; pending edits are saved first. Escape and Cancel change nothing. */
export function FinishPlanDialog({ open, busy, onConfirm, onCancel }: DialogProps) {
  const { t } = useTranslation();
  return (
    <Dialog open={open} onOpenChange={(next) => !next && !busy && onCancel()}>
      <DialogContent role="alertdialog" showCloseButton={false} className="max-w-115" data-testid="finish-plan-dialog">
        <DialogHeader>
          <DialogTitle>{t('clientPlanEditor.finish.title')}</DialogTitle>
          <DialogDescription>{t('clientPlanEditor.finish.body')}</DialogDescription>
        </DialogHeader>
        <DialogFooter>
          <Button type="button" variant="outline" disabled={busy} onClick={onCancel}>
            {t('common.cancel')}
          </Button>
          <Button type="button" disabled={busy} onClick={onConfirm}>
            {t('clientPlanEditor.finish.confirm')}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

interface UnpublishDialogProps extends DialogProps {
  week: number;
}

/** Asks before a published week goes back to draft; pending edits are saved first. Escape and Cancel change nothing. */
export function UnpublishWeekDialog({ open, busy, week, onConfirm, onCancel }: UnpublishDialogProps) {
  const { t } = useTranslation();
  return (
    <Dialog open={open} onOpenChange={(next) => !next && !busy && onCancel()}>
      <DialogContent role="alertdialog" showCloseButton={false} className="max-w-115" data-testid="unpublish-week-dialog">
        <DialogHeader>
          <DialogTitle>{t('clientPlanEditor.unpublish.title', { week })}</DialogTitle>
          <DialogDescription>{t('clientPlanEditor.unpublish.body', { week })}</DialogDescription>
        </DialogHeader>
        <DialogFooter>
          <Button type="button" variant="outline" disabled={busy} onClick={onCancel}>
            {t('common.cancel')}
          </Button>
          <Button type="button" disabled={busy} onClick={onConfirm}>
            {t('clientPlanEditor.unpublish.confirm')}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
