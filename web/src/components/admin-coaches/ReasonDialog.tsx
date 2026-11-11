import type { ReactNode } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { useTranslation } from 'react-i18next';
import { TriangleAlert } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogDescription, DialogTitle } from '@/components/ui/dialog';
import { Textarea } from '@/components/ui/textarea';
import { Field } from '@/components/package-catalog/packageFormParts';
import { REASON_MAX, reasonSchema, type ReasonFormValues } from '@/components/admin-coaches/coachActionForms';
import { cn } from '@/lib/utils';

/** Colours of the icon tile in a dialog header. */
export type TileTone = 'orange' | 'neutral' | 'red';

const TILE_CLASS: Record<TileTone, string> = {
  orange: 'bg-training-soft text-training-ink',
  neutral: 'bg-sunken text-ink-2',
  red: 'bg-error-soft text-error',
};

/** Icon tile at the start of a dialog header. */
export function DialogIconTile({ tone, children }: { tone: TileTone; children: ReactNode }) {
  return (
    <span
      aria-hidden="true"
      className={cn('flex size-11 shrink-0 items-center justify-center rounded-xl [&_svg]:size-5', TILE_CLASS[tone])}
    >
      {children}
    </span>
  );
}

interface Props {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  tone: TileTone;
  icon: ReactNode;
  title: string;
  description: string;
  /** Warning shown under the description, e.g. what ending the plan does to the coach's clients. */
  callout?: string;
  placeholder: string;
  cancelLabel: string;
  confirmLabel: string;
  /** The confirm button is red. */
  destructive?: boolean;
  /** Runs the action; resolves true when it succeeded, which closes the dialog. */
  run: (reason: string) => Promise<boolean>;
}

/** A confirmation dialog that needs only a reason; nothing runs until the admin confirms. */
export default function ReasonDialog({ open, onOpenChange, ...rest }: Props) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="gap-0 overflow-hidden p-0 sm:max-w-110">
        <ReasonForm onClose={() => onOpenChange(false)} {...rest} />
      </DialogContent>
    </Dialog>
  );
}

function ReasonForm({
  tone,
  icon,
  title,
  description,
  callout,
  placeholder,
  cancelLabel,
  confirmLabel,
  destructive = false,
  run,
  onClose,
}: Omit<Props, 'open' | 'onOpenChange'> & { onClose: () => void }) {
  const { t } = useTranslation();
  const {
    register,
    handleSubmit,
    formState: { errors, isSubmitting },
  } = useForm<ReasonFormValues>({ resolver: zodResolver(reasonSchema), defaultValues: { reason: '' } });

  const submit = handleSubmit(async (values) => {
    if (await run(values.reason.trim())) {
      onClose();
    }
  });

  return (
    <form onSubmit={submit} noValidate className="flex flex-col">
      <div className="flex items-center gap-3.5 px-6 pt-5.5 pr-14">
        <DialogIconTile tone={tone}>{icon}</DialogIconTile>
        <DialogTitle className="font-display text-panel-title leading-snug">{title}</DialogTitle>
      </div>
      <div className="flex flex-col gap-4 px-6 pt-2.5 pb-5.5">
        <DialogDescription className="text-copy text-muted-foreground">{description}</DialogDescription>
        {callout && (
          <div className="flex gap-2.5 rounded-xl bg-training-soft px-3.5 py-3 text-training-ink">
            <TriangleAlert aria-hidden="true" className="mt-px size-4 shrink-0" />
            <p className="text-body leading-normal">{callout}</p>
          </div>
        )}
        <Field label={t('adminCoaches.actions.reason')} error={errors.reason?.message}>
          {(fieldControl) => (
            <Textarea rows={3} maxLength={REASON_MAX} placeholder={placeholder} {...register('reason')} {...fieldControl} />
          )}
        </Field>
      </div>
      <div className="flex justify-end gap-2 border-t border-line px-6 py-4">
        <Button type="button" variant="outline" size="lg" disabled={isSubmitting} onClick={onClose}>
          {cancelLabel}
        </Button>
        <Button
          type="submit"
          size="lg"
          variant={destructive ? 'destructive' : 'default'}
          className={destructive ? 'bg-error text-primary-foreground hover:bg-error/90' : undefined}
          disabled={isSubmitting}
        >
          {confirmLabel}
        </Button>
      </div>
    </form>
  );
}
