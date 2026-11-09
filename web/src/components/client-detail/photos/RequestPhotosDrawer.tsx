import { useEffect, useRef, useState } from 'react';
import { useForm, useWatch } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { useTranslation } from 'react-i18next';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Bell, Calendar, Camera, Check, ChevronDown, Eye, MessageSquare, Send } from 'lucide-react';
import { cancelDiaryRequest, createDiaryRequest, type PhotoDiaryRequestSummary } from '@/api/diary-requests';
import type { ClientPlanItem } from '@/api/client-plans';
import { PhotoDiaryMode } from '@/api/generated';
import { getRfc7807ErrorCode, showApiError, showSuccess } from '@/lib/api-errors';
import { useAuthStore } from '@/stores/auth';
import { cn } from '@/lib/utils';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { Textarea } from '@/components/ui/textarea';
import { Sheet, SheetContent, SheetDescription, SheetFooter, SheetHeader, SheetTitle } from '@/components/ui/sheet';
import RequestPhotosStatus, {
  RequestPreviewCard,
  RequestSectionHeading,
} from '@/components/client-detail/photos/RequestPhotosStatus';
import {
  DAY_OPTIONS,
  REQUEST_DEFAULT_DAYS,
  REQUEST_MAX_DAYS,
  REQUEST_MESSAGE_MAX,
  REQUEST_MIN_DAYS,
  findPlan,
  formatWeekdayDate,
  previewEndsAt,
  usePlanLabel,
  type RequestFrequency,
} from '@/components/client-detail/photos/photoRequestModel';
import { photoRequestsKey } from '@/components/client-detail/photos/useOpenPhotoRequest';
import HorizontalDumbbellIcon from '@/components/clients/HorizontalDumbbellIcon';

const SELECT_CLASS =
  'flex h-10 w-full min-w-0 appearance-none rounded-field border border-input bg-background py-1 pr-9 pl-3 text-copy text-foreground outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 aria-invalid:border-destructive disabled:opacity-50';

function SelectChevron() {
  return (
    <ChevronDown
      className="pointer-events-none absolute top-1/2 right-3 size-4 -translate-y-1/2 text-muted-foreground"
      aria-hidden="true"
    />
  );
}

const ERROR_ALREADY_OPEN = 'PHOTO_DIARY_REQUEST_ALREADY_OPEN';
const ERROR_INVALID_STATUS = 'PHOTO_DIARY_REQUEST_INVALID_STATUS';

const schema = z.object({
  frequency: z.enum(['once', 'daily']),
  durationDays: z.number().int().min(REQUEST_MIN_DAYS).max(REQUEST_MAX_DAYS),
  planId: z.string(),
  message: z.string().max(REQUEST_MESSAGE_MAX),
});

type FormValues = z.infer<typeof schema>;

interface Props {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  clientId: string;
  /** The client's first name (or the localized fallback). */
  name: string;
  plans: ClientPlanItem[] | undefined;
  /** The open request, null when none, undefined while the lookup is loading. */
  openRequest: PhotoDiaryRequestSummary | null | undefined;
  /** The lookup failed and nothing is cached: show an error with retry instead of the form. */
  lookupFailed?: boolean;
  onRetryLookup?: () => void;
}

/**
 * Side drawer of the Photos tab: the request form, or, while a request is open,
 * its status with a Cancel request action.
 */
