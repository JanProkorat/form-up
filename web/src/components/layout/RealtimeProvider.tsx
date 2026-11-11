import { useCallback, useEffect, useMemo, useRef, type ReactNode } from 'react';
import { HubConnectionBuilder, HubConnection, LogLevel, HubConnectionState, HttpError } from '@microsoft/signalr';
import { useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { useAuthStore } from '@/stores/auth';
import { useToastStore } from '@/stores/toast';
import { clientOfflineSyncConflictKeys } from '@/hooks/useClientOfflineSyncConflicts';
import { clientFormsKey } from '@/api/questionnaires';
import { subscriptionKeys } from '@/api/subscription';
import { weeklyCheckInKeys } from '@/hooks/useWeeklyCheckIns';
import { executeRefresh } from '@/lib/refresh';
import { RealtimeContext, setHubConnection, useSignalR, type RealtimeApi } from '@/hooks/useSignalR';

const RESTART_INITIAL_DELAY_MS = 2000;
const RESTART_MAX_DELAY_MS = 30000;

type Handler = (payload: unknown) => void;

/** Cooperation events that change what a coach sees in Pending, the notification popover or the Inbox badge. */
const COOPERATION_EVENTS = [
  'clientrequestreceived',
  'clientrequestcancelled',
  'inviteaccepted',
  'invitedeclined',
  'clientrequestproposalaccepted',
  'clientrequestproposaldeclined',
] as const;

/** Reads the optional client public id of a form event; null when absent or not a string. */
function readClientPublicId(payload: unknown): string | null {
  if (typeof payload !== 'object' || payload === null) return null;
  const value = (payload as Record<string, unknown>).clientPublicId;
  return typeof value === 'string' && value.length > 0 ? value : null;
}

interface SubscriptionChange {
  action: string;
  planNameCs: string;
  planNameEn: string;
  planNameDe: string;
}

/** Reads a `subscriptionchanged` payload; null when it is not an object with a string action. */
function readSubscriptionChange(payload: unknown): SubscriptionChange | null {
  if (typeof payload !== 'object' || payload === null) return null;
  const record = payload as Record<string, unknown>;
  if (typeof record.action !== 'string') return null;
  const text = (value: unknown) => (typeof value === 'string' ? value : '');
  return {
    action: record.action,
    planNameCs: text(record.planNameCs),
    planNameEn: text(record.planNameEn),
    planNameDe: text(record.planNameDe),
  };
}

/**
 * Owns the single SignalR connection of the signed-in portal session and lets
 * any component subscribe to its events through `useSignalR`. Connects when the
 * user is authenticated and disconnects on logout.
 */
export default function RealtimeProvider({ children }: { children: ReactNode }) {
  const isAuthenticated = useAuthStore((s) => s.isAuthenticated);
  const connectionRef = useRef<HubConnection | null>(null);
  const attachedRef = useRef<Set<string>>(new Set());
  const listenersRef = useRef<Map<string, Set<Handler>>>(new Map());
  const reconnectedRef = useRef<Set<() => void>>(new Set());

  const attach = useCallback((connection: HubConnection, key: string) => {
    if (attachedRef.current.has(key)) return;
    attachedRef.current.add(key);
    // The SignalR JS client lowercases method names, so keys are lowercase.
    connection.on(key, (payload: unknown) => {
      listenersRef.current.get(key)?.forEach((handler) => handler(payload));
    });
  }, []);

  const api = useMemo<RealtimeApi>(
    () => ({
      subscribe: (event, handler) => {
        const key = event.toLowerCase();
        let handlers = listenersRef.current.get(key);
        if (!handlers) {
          handlers = new Set();
          listenersRef.current.set(key, handlers);
        }
        handlers.add(handler);
        if (connectionRef.current) attach(connectionRef.current, key);
        return () => {
          listenersRef.current.get(key)?.delete(handler);
        };
      },
      subscribeReconnected: (callback) => {
        reconnectedRef.current.add(callback);
        return () => {
          reconnectedRef.current.delete(callback);
        };
      },
    }),
    [attach],
  );

  // Connect/disconnect based on auth state only (not token changes).
  // StrictMode runs effects twice (mount -> cleanup -> mount), so cancellation
  // is tracked to avoid "stopped during negotiation" errors.
  useEffect(() => {
    if (!isAuthenticated) return;

    let cancelled = false;

    const connection = new HubConnectionBuilder()
      .withUrl('/hubs/notifications', {
        accessTokenFactory: () => useAuthStore.getState().accessToken ?? '',
      })
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(LogLevel.Warning)
      .build();

    connectionRef.current = connection;
    attachedRef.current = new Set();
    for (const key of listenersRef.current.keys()) attach(connection, key);

    connection.onreconnected(() => {
      console.log('[SignalR] Reconnected');
      reconnectedRef.current.forEach((callback) => callback());
    });

    let timer: ReturnType<typeof setTimeout> | null = null;
    let starting = false;
    // Set once a start failed or the connection closed, so the next success refreshes the lists.
    let recovering = false;
    let delay = RESTART_INITIAL_DELAY_MS;

    const scheduleRestart = () => {
      if (cancelled || timer !== null || starting) return;
      recovering = true;
      const wait = delay;
      delay = Math.min(delay * 2, RESTART_MAX_DELAY_MS);
      timer = setTimeout(() => {
        timer = null;
        void startConnection();
      }, wait);
    };

    // Restarts the same connection object, so handlers and attachedRef stay valid.
    const startConnection = async () => {
      if (cancelled || starting || connection.state !== HubConnectionState.Disconnected) return;
      starting = true;
      try {
        await connection.start();
      } catch (err) {
        starting = false;
        if (cancelled) return;
        console.warn('[SignalR] Connection failed:', err);
        if (err instanceof HttpError && err.statusCode === 401) {
          // Token expired while the hub was down; the factory reads the refreshed one.
          try {
            await executeRefresh();
          } catch {
            // Refresh failed: stop retrying and let the normal logout flow take over.
            return;
          }
          if (cancelled) return;
        }
        scheduleRestart();
        return;
      }
      starting = false;
      // If cleanup ran mid-negotiation, stop now: stop() during negotiation logs an internal error.
      if (cancelled) {
        void connection.stop();
        return;
      }
      delay = RESTART_INITIAL_DELAY_MS;
      setHubConnection(connection);
      if (recovering) {
        recovering = false;
        console.log('[SignalR] Restarted');
        reconnectedRef.current.forEach((callback) => callback());
      }
    };

    connection.onclose(() => {
      if (cancelled) return;
      console.warn('[SignalR] Connection closed, restarting');
      scheduleRestart();
    });

    void startConnection();

    return () => {
      cancelled = true;
      if (timer !== null) {
        clearTimeout(timer);
        timer = null;
      }
      if (connectionRef.current === connection) connectionRef.current = null;
      setHubConnection(null);
      if (connection.state === HubConnectionState.Connected) {
        void connection.stop();
      }
    };
  }, [isAuthenticated, attach]);

  return (
    <RealtimeContext.Provider value={api}>
      <CoachRealtimeSync />
      {children}
    </RealtimeContext.Provider>
  );
}

/**
 * Refetches the coach-facing lists on realtime events (and after a reconnect).
 * A new message only touches the inbox lists and notifications (the open thread
 * is refreshed by `InboxPage`); cooperation events also refresh clients and Pending.
 */
function CoachRealtimeSync() {
  const queryClient = useQueryClient();
  const { t, i18n } = useTranslation();

  const invalidateInbox = useCallback(() => {
    void queryClient.invalidateQueries({ queryKey: ['notifications'] });
    void queryClient.invalidateQueries({ queryKey: ['conversations', 'filter-counts'] });
    void queryClient.invalidateQueries({ queryKey: ['conversations', 'list'] });
  }, [queryClient]);

  // Also sent when the backend updates an existing unread conflict notification.
  const invalidateConflicts = useCallback(() => {
    void queryClient.invalidateQueries({ queryKey: ['notifications'] });
    void queryClient.invalidateQueries({ queryKey: clientOfflineSyncConflictKeys.root });
  }, [queryClient]);

  const invalidateAll = useCallback(() => {
    invalidateInbox();
    void queryClient.invalidateQueries({ queryKey: clientOfflineSyncConflictKeys.root });
    void queryClient.invalidateQueries({ queryKey: ['clients', 'pending'] });
    // Carries the Pending tab's badge count.
    void queryClient.invalidateQueries({ queryKey: ['clients', 'list'] });
    // The review drawer, the request card and the open thread read these.
    void queryClient.invalidateQueries({ queryKey: ['clients', 'incoming-requests'] });
    void queryClient.invalidateQueries({ queryKey: ['conversations'] });
  }, [queryClient, invalidateInbox]);

  // A submitted or missed form changes the notification list and that client's forms views.
  const invalidateForms = useCallback(
    (payload: unknown) => {
      void queryClient.invalidateQueries({ queryKey: ['notifications'] });
      // The client Overview check-in trend reads the same responses.
      void queryClient.invalidateQueries({ queryKey: weeklyCheckInKeys.all });
      const clientPublicId = readClientPublicId(payload);
      void queryClient.invalidateQueries({
        queryKey: clientPublicId ? clientFormsKey(clientPublicId) : ['client-forms'],
      });
    },
    [queryClient],
  );

  // An ended collaboration frees a client place, so the plan's client usage is stale.
  const invalidateSubscription = useCallback(() => {
    void queryClient.invalidateQueries({ queryKey: subscriptionKeys.mine });
  }, [queryClient]);

  // Form Up changed, ended or stopped renewing the plan: refresh the plan views, the keep-clients
  // list and the bell. Only an ended plan interrupts the coach with a toast, and nothing navigates.
  const handleSubscriptionChanged = useCallback(
    (payload: unknown) => {
      void queryClient.invalidateQueries({ queryKey: subscriptionKeys.mine });
      void queryClient.invalidateQueries({ queryKey: subscriptionKeys.keepClients });
      void queryClient.invalidateQueries({ queryKey: ['notifications'] });
      const event = readSubscriptionChange(payload);
      if (event?.action !== 'ended') return;
      const lang = i18n.language.slice(0, 2).toLowerCase();
      const byLang = lang === 'cs' ? event.planNameCs : lang === 'de' ? event.planNameDe : event.planNameEn;
      const plan = byLang || event.planNameEn || event.planNameCs || event.planNameDe;
      useToastStore.getState().addToast(t('subscription.plan.endedByFormUp', { plan }), 'success');
    },
    [queryClient, t, i18n],
  );

  // Photo events carry no client id, so the client photo lists and request status refresh by prefix.
  const invalidatePhotos = useCallback(() => {
    void queryClient.invalidateQueries({ queryKey: ['client-photos'] });
    void queryClient.invalidateQueries({ queryKey: ['photo-diary-requests'] });
  }, [queryClient]);

  // A submitted request also stores a coach notification.
  const invalidatePhotosSubmitted = useCallback(() => {
    void queryClient.invalidateQueries({ queryKey: ['notifications'] });
    invalidatePhotos();
  }, [queryClient, invalidatePhotos]);

  // After a reconnect any missed event may have changed these lists too.
  const resync = useCallback(() => {
    invalidateAll();
    invalidatePhotosSubmitted();
  }, [invalidateAll, invalidatePhotosSubmitted]);

  const handlers = useMemo(
    () => ({
      ...Object.fromEntries(COOPERATION_EVENTS.map((event) => [event, invalidateAll])),
      collaborationended: invalidateSubscription,
      subscriptionchanged: handleSubscriptionChanged,
      newmessage: invalidateInbox,
      offlinesyncconflict: invalidateConflicts,
      questionnairesubmitted: invalidateForms,
      checkinmissed: invalidateForms,
      photodiarysubmitted: invalidatePhotosSubmitted,
      photodiaryphotouploaded: invalidatePhotos,
    }),
    [
      invalidateAll,
      invalidateInbox,
      invalidateConflicts,
      invalidateForms,
      invalidateSubscription,
      handleSubscriptionChanged,
      invalidatePhotosSubmitted,
      invalidatePhotos,
    ],
  );
  useSignalR(handlers, resync);

  return null;
}
