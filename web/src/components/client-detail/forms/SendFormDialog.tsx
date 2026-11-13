import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { assignQuestionnaire, clientFormsKey, QuestionnaireType } from '@/api/questionnaires';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Skeleton } from '@/components/ui/skeleton';
import { useTrainerQuestionnaires } from '@/hooks/useQuestionnaireQueries';
import PaywallDialog from '@/components/subscription/PaywallDialog';
import { useInvalidateSubscription } from '@/hooks/useSubscriptionQueries';
import { showApiError, showSuccess } from '@/lib/api-errors';
import { isSubscriptionRequired } from '@/lib/subscriptionErrors';

interface Props {
  clientId: string;
  clientName: string;
  open: boolean;
  onOpenChange: (open: boolean) => void;
}

/** Sends one of the coach's active forms to the client. Check-in forms are left out: the server refuses to send them. */
export default function SendFormDialog({ clientId, clientName, open, onOpenChange }: Props) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const formsQuery = useTrainerQuestionnaires();
  const [picked, setPicked] = useState<string | null>(null);
  const [paywallOpen, setPaywallOpen] = useState(false);
  const invalidateSubscription = useInvalidateSubscription();

  const forms = (formsQuery.data ?? []).filter((form) => form.isActive && form.type !== QuestionnaireType.CheckIn);

  const send = useMutation({
    mutationFn: (questionnairePublicId: string) => assignQuestionnaire(clientId, questionnairePublicId),
    onSuccess: () => {
      showSuccess('clientForms.send.success');
      onOpenChange(false);
      setPicked(null);
    },
    onError: (error) => {
      if (isSubscriptionRequired(error)) {
        // Stale plan data: refetch it and explain with the paywall instead of a toast.
        invalidateSubscription();
        setPaywallOpen(true);
        return;
      }
      showApiError(error, 'clientForms.send.error');
    },
    onSettled: () => queryClient.invalidateQueries({ queryKey: clientFormsKey(clientId) }),
  });

  return (
    <>
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{t('clientForms.send.title', { name: clientName })}</DialogTitle>
          <DialogDescription>{t('clientForms.send.description')}</DialogDescription>
        </DialogHeader>

        {formsQuery.isPending ? (
          <div className="flex flex-col gap-2">
            <Skeleton className="h-12 w-full" />
            <Skeleton className="h-12 w-full" />
          </div>
        ) : formsQuery.isError ? (
          <p role="alert" className="text-body text-muted-foreground">
            {t('clientForms.send.loadError')}
          </p>
        ) : forms.length === 0 ? (
          <p data-testid="send-form-empty" className="text-body text-muted-foreground">
            {t('clientForms.send.empty')}
          </p>
        ) : (
          <fieldset className="flex max-h-80 flex-col gap-2 overflow-y-auto">
            <legend className="sr-only">{t('clientForms.send.pick')}</legend>
            {forms.map((form) => (
              <label
                key={form.publicId}
                className="flex cursor-pointer items-start gap-3 rounded-xl border border-border bg-card p-3 has-checked:border-ink has-checked:bg-muted has-focus-visible:ring-3 has-focus-visible:ring-ring/50"
              >
                <input
                  type="radio"
                  name="send-form"
                  value={form.publicId}
                  checked={picked === form.publicId}
                  onChange={() => setPicked(form.publicId)}
                  className="mt-1 accent-primary"
                />
                <span className="flex min-w-0 flex-col gap-0.5">
                  <span className="text-copy font-bold text-ink [overflow-wrap:anywhere]">{form.title}</span>
                  {form.description && (
                    <span className="text-body text-muted-foreground [overflow-wrap:anywhere]">{form.description}</span>
                  )}
                </span>
              </label>
            ))}
          </fieldset>
        )}

        <DialogFooter>
          <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>
            {t('common.cancel')}
          </Button>
          <Button
            type="button"
            data-testid="send-form-confirm"
            disabled={!picked || send.isPending}
            onClick={() => picked && send.mutate(picked)}
          >
            {t('clientForms.send.confirm')}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
    <PaywallDialog open={paywallOpen} onOpenChange={setPaywallOpen} feature="forms" />
    </>
  );
}
