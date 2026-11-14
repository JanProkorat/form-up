import { useEffect, useMemo, useRef, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { useQueryClient } from '@tanstack/react-query';
import ConversationList from '@/components/inbox/ConversationList';
import ThreadPane from '@/components/inbox/ThreadPane';
import ThreadEmptyState from '@/components/inbox/ThreadEmptyState';
import ClientSidePanel from '@/components/inbox/ClientSidePanel';
import InvitedClientPanel from '@/components/inbox/panel/InvitedClientPanel';
import PendingClientPanel from '@/components/inbox/panel/PendingClientPanel';
import { getPanelKind } from '@/lib/cooperationCards';
import { getConversations } from '@/api/conversations';
import {
  useConversationFilterCounts,
  useConversations,
  useMarkConversationRead,
  useStartConversation,
} from '@/hooks/useInboxQueries';
import { useSignalR } from '@/hooks/useSignalR';
import { useSubscription } from '@/hooks/useSubscriptionQueries';
import { isSubscriptionRequired } from '@/lib/subscriptionErrors';
import PaywallDialog from '@/components/subscription/PaywallDialog';
import { useMediaQuery } from '@/hooks/useMediaQuery';
import { cn } from '@/lib/utils';
import { useAuthStore } from '@/stores/auth';
import { ClientListFilter, type ConversationDto } from '@/api/generated';

/** At and above this width the client panel sits beside the list and thread; below it, when open, it replaces the list. */
const DOCKED_PANEL_QUERY = '(min-width: 1280px)';
const TYPING_INDICATOR_TIMEOUT_MS = 3500;
/** Debounces the mark-read call for messages landing in the already-open thread,
 * so a burst (several messages in a row) collapses into one request. */
const OPEN_THREAD_MARK_READ_DEBOUNCE_MS = 800;

interface NewMessagePayload {
  conversationId: string;
  /** Present on every push; a plain-text send omits `eventType` (implicitly Text/null). Unused
   * here beyond typing the payload — the broad invalidation below already refreshes the thread's
   * messages and the list's `lastMessageEventType` preview for a cooperation-event push exactly
   * as it does for a text one. */
  kind?: string;
  eventType?: string;
}

interface TypingPayload {
  conversationId: string;
  senderId: string;
}

interface UserPresencePayload {
  userId: string;
  isOnline: boolean;
}

/**
 * The inbox page (#1095): a three-pane layout (conversation list, thread,
 * optional client panel) that escapes AppShell's page padding via a
 * negative margin so the panes butt up against the viewport edges, per
 * docs/design/1095/inbox-inventory.md. AppShell/Sidebar are #1073's and
 * out of scope — see the design inventory's provenance note on why the
 * pixel sizes here are estimates, repo tokens win.
 */
export default function InboxPage() {
  const { t } = useTranslation();
  const [searchParams, setSearchParams] = useSearchParams();
  const queryClient = useQueryClient();
  const currentUserId = useAuthStore((s) => s.user?.publicId);

  const [archived, setArchived] = useState(false);
  const [filter, setFilter] = useState<ClientListFilter>(ClientListFilter.All);
  const [search, setSearch] = useState('');
  const [selectedParticipantId, setSelectedParticipantId] = useState<string | undefined>(undefined);
  // null = the user has not toggled yet: open at the wide layout, closed below it.
  const [clientPanelOverride, setClientPanelOverride] = useState<boolean | null>(null);
  const isPanelDocked = useMediaQuery(DOCKED_PANEL_QUERY);
  const showClientPanel = clientPanelOverride ?? isPanelDocked;
  const [isOtherPartyTyping, setIsOtherPartyTyping] = useState(false);
  const typingTimeoutRef = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const markReadDebounceRef = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const deepLinkHandledRef = useRef(false);
  /** True while the open thread was preselected on entry and the trainer has not engaged with it yet. */
  const [isAutoSelected, setIsAutoSelected] = useState(false);
  const [paywallOpen, setPaywallOpen] = useState(false);
  const { isEntitled } = useSubscription();

  const conversationsQuery = useConversations(archived, filter);
  const countsQuery = useConversationFilterCounts();
  const startConversationMutation = useStartConversation();
  const markReadMutation = useMarkConversationRead();

  const conversations = useMemo(() => conversationsQuery.data ?? [], [conversationsQuery.data]);
  const selectedConversation = conversations.find((c) => c.participant?.id === selectedParticipantId);

  // Reset the typing indicator whenever the open thread changes.
  useEffect(() => {
    setIsOtherPartyTyping(false);
    if (typingTimeoutRef.current) {
      clearTimeout(typingTimeoutRef.current);
    }
  }, [selectedConversation?.id]);

  // Cancel any in-flight debounced mark-read call on unmount.
  useEffect(
    () => () => {
      if (markReadDebounceRef.current) {
        clearTimeout(markReadDebounceRef.current);
      }
    },
    [],
  );

  // `?client=<publicId>` deep-link entry from the client-detail page's Chat
  // button: select the matching row if a conversation already exists,
  // otherwise start one. An unknown/unlinked id surfaces as a toast via
  // useStartConversation's onError — never a silently empty thread.
  useEffect(() => {
    const clientParam = searchParams.get('client');
    if (!clientParam || deepLinkHandledRef.current || conversationsQuery.isPending) {
      return;
    }
    deepLinkHandledRef.current = true;

    const existingRow = conversations.find((c) => c.participant?.clientPublicId === clientParam);
    if (existingRow?.id) {
      setIsAutoSelected(false);
      setSelectedParticipantId(existingRow.participant?.id);
    } else if (!isEntitled('canMessage')) {
      // Starting a conversation is a coach send: explain with the paywall instead of a refused request.
      setPaywallOpen(true);
    } else {
      startConversationMutation.mutate(clientParam, {
        onError: (error) => {
          if (isSubscriptionRequired(error)) {
            setPaywallOpen(true);
          }
        },
        onSuccess: async (conversation) => {
          setIsAutoSelected(false);
          setSelectedParticipantId(conversation.participant?.id);
          // POST /conversations gets-or-creates: the returned conversation may
          // already exist but be archived, and an archived thread never shows
          // up in the Active-filtered list `existingRow` just searched. Probe
          // the archived list and switch views instead of landing on a silent
          // empty state (a freshly-created conversation is always active, so
          // it simply won't match here and `archived` stays put).
          if (!archived && conversation.id) {
            const archivedConversations = await queryClient.fetchQuery({
              queryKey: ['conversations', 'list', true, ClientListFilter.All],
              queryFn: () => getConversations(true, ClientListFilter.All),
            });
            if (archivedConversations.some((c) => c.id === conversation.id)) {
              setArchived(true);
            }
          }
        },
      });
    }

    setSearchParams(
      (previous) => {
        const next = new URLSearchParams(previous);
        next.delete('client');
        return next;
      },
      { replace: true },
    );
    // eslint-disable-next-line react-hooks/exhaustive-deps -- runs once conversations resolve; deepLinkHandledRef guards re-entry
  }, [conversationsQuery.isPending]);

  // Preselect the first chat once the list loads. Only when nothing is selected, and never while a
  // `?client=` deep link is pending or a start-conversation call is in flight (those own selection).
  const hasPendingDeepLink = searchParams.has('client');
  useEffect(() => {
    if (
      selectedParticipantId !== undefined ||
      hasPendingDeepLink ||
      conversationsQuery.isPending ||
      startConversationMutation.isPending
    ) {
      return;
    }
    const firstParticipantId = conversations[0]?.participant?.id;
    if (firstParticipantId) {
      setIsAutoSelected(true);
      setSelectedParticipantId(firstParticipantId);
    }
  }, [conversations, conversationsQuery.isPending, hasPendingDeepLink, selectedParticipantId, startConversationMutation.isPending]);

  function handleSelectConversation(conversation: (typeof conversations)[number]) {
    setIsAutoSelected(false);
    setSelectedParticipantId(conversation.participant?.id);
  }

  const signalRHandlers = useMemo(
    () => ({
      newmessage: (payload: unknown) => {
        const { conversationId } = payload as NewMessagePayload;
        queryClient.invalidateQueries({ queryKey: ['conversations', conversationId, 'messages'] });
        queryClient.invalidateQueries({ queryKey: ['conversations', 'list'] });
        queryClient.invalidateQueries({ queryKey: ['conversations', 'filter-counts'] });

        // A message landing in the thread that's already open would otherwise
        // re-light its unread dot until the reader re-opens it. Debounced so a
        // burst of messages collapses into one mark-read call.
        // Skipped while the thread is only auto-selected: the trainer has not read it yet.
        if (conversationId === selectedConversation?.id && !isAutoSelected) {
          if (markReadDebounceRef.current) {
            clearTimeout(markReadDebounceRef.current);
          }
          markReadDebounceRef.current = setTimeout(() => {
            markReadMutation.mutate(conversationId);
          }, OPEN_THREAD_MARK_READ_DEBOUNCE_MS);
        }
      },
      typing: (payload: unknown) => {
        const { conversationId, senderId } = payload as TypingPayload;
        if (conversationId !== selectedConversation?.id || senderId === currentUserId) {
          return;
        }
        setIsOtherPartyTyping(true);
        if (typingTimeoutRef.current) {
          clearTimeout(typingTimeoutRef.current);
        }
        typingTimeoutRef.current = setTimeout(() => setIsOtherPartyTyping(false), TYPING_INDICATOR_TIMEOUT_MS);
      },
      userPresence: (payload: unknown) => {
        const { userId, isOnline } = payload as UserPresencePayload;
        // Patch the affected participant's online flag in every cached list
        // variant (Active/Archived x each filter chip) instead of refetching
        // the whole roster on every presence flicker.
        queryClient.setQueriesData<ConversationDto[]>({ queryKey: ['conversations', 'list'] }, (previous) =>
          previous?.map((conversation) => {
            if (!conversation.participant || conversation.participant.id !== userId) {
              return conversation;
            }
            return { ...conversation, participant: { ...conversation.participant, online: isOnline } };
          }),
        );
      },
      conversationunarchived: () => {
        queryClient.invalidateQueries({ queryKey: ['conversations', 'list'] });
      },
    }),
    // markReadMutation.mutate is the stable bound function off useMutation's
    // observer; the wrapping object is a fresh reference every render, so
    // depending on the whole object would re-register the handlers on every render.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [queryClient, selectedConversation?.id, currentUserId, markReadMutation.mutate, isAutoSelected],
  );
  useSignalR(signalRHandlers);

  const selectedClientPublicId = selectedConversation?.participant?.clientPublicId;
  const selectedCooperation = selectedConversation?.cooperation;
  const panelKind = getPanelKind(selectedCooperation, selectedClientPublicId);
  const isClientPanelOpen = showClientPanel && panelKind !== null;
  const selectedName = selectedConversation?.participant?.name ?? '';

  // Below the wide layout there is no room for list, thread and panel together, so an open
  // panel takes the list's place.
  const showConversationList = isPanelDocked || !isClientPanelOpen;

  return (
    <div className="-m-6 flex h-screen overflow-hidden bg-background">
      {showConversationList && (
        <ConversationList
          archived={archived}
          onArchivedChange={setArchived}
          search={search}
          onSearchChange={setSearch}
          filter={filter}
          onFilterChange={setFilter}
          counts={countsQuery.data}
          conversations={conversations}
          isPending={conversationsQuery.isPending}
          isError={conversationsQuery.isError}
          onRetry={() => void conversationsQuery.refetch()}
          selectedParticipantId={selectedParticipantId}
          onSelectConversation={handleSelectConversation}
        />
      )}

      {selectedConversation?.id && selectedConversation.participant ? (
        <ThreadPane
          conversationId={selectedConversation.id}
          participant={selectedConversation.participant}
          showClientPanel={isClientPanelOpen}
          onToggleClientPanel={() => setClientPanelOverride(!showClientPanel)}
          isOtherPartyTyping={isOtherPartyTyping}
          isSendLocked={selectedConversation.isSendLocked ?? false}
          isPlanLocked={!isEntitled('canMessage') && Boolean(selectedConversation.participant.clientPublicId)}
          isFormer={selectedConversation.isFormer ?? false}
          cooperation={selectedCooperation}
          deferMarkRead={isAutoSelected}
          onEngage={() => setIsAutoSelected(false)}
        />
      ) : (
        <ThreadEmptyState />
      )}

      {panelKind && (
        <aside
          aria-label={t('inbox.thread.clientPanelTitle')}
          aria-hidden={!isClientPanelOpen}
          inert={!isClientPanelOpen}
          className={cn(
            'h-full shrink-0 overflow-hidden bg-card transition-[width] duration-300 ease-out motion-reduce:transition-none',
            isClientPanelOpen ? 'w-panel border-l border-border' : 'w-0',
          )}
        >
          <div className="flex h-full w-panel flex-col">
            {panelKind === 'pending' && selectedCooperation?.sourceId ? (
              <PendingClientPanel name={selectedName} sourceId={selectedCooperation.sourceId} clientPublicId={selectedClientPublicId} />
            ) : panelKind === 'invited' && selectedCooperation?.sourceId ? (
              <InvitedClientPanel name={selectedName} sourceId={selectedCooperation.sourceId} />
            ) : selectedClientPublicId ? (
              <ClientSidePanel clientPublicId={selectedClientPublicId} participantName={selectedName} />
            ) : null}
          </div>
        </aside>
      )}

      <PaywallDialog open={paywallOpen} onOpenChange={setPaywallOpen} feature="chat" />
    </div>
  );
}
