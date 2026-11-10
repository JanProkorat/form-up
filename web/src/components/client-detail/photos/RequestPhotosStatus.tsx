import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Calendar, Camera, Eye, MessageSquare } from 'lucide-react';
import HorizontalDumbbellIcon from '@/components/clients/HorizontalDumbbellIcon';
import type { ClientPlanItem } from '@/api/client-plans';
import type { PhotoDiaryRequestSummary } from '@/api/diary-requests';
import {
  findPlan,
  formatDayMonth,
  formatWeekdayDate,
  requestFrequency,
  usePlanLabel,
  type RequestFrequency,
} from '@/components/client-detail/photos/photoRequestModel';

/** Icon + title row that opens a drawer section. */
export function RequestSectionHeading({
  icon,
  children,
  optional,
}: {
  icon: ReactNode;
  children: ReactNode;
  optional?: string;
}) {
  return (
    <div className="flex items-center gap-2 text-copy font-semibold text-ink">
      <span className="flex text-success" aria-hidden="true">
        {icon}
      </span>
      {children}
      {optional && <span className="font-normal text-muted-foreground">({optional})</span>}
    </div>
  );
}

interface PreviewProps {
  coachName: string;
  frequency: RequestFrequency;
  /** Window end: a local Date in the form preview, the server's ISO string in the status view. */
  endsAt: Date | string | undefined;
  planName?: string;
  message?: string;
  locale: string;
}

/** The notification card as the client sees it. */
export function RequestPreviewCard({ coachName, frequency, endsAt, planName, message, locale }: PreviewProps) {
  const { t } = useTranslation();
  const date = formatWeekdayDate(endsAt, locale);
  const schedule =
    frequency === 'once'
      ? t('clientDetail.photos.requestDrawer.preview.once')
      : t('clientDetail.photos.requestDrawer.preview.daily', { date });

  return (
    <div className="flex gap-3 rounded-2xl border border-border p-3.5">
      <span className="flex size-9 shrink-0 items-center justify-center rounded-field bg-training-soft text-training-ink">
        <Camera className="size-5" aria-hidden="true" />
      </span>
      <div className="flex min-w-0 flex-col gap-1">
        <span className="text-copy font-semibold text-ink">
          {t('clientDetail.photos.requestDrawer.preview.title', { name: coachName })}
        </span>
        <span className="text-meta text-muted-foreground">{planName ? `${schedule} · ${planName}` : schedule}</span>
        {message && <span className="whitespace-pre-line break-words text-body text-ink-2">{message}</span>}
      </div>
    </div>
  );
}

interface StatusProps {
  request: PhotoDiaryRequestSummary;
  name: string;
  coachName: string;
  plans: ClientPlanItem[] | undefined;
  locale: string;
}

/** Body of the drawer while a request is open: what was asked, until when, and what the client sees. */
export default function RequestPhotosStatus({ request, name, coachName, plans, locale }: StatusProps) {
  const { t } = useTranslation();
  const planLabel = usePlanLabel();
  const frequency = requestFrequency(request);
  const plan = findPlan(plans, request.planId);
  const statusKey = request.status ?? 'Pending';

  return (
    <div className="flex flex-1 flex-col gap-6 overflow-y-auto px-6 py-5">
      <section className="flex flex-col gap-3">
        <RequestSectionHeading icon={<Calendar className="size-4" />}>
          {t('clientDetail.photos.requestDrawer.status.heading')}
        </RequestSectionHeading>
        <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-2 text-body">
          <dt className="text-muted-foreground">{t('clientDetail.photos.requestDrawer.status.state')}</dt>
          <dd className="font-medium text-ink">
            {t(`clientDetail.photos.requestDrawer.status.values.${statusKey}`, { name })}
          </dd>
          <dt className="text-muted-foreground">{t('clientDetail.photos.requestDrawer.status.frequency')}</dt>
          <dd className="font-medium text-ink">
            {frequency === 'once'
              ? t('clientDetail.photos.requestDrawer.once')
              : t('clientDetail.photos.requestDrawer.everyDay')}
          </dd>
          <dt className="text-muted-foreground">{t('clientDetail.photos.requestDrawer.status.sent')}</dt>
          <dd className="font-medium text-ink">{formatDayMonth(request.createdAt, locale)}</dd>
          {frequency === 'daily' && (
            <>
              <dt className="text-muted-foreground">{t('clientDetail.photos.requestDrawer.status.until')}</dt>
              <dd className="font-medium text-ink">{formatWeekdayDate(request.endsAt, locale)}</dd>
            </>
          )}
        </dl>
        <p className="rounded-field bg-sunken px-3.5 py-3 text-body leading-normal text-ink-2">
          {t(
            frequency === 'once'
              ? 'clientDetail.photos.requestDrawer.status.noteOnce'
              : 'clientDetail.photos.requestDrawer.status.note',
            { name },
          )}
        </p>
      </section>

      {request.planId && (
        <section className="flex flex-col gap-3">
          <RequestSectionHeading icon={<HorizontalDumbbellIcon className="size-4" />}>
            {t('clientDetail.photos.requestDrawer.plan.heading')}
          </RequestSectionHeading>
          <p className="text-body text-ink">
            {plan ? planLabel(plan) : t('clientDetail.photos.planRemoved')}
          </p>
        </section>
      )}

      {request.message && (
        <section className="flex flex-col gap-3">
          <RequestSectionHeading icon={<MessageSquare className="size-4" />}>
            {t('clientDetail.photos.requestDrawer.message.heading')}
          </RequestSectionHeading>
          <p className="whitespace-pre-line break-words text-body text-ink-2">{request.message}</p>
        </section>
      )}

      <section className="flex flex-col gap-3">
        <RequestSectionHeading icon={<Eye className="size-4" />}>
          {t('clientDetail.photos.requestDrawer.preview.heading', { name })}
        </RequestSectionHeading>
        <RequestPreviewCard
          coachName={coachName}
          frequency={frequency}
          endsAt={request.endsAt}
          planName={plan?.name}
          message={request.message}
          locale={locale}
        />
      </section>
    </div>
  );
}
