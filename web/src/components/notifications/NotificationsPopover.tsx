import { useState, type ReactNode } from 'react';
import { useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import {
  Ban,
  Bell,
  CalendarX,
  Camera,
  Check,
  ChevronRight,
  History,
  MessageSquare,
  Repeat,
  UserCheck,
  UserPlus,
  UserX,
  type LucideIcon,
} from 'lucide-react';
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover';
import { Skeleton } from '@/components/ui/skeleton';
import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';
import type { NotificationDto } from '@/api/notifications';
import { parseOfflineSyncConflictPayload } from '@/api/offlineSyncConflicts';
import {
  useMarkAllNotificationsRead,
  useMarkNotificationRead,
  useNotifications,
  useUnreadNotificationsCount,
} from '@/hooks/useNotificationsQueries';
import { NOTIFICATIONS_PAGE_SIZE } from '@/api/notifications';
import { useSubscription } from '@/hooks/useSubscriptionQueries';
import { KEEP_CLIENTS_PATH, PLAN_BILLING_PATH } from '@/components/subscription/planLinks';

const MS_PER_DAY = 24 * 60 * 60 * 1000;
const RECENT_WEEKDAY_WINDOW_DAYS = 6;

const TYPE_ICON: Record<string, LucideIcon> = {
  clientrequestreceived: UserPlus,
  invitationaccepted: UserCheck,
  invitationdeclined: UserX,
  newmessage: MessageSquare,
  progressphotossubmitted: Camera,
  coachplanchanged: Repeat,
  coachplanended: CalendarX,
  coachplanrenewalcancelled: Ban,
};

const PLAN_NOTIFICATION_TYPES: ReadonlySet<string> = new Set([
  'coachplanchanged',
  'coachplanended',
  'coachplanrenewalcancelled',
]);
const COACH_PLAN_ENDED_TYPE = 'coachplanended';

const OFFLINE_SYNC_CONFLICT_TYPE = 'offlinesyncconflict';
const PROGRESS_PHOTOS_SUBMITTED_TYPE = 'progressphotossubmitted';

/** Reads the client public id from a `progressphotossubmitted` payload (`clientId`); undefined when absent. */
function readPhotosClientId(raw: string | null | undefined): string | undefined {
  if (!raw) return undefined;
  try {
    const parsed: unknown = JSON.parse(raw);
    if (typeof parsed !== 'object' || parsed === null) return undefined;
    const value = (parsed as Record<string, unknown>).clientId;
    return typeof value === 'string' && value.length > 0 ? value : undefined;
  } catch {
    return undefined;
  }
}

/** Up to two initials from a full name, e.g. "Jana Nováková" -> "JN". */
function nameInitials(name: string | undefined): string {
  const parts = (name ?? '').trim().split(/\s+/).filter(Boolean);
  return parts
    .slice(0, 2)
    .map((part) => part.charAt(0).toLocaleUpperCase())
    .join('');
}

interface Props {
  collapsed: boolean;
  /** Shared sidebar row classes, so the trigger looks like its neighbours. */
  rowClassName: string;
  /** The row's label, already wrapped for the collapse animation. */
  label: ReactNode;
  /** Fired when a link inside the popover navigates — closes the mobile drawer. */
  onNavigate?: () => void;
}

/** "10:42" today, a short weekday within the last week, otherwise "28 Sep". */
function formatTimestamp(iso: string, locale: string): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return '';
  const now = new Date();
  if (date.toDateString() === now.toDateString()) {
    return date.toLocaleTimeString(locale, { hour: '2-digit', minute: '2-digit' });
  }
  if (now.getTime() - date.getTime() < RECENT_WEEKDAY_WINDOW_DAYS * MS_PER_DAY) {
    return date.toLocaleDateString(locale, { weekday: 'short' });
  }
  return date.toLocaleDateString(locale, { day: 'numeric', month: 'short' });
}