export default function RequestPhotosDrawer({
  open,
  onOpenChange,
  clientId,
  name,
  plans,
  openRequest,
  lookupFailed = false,
  onRetryLookup,
}: Props) {
  const { t, i18n } = useTranslation();
  const user = useAuthStore((state) => state.user);
  const queryClient = useQueryClient();
  const coachName = user?.firstName?.trim() || t('clientDetail.photos.requestDrawer.coachFallback');
  const [confirmingCancel, setConfirmingCancel] = useState(false);
  const keepRequestRef = useRef<HTMLButtonElement>(null);
  const cancelRequestRef = useRef<HTMLButtonElement>(null);
  const confirmShownRef = useRef(false);

  // The inline confirm swaps the buttons out, so hand focus over: to the safe choice when it
  // appears, back to Cancel request when the coach keeps the request.
  useEffect(() => {
    if (confirmingCancel) {
      keepRequestRef.current?.focus();
    } else if (confirmShownRef.current) {
      cancelRequestRef.current?.focus();
    }
    confirmShownRef.current = confirmingCancel;
  }, [confirmingCancel]);

  const refreshRequests = () => queryClient.invalidateQueries({ queryKey: photoRequestsKey(clientId) });

  const cancelMutation = useMutation({
    mutationFn: (id: string) => cancelDiaryRequest(id),
    onSuccess: async () => {
      await refreshRequests();
      showSuccess('clientDetail.photos.requestDrawer.cancelled');
      setConfirmingCancel(false);
      onOpenChange(false);
    },
    onError: (error) => {
      showApiError(error, 'clientDetail.photos.requestDrawer.cancelError');
      setConfirmingCancel(false);
      void refreshRequests();
    },
  });

  const handleOpenChange = (next: boolean) => {
    if (!next) {
      setConfirmingCancel(false);
    }
    onOpenChange(next);
  };

  return (
    <Sheet open={open} onOpenChange={handleOpenChange}>
      <SheetContent className="gap-0 sm:w-drawer sm:max-w-none">
        <SheetHeader className="border-b border-border px-6 py-5 pr-14">
          <SheetTitle className="text-auth-title">{t('clientDetail.photos.requestDrawer.title')}</SheetTitle>
          <SheetDescription className="text-body">
            {openRequest
              ? t('clientDetail.photos.requestDrawer.descriptionOpen', { name })
              : t('clientDetail.photos.requestDrawer.description', { name })}
          </SheetDescription>
        </SheetHeader>

        {lookupFailed ? (
          <div
            role="alert"
            data-testid="photo-request-lookup-error"
            className="flex flex-1 flex-col items-center justify-center gap-3 px-6 py-5 text-center"
          >
            <p className="text-body text-muted-foreground">{t('clientDetail.photos.loadError')}</p>
            <Button type="button" variant="outline" onClick={onRetryLookup}>
              {t('clients.retry')}
            </Button>
          </div>
        ) : openRequest === undefined ? (
          <div className="flex flex-1 flex-col gap-4 px-6 py-5">
            <Skeleton className="h-6 w-1/3" />
            <Skeleton className="h-24 w-full" />
            <Skeleton className="h-24 w-full" />
          </div>
        ) : openRequest ? (
          <>
            <RequestPhotosStatus
              request={openRequest}
              name={name}
              coachName={coachName}
              plans={plans}
              locale={i18n.language}
            />
            <SheetFooter className="flex-row items-center gap-2 border-t border-border px-6 py-4">
              {confirmingCancel ? (
                <>
                  <span className="mr-auto text-meta text-muted-foreground">
                    {t('clientDetail.photos.requestDrawer.cancelConfirm', { name })}
                  </span>
                  <Button
                    ref={keepRequestRef}
                    type="button"
                    variant="outline"
                    size="lg"
                    disabled={cancelMutation.isPending}
                    onClick={() => setConfirmingCancel(false)}
                  >
                    {t('clientDetail.photos.requestDrawer.keepRequest')}
                  </Button>
                  <Button
                    type="button"
                    variant="destructive"
                    size="lg"
                    disabled={cancelMutation.isPending || !openRequest.id}
                    onClick={() => openRequest.id && cancelMutation.mutate(openRequest.id)}
                  >
                    {cancelMutation.isPending
                      ? t('clientDetail.photos.requestDrawer.cancelling')
                      : t('clientDetail.photos.requestDrawer.confirmCancel')}
                  </Button>
                </>
              ) : (
                <>
                  <Button type="button" variant="outline" size="lg" className="ml-auto" onClick={() => onOpenChange(false)}>
                    {t('common.close')}
                  </Button>
                  <Button
                    ref={cancelRequestRef}
                    type="button"
                    variant="destructive"
                    size="lg"
                    onClick={() => setConfirmingCancel(true)}
                  >
                    {t('clientDetail.photos.requestDrawer.cancelRequest')}
                  </Button>
                </>
              )}
            </SheetFooter>
          </>
        ) : (
          <RequestForm
            clientId={clientId}
            name={name}
            coachName={coachName}
            plans={plans}
            locale={i18n.language}
            onClose={() => onOpenChange(false)}
            onCreated={async () => {
              await refreshRequests();
              showSuccess('clientDetail.photos.requestDrawer.sent');
              onOpenChange(false);
            }}
            onConflict={() => void refreshRequests()}
          />
        )}
      </SheetContent>
    </Sheet>
  );
}

