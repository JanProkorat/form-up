import { useEffect, useRef, useState } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/button';
import { Textarea } from '@/components/ui/textarea';
import {
  Sheet,
  SheetContent,
  SheetDescription,
  SheetFooter,
  SheetHeader,
  SheetTitle,
} from '@/components/ui/sheet';
import { useBroadcastMessage } from '@/hooks/useClientsQueries';
import { isSubscriptionRequired } from '@/lib/subscriptionErrors';
import PaywallDialog from '@/components/subscription/PaywallDialog';

interface FormValues {
  text: string;
}

interface Props {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  clientPublicIds: string[];
  onSent: () => void;
}

/**
 * Compose-and-send drawer for the bulk action bar. `{{firstName}}` /
 * `{{fullName}}` are substituted server-side per recipient — this drawer
 * only inserts the literal token text, it never renders a local preview of
 * the substitution. The server caps recipients at 50 and re-checks length
 * *after* substitution; both failure codes
 * (BROADCAST_RECIPIENT_LIMIT_EXCEEDED,
 * BROADCAST_MESSAGE_TOO_LONG_AFTER_SUBSTITUTION) surface through
 * `useBroadcastMessage`'s existing `showApiError` call — no special
 * handling needed here beyond the translation entries.
 */
export default function BroadcastDrawer({ open, onOpenChange, clientPublicIds, onSent }: Props) {
  const { t } = useTranslation();
  const textareaRef = useRef<HTMLTextAreaElement | null>(null);
  const [paywallOpen, setPaywallOpen] = useState(false);

  const schema = z.object({
    text: z.string().min(1, t('clients.broadcastDrawer.validation.textRequired')),
  });

  const {
    register,
    handleSubmit,
    reset,
    watch,
    setValue,
    formState: { errors, isValid },
  } = useForm<FormValues>({
    resolver: zodResolver(schema),
    mode: 'onTouched',
    defaultValues: { text: '' },
  });

  const { ref: textRegisterRef, ...textRegister } = register('text');
  const text = watch('text');
  const broadcastMutation = useBroadcastMessage();

  useEffect(() => {
    if (open) {
      reset({ text: '' });
    }
  }, [open, reset]);

  function insertToken(token: string) {
    const nextValue = text ? `${text} ${token}` : token;
    setValue('text', nextValue, { shouldValidate: true });
    textareaRef.current?.focus();
  }

  function onSubmit(values: FormValues) {
    broadcastMutation.mutate(
      { clientPublicIds, text: values.text },
      {
        onSuccess: () => {
          onOpenChange(false);
          onSent();
        },
        onError: (error) => {
          if (isSubscriptionRequired(error)) {
            setPaywallOpen(true);
          }
        },
      },
    );
  }

  return (
    <>
    <PaywallDialog open={paywallOpen} onOpenChange={setPaywallOpen} feature="chat" />
    <Sheet open={open} onOpenChange={onOpenChange}>
      <SheetContent>
        <SheetHeader>
          <SheetTitle>{t('clients.broadcastDrawer.title')}</SheetTitle>
          <SheetDescription>
            {t('clients.broadcastDrawer.description', { count: clientPublicIds.length })}
          </SheetDescription>
        </SheetHeader>
        <form onSubmit={handleSubmit(onSubmit)} className="flex flex-1 flex-col gap-4 px-4">
          <div className="flex flex-col gap-1.5">
            <Textarea
              {...textRegister}
              ref={(node) => {
                textRegisterRef(node);
                textareaRef.current = node;
              }}
              rows={6}
              placeholder={t('clients.broadcastDrawer.placeholder')}
              aria-invalid={!!errors.text}
            />
            {errors.text && <p className="text-meta text-destructive">{errors.text.message}</p>}
            <div className="flex flex-wrap gap-2">
              <Button type="button" variant="outline" size="xs" onClick={() => insertToken('{{firstName}}')}>
                {t('clients.broadcastDrawer.insertFirstName')}
              </Button>
              <Button type="button" variant="outline" size="xs" onClick={() => insertToken('{{fullName}}')}>
                {t('clients.broadcastDrawer.insertFullName')}
              </Button>
            </div>
          </div>
        </form>
        <SheetFooter>
          <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>
            {t('common.cancel')}
          </Button>
          <Button
            type="button"
            disabled={!isValid || broadcastMutation.isPending}
            onClick={handleSubmit(onSubmit)}
          >
            {broadcastMutation.isPending ? t('common.sending') : t('clients.broadcastDrawer.send')}
          </Button>
        </SheetFooter>
      </SheetContent>
    </Sheet>
    </>
  );
}