/** The sidebar Notifications row and the popover it opens. */
export default function NotificationsPopover({ collapsed, rowClassName, label, onNavigate }: Props) {
  const { t, i18n } = useTranslation();
  const navigate = useNavigate();
  const [open, setOpen] = useState(false);
  const notificationsQuery = useNotifications();
  const { count, capped } = useUnreadNotificationsCount();
  const markRead = useMarkNotificationRead();
  const markAllRead = useMarkAllNotificationsRead();
  // Same rule as the account menu: the keep-clients screen is only useful while a pick is still due.
  const needsKeepPick = useSubscription().keepPickDue;

  const items = notificationsQuery.data ?? [];
  const badgeText = capped ? `${NOTIFICATIONS_PAGE_SIZE}+` : String(count);
  const title = t('notifications.title');

  function reviewInClients(item: NotificationDto) {
    if (!item.read) markRead.mutate(item.id);
    setOpen(false);
    onNavigate?.();
    navigate('/clients?tab=Pending');
  }

  function openPlanPage(item: NotificationDto, path: string) {
    if (!item.read) markRead.mutate(item.id);
    setOpen(false);
    onNavigate?.();
    navigate(path);
  }

  function seeClientActivity(item: NotificationDto, clientPublicId: string) {
    if (!item.read) markRead.mutate(item.id);
    setOpen(false);
    onNavigate?.();
    navigate(`/clients/${encodeURIComponent(clientPublicId)}`);
  }

  function seeClientPhotos(item: NotificationDto, clientPublicId: string) {
    if (!item.read) markRead.mutate(item.id);
    setOpen(false);
    onNavigate?.();
    navigate(`/clients/${encodeURIComponent(clientPublicId)}/photos`);
  }

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <button
          type="button"
          title={collapsed ? title : undefined}
          aria-label={
            count > 0 ? `${title}, ${t('notifications.unreadCount', { count })}` : collapsed ? title : undefined
          }
          className={cn(
            rowClassName,
            'text-sidebar-muted outline-none hover:bg-sidebar-active/60 hover:text-sidebar-text focus-visible:ring-2 focus-visible:ring-sidebar-text/60',
            open && 'bg-sidebar-active font-semibold text-sidebar-text',
          )}
        >
          <Bell className="size-4 shrink-0" aria-hidden="true" />
          {label}
          {count > 0 && (
            <span
              data-testid="notifications-badge"
              aria-hidden="true"
              className={cn(
                'flex h-5 min-w-5 items-center justify-center rounded-full bg-marker-solid px-1.5 text-label font-bold text-sidebar-text',
                collapsed ? 'absolute top-0.5 right-0.5' : 'ml-auto',
              )}
            >
              {badgeText}
            </span>
          )}
        </button>
      </PopoverTrigger>
      <PopoverContent side="right" align="end" sideOffset={14} aria-label={title} className="flex w-90 flex-col p-2">
        <div className="flex items-center px-2.5 pt-1.5 pb-2">
          <h2 className="font-display text-lg font-semibold text-foreground">{title}</h2>
          <Button
            type="button"
            variant="ghost"
            size="sm"
            className="ml-auto gap-1.5 text-label font-semibold"
            disabled={count === 0 || markAllRead.isPending}
            onClick={() => markAllRead.mutate()}
          >
            <Check className="size-3.5" aria-hidden="true" />
            {t('notifications.markAllReadShort')}
          </Button>
        </div>
        <div className="mb-1.5 h-px bg-border" />

        {notificationsQuery.isPending && (
          <div className="flex flex-col gap-2 p-2">
            <Skeleton className="h-14 w-full" />
            <Skeleton className="h-14 w-full" />
          </div>
        )}

        {notificationsQuery.isError && (
          <div className="flex flex-col items-center gap-3 px-2 py-6">
            <p className="text-body text-muted-foreground">{t('common.dataLoadError')}</p>
            <Button type="button" variant="outline" size="sm" onClick={() => void notificationsQuery.refetch()}>
              {t('clients.retry')}
            </Button>
          </div>
        )}

        {!notificationsQuery.isPending && !notificationsQuery.isError && items.length === 0 && (
          <p className="px-2 py-6 text-center text-body text-muted-foreground">{t('notifications.empty')}</p>
        )}

        {items.length > 0 && (
          <ul className="flex max-h-96 flex-col gap-0.5 overflow-y-auto">
            {items.map((item) => {
              const Icon = TYPE_ICON[item.type] ?? Bell;
              const isConflict = item.type === OFFLINE_SYNC_CONFLICT_TYPE;
              const conflictPayload = isConflict ? parseOfflineSyncConflictPayload(item.actionPayload) : {};
              const initials = nameInitials(conflictPayload.clientName);
              const photosClientId =
                item.type === PROGRESS_PHOTOS_SUBMITTED_TYPE ? readPhotosClientId(item.actionPayload) : undefined;
              return (
                <li
                  key={item.id}
                  data-testid="notification-item"
                  data-unread={!item.read}
                  className={cn('flex gap-2.5 rounded-field p-2.5', !item.read && 'bg-marker/5')}
                >
                  {isConflict && initials ? (
                    <span
                      data-testid="notification-conflict-avatar"
                      className="relative flex size-8.5 shrink-0 items-center justify-center rounded-full bg-muted text-label font-semibold text-foreground"
                    >
                      {initials}
                      <span className="absolute -right-0.5 -bottom-0.5 flex size-4 items-center justify-center rounded-full border border-card bg-caution-soft text-caution-ink">
                        <History className="size-2.5" aria-hidden="true" />
                      </span>
                    </span>
                  ) : (
                    <span className="flex size-8.5 shrink-0 items-center justify-center rounded-full bg-muted text-foreground">
                      <Icon className="size-4" aria-hidden="true" />
                    </span>
                  )}
                  <div className="flex min-w-0 grow flex-col gap-1">
                    <div className="flex items-start gap-2">
                      <span
                        className={cn(
                          'text-body leading-snug',
                          item.read ? 'font-medium text-muted-foreground' : 'font-bold text-foreground',
                        )}
                      >
                        {item.title}
                      </span>
                      <time
                        dateTime={item.timestamp}
                        className="ml-auto pt-0.5 text-label whitespace-nowrap text-muted-foreground"
                      >
                        {formatTimestamp(item.timestamp, i18n.language)}
                      </time>
                    </div>
                    {item.body && <span className="truncate text-label text-muted-foreground">{item.body}</span>}
                    {isConflict && conflictPayload.clientPublicId && (
                      <button
                        type="button"
                        onClick={() => seeClientActivity(item, conflictPayload.clientPublicId!)}
                        className="inline-flex items-center gap-0.5 self-start text-label font-semibold text-foreground underline underline-offset-2"
                      >
                        {t('notifications.seeInClientActivity')}
                        <ChevronRight className="size-3.5" aria-hidden="true" />
                      </button>
                    )}
                    {item.type === PROGRESS_PHOTOS_SUBMITTED_TYPE && photosClientId && (
                      <button
                        type="button"
                        onClick={() => seeClientPhotos(item, photosClientId)}
                        className="inline-flex items-center gap-0.5 self-start text-label font-semibold text-foreground underline underline-offset-2"
                      >
                        {t('notifications.seePhotos')}
                        <ChevronRight className="size-3.5" aria-hidden="true" />
                      </button>
                    )}
                    {PLAN_NOTIFICATION_TYPES.has(item.type) && (
                      <button
                        type="button"
                        onClick={() =>
                          openPlanPage(
                            item,
                            item.type === COACH_PLAN_ENDED_TYPE && needsKeepPick ? KEEP_CLIENTS_PATH : PLAN_BILLING_PATH,
                          )
                        }
                        className="inline-flex items-center gap-0.5 self-start text-label font-semibold text-foreground underline underline-offset-2"
                      >
                        {item.type === COACH_PLAN_ENDED_TYPE && needsKeepPick
                          ? t('notifications.pickClientsToKeep')
                          : t('notifications.seeYourPlan')}
                        <ChevronRight className="size-3.5" aria-hidden="true" />
                      </button>
                    )}
                    {item.type === 'clientrequestreceived' && (
                      <button
                        type="button"
                        onClick={() => reviewInClients(item)}
                        className="inline-flex items-center gap-0.5 self-start text-label font-semibold text-foreground underline underline-offset-2"
                      >
                        {t('notifications.reviewInClients')}
                        <ChevronRight className="size-3.5" aria-hidden="true" />
                      </button>
                    )}
                  </div>
                  {item.read ? (
                    <span className="w-2 shrink-0" />
                  ) : (
                    <button
                      type="button"
                      title={t('notifications.markRead')}
                      aria-label={t('notifications.markRead')}
                      onClick={() => markRead.mutate(item.id)}
                      className="mt-1.5 size-2 shrink-0 rounded-full bg-marker-solid outline-none focus-visible:ring-2 focus-visible:ring-ring"
                    />
                  )}
                </li>
              );
            })}
          </ul>
        )}
      </PopoverContent>
    </Popover>
  );
}