interface FormProps {
  clientId: string;
  name: string;
  coachName: string;
  plans: ClientPlanItem[] | undefined;
  locale: string;
  onClose: () => void;
  onCreated: () => Promise<void>;
  /** The server says a request is already open (or not cancellable): refetch so the drawer switches view. */
  onConflict: () => void;
}

/** Mounted only while the sheet is open, so every opening starts from the defaults. */
function RequestForm({ clientId, name, coachName, plans, locale, onClose, onCreated, onConflict }: FormProps) {
  const { t } = useTranslation();
  const planLabel = usePlanLabel();

  const { register, handleSubmit, setValue, control } = useForm<FormValues>({
    resolver: zodResolver(schema),
    defaultValues: { frequency: 'daily', durationDays: REQUEST_DEFAULT_DAYS, planId: '', message: '' },
  });

  const frequency = useWatch({ control, name: 'frequency' });
  const durationDays = useWatch({ control, name: 'durationDays' });
  const planId = useWatch({ control, name: 'planId' });
  const message = useWatch({ control, name: 'message' }) ?? '';
  const plan = findPlan(plans, planId || undefined);
  const trimmedMessage = message.trim();

  const createMutation = useMutation({
    mutationFn: (values: FormValues) =>
      createDiaryRequest({
        clientId,
        mode: values.frequency === 'once' ? PhotoDiaryMode.Bulk : PhotoDiaryMode.Workflow,
        durationDays: values.frequency === 'once' ? 1 : values.durationDays,
        planId: values.planId || undefined,
        message: values.message.trim() || undefined,
      }),
    onSuccess: () => onCreated(),
    onError: (error) => {
      const code = getRfc7807ErrorCode(error);
      showApiError(error, 'clientDetail.photos.requestDrawer.sendError');
      if (code === ERROR_ALREADY_OPEN || code === ERROR_INVALID_STATUS) {
        onConflict();
      }
    },
  });

  const endsAt = previewEndsAt(frequency, Number.isFinite(durationDays) ? durationDays : REQUEST_DEFAULT_DAYS);
  const endsLabel = formatWeekdayDate(endsAt, locale);

  const options: { value: RequestFrequency; label: string }[] = [
    { value: 'once', label: t('clientDetail.photos.requestDrawer.once') },
    { value: 'daily', label: t('clientDetail.photos.requestDrawer.everyDay') },
  ];

  return (
    <form
      onSubmit={handleSubmit((values) => createMutation.mutate(values))}
      className="flex min-h-0 flex-1 flex-col"
      noValidate
    >
      <div className="flex flex-1 flex-col gap-6 overflow-y-auto px-6 py-5">
        <section className="flex flex-col gap-3">
          <RequestSectionHeading icon={<Calendar className="size-4" />}>
            {t('clientDetail.photos.requestDrawer.frequency.heading')}
          </RequestSectionHeading>
          <div
            role="group"
            aria-label={t('clientDetail.photos.requestDrawer.frequency.heading')}
            className="grid grid-cols-2 gap-2"
          >
            {options.map(({ value, label }) => {
              const selected = frequency === value;
              return (
                <Button
                  key={value}
                  type="button"
                  size="lg"
                  variant="outline"
                  aria-pressed={selected}
                  className={cn(
                    'font-semibold',
                    selected && 'border-success bg-success-soft text-success-ink hover:bg-success-soft',
                  )}
                  onClick={() => setValue('frequency', value, { shouldDirty: true })}
                >
                  {selected ? <Check aria-hidden="true" /> : value === 'once' && <Camera aria-hidden="true" />}
                  {label}
                </Button>
              );
            })}
          </div>

          {frequency === 'daily' && (
            <label className="flex flex-col gap-1.5 text-body font-medium text-ink-2">
              <span>{t('clientDetail.photos.requestDrawer.for')}</span>
              <div className="relative">
                <select
                  {...register('durationDays', { valueAsNumber: true })}
                  className={SELECT_CLASS}
                  disabled={createMutation.isPending}
                >
                  {DAY_OPTIONS.map((days) => (
                    <option key={days} value={days}>
                      {t('clientDetail.photos.requestDrawer.days', { count: days })}
                    </option>
                  ))}
                </select>
                <SelectChevron />
              </div>
              <span className="text-meta font-normal text-muted-foreground">
                {t('clientDetail.photos.requestDrawer.untilHelp', {
                  date: endsLabel,
                  min: REQUEST_MIN_DAYS,
                  max: REQUEST_MAX_DAYS,
                })}
              </span>
            </label>
          )}
          {frequency === 'once' && (
            <p className="text-meta text-muted-foreground">
              {t('clientDetail.photos.requestDrawer.onceHelp', { name })}
            </p>
          )}

          <div className="flex items-start gap-2.5 rounded-field bg-sunken px-3.5 py-3 text-body leading-normal text-ink-2">
            <Bell className="mt-px size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
            <span>
              {frequency === 'daily'
                ? t('clientDetail.photos.requestDrawer.reminderDaily', { name })
                : t('clientDetail.photos.requestDrawer.reminderOnce', { name })}
            </span>
          </div>
        </section>

        <section className="flex flex-col gap-3">
          <RequestSectionHeading
            icon={<HorizontalDumbbellIcon className="size-4" />}
            optional={t('clientDetail.photos.requestDrawer.optional')}
          >
            {t('clientDetail.photos.requestDrawer.plan.heading')}
          </RequestSectionHeading>
          <label className="flex flex-col gap-1.5 text-body font-medium text-ink-2">
            <span>{t('clientDetail.photos.requestDrawer.plan.label')}</span>
            <div className="relative">
              <select {...register('planId')} className={SELECT_CLASS} disabled={createMutation.isPending}>
                <option value="">{t('clientDetail.photos.requestDrawer.plan.none')}</option>
                {(plans ?? []).map((candidate) => (
                  <option key={candidate.planId} value={candidate.planId}>
                    {planLabel(candidate)}
                  </option>
                ))}
              </select>
              <SelectChevron />
            </div>
            <span className="text-meta font-normal text-muted-foreground">
              {t('clientDetail.photos.requestDrawer.plan.help')}
            </span>
          </label>
        </section>

        <section className="flex flex-col gap-3">
          <RequestSectionHeading
            icon={<MessageSquare className="size-4" />}
            optional={t('clientDetail.photos.requestDrawer.optional')}
          >
            {t('clientDetail.photos.requestDrawer.message.heading')}
          </RequestSectionHeading>
          <label className="flex flex-col gap-1.5 text-body font-medium text-ink-2">
            <span>{t('clientDetail.photos.requestDrawer.message.label', { name })}</span>
            <Textarea
              {...register('message')}
              rows={3}
              maxLength={REQUEST_MESSAGE_MAX}
              disabled={createMutation.isPending}
              className="min-h-21 resize-none leading-normal"
            />
            <span className="self-end text-meta font-normal text-muted-foreground">
              {t('clientDetail.photos.requestDrawer.message.counter', {
                current: message.length,
                max: REQUEST_MESSAGE_MAX,
              })}
            </span>
          </label>
        </section>

        <section className="flex flex-col gap-3">
          <RequestSectionHeading icon={<Eye className="size-4" />}>
            {t('clientDetail.photos.requestDrawer.preview.heading', { name })}
          </RequestSectionHeading>
          <RequestPreviewCard
            coachName={coachName}
            frequency={frequency}
            endsAt={endsAt}
            planName={plan?.name}
            message={trimmedMessage || undefined}
            locale={locale}
          />
        </section>
      </div>

      <SheetFooter className="flex-row items-center gap-2 border-t border-border px-6 py-4">
        <span className="mr-auto text-meta text-muted-foreground">
          {t('clientDetail.photos.requestDrawer.footnote', { name })}
        </span>
        <Button type="button" variant="outline" size="lg" onClick={onClose}>
          {t('common.cancel')}
        </Button>
        <Button type="submit" size="lg" disabled={createMutation.isPending}>
          <Send aria-hidden="true" />
          {createMutation.isPending ? t('common.sending') : t('clientDetail.photos.requestDrawer.submit')}
        </Button>
      </SheetFooter>
    </form>
  );
}
