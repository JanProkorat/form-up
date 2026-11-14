import { useEffect, useMemo, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import Composer from '@/components/inbox/Composer';
import PlanLockedComposer from '@/components/inbox/PlanLockedComposer';
import DateSeparator from '@/components/inbox/DateSeparator';
import EventBanner from '@/components/inbox/EventBanner';
import MessageBubble from '@/components/inbox/MessageBubble';
import ThreadHeader from '@/components/inbox/ThreadHeader';
import TypingIndicator from '@/components/inbox/TypingIndicator';
import { useConversationMessages, useMarkConversationRead, useSendMessage } from '@/hooks/useInboxQueries';
import { invokeHub } from '@/hooks/useSignalR';
import { useAuthStore } from '@/stores/auth';
import { foldThreadCards } from '@/lib/cooperationCards';
import { ChatMessageKind } from '@/api/generated';
import type { ConversationCooperationDto, MessageDto, ParticipantDto } from '@/api/generated';

const SCROLL_TOP_THRESHOLD_PX = 80;
/** How close to the bottom counts as "already reading the latest" — below this, an
 * arriving/sent message auto-scrolls into view; above it, the reader is left alone. */
const SCROLL_BOTTOM_THRESHOLD_PX = 120;
const TYPING_THROTTLE_MS = 2000;

interface Props {
  conversationId: string;
  participant: ParticipantDto;
  showClientPanel: boolean;
  onToggleClientPanel: () => void;
  isOtherPartyTyping: boolean;
  /** `ConversationDto.isSendLocked` — the server would refuse a send here. */
  isSendLocked?: boolean;
  /** The coach's plan has no chat, so sending is refused; the composer shows the paywall notice. */
  isPlanLocked?: boolean;
  isFormer?: boolean;
  /** The conversation's newest invite or request; supplies an invite card's requested scope. */
  cooperation?: ConversationCooperationDto;
  /** True while the thread was auto-selected: it is shown but not marked read until the trainer engages. */
  deferMarkRead?: boolean;
  /** Called on the first user-initiated input inside the pane while `deferMarkRead` is set. */
  onEngage?: () => void;
}

/** True when `message` starts a new calendar day relative to `previous` (or there is no previous message). */
function startsNewDay(message: MessageDto, previous: MessageDto | undefined): boolean {
  if (!message.timestamp) {
    return false;
  }
  if (!previous?.timestamp) {
    return true;
  }
  return new Date(message.timestamp).toDateString() !== new Date(previous.timestamp).toDateString();
}

/**
 * The thread pane: header, the scrollable message list (oldest at top,
 * newest at bottom, date separators between calendar days, older pages
 * loaded on scroll-up), the typing indicator, and the composer.
 */
export default function ThreadPane({
  conversationId,
  participant,
  showClientPanel,
  onToggleClientPanel,
  isOtherPartyTyping,
  isSendLocked = false,
  isPlanLocked = false,
  isFormer = false,
  cooperation,
  deferMarkRead = false,
  onEngage,
}: Props) {
  const { t } = useTranslation();
  const user = useAuthStore((s) => s.user);
  const scrollRef = useRef<HTMLDivElement>(null);
  /** Wraps the message list's actual content (loading indicator + bubbles).
   * `ResizeObserver` watches this, not `scrollRef` itself — the scroll
   * container's own border box is pinned by the flex layout and never
   * resizes when `scrollHeight` grows; only its content does. */
  const contentRef = useRef<HTMLDivElement>(null);
  const lastTypingSentAtRef = useRef(0);
  const previousConversationIdRef = useRef<string | undefined>(undefined);
  /** The conversation id for which the one-time "jump to newest" has already run. */
  const initializedConversationIdRef = useRef<string | undefined>(undefined);
  /** Id of the newest loaded message, used to tell "a message was appended" apart
   * from "an older page was prepended" (both change the `messages` array). */
  const lastMessageIdRef = useRef<string | undefined>(undefined);
  /** Whether the reader was scrolled near the bottom the last time they scrolled —
   * gates auto-scroll on arrival/send so it never yanks someone reading history. */
  const isNearBottomRef = useRef(true);
  /** Set right before `fetchNextPage()` for an older page; restored once that page
   * lands so the viewport doesn't jump (invariant: scrollHeight - scrollTop). */
  const pendingOlderPageAnchorRef = useRef<number | null>(null);
  const wasFetchingNextPageRef = useRef(false);
  /** The conversation id already marked read, so each conversation is marked once. */
  const markedReadConversationIdRef = useRef<string | undefined>(undefined);

  const messagesQuery = useConversationMessages(conversationId);
  const markReadMutation = useMarkConversationRead();
  const sendMessageMutation = useSendMessage(conversationId);

  const messages = useMemo(
    () => (messagesQuery.data?.pages.flatMap((page) => page.items ?? []) ?? []).reverse(),
    [messagesQuery.data],
  );
  // Accepted/Declined/Withdrawn events are folded into the card they answer, so they are not rendered on their own.
  const { visible: visibleMessages, followUps, sourceFollowUps } = useMemo(() => foldThreadCards(messages), [messages]);

  // Entrance animation: a message is "new" when it sits after the newest message that was
  // known when the thread was opened (or last rendered). Older pages are prepended, so they
  // never land after it; the first load of a conversation seeds the marker and animates nothing.
  const knownRef = useRef<{ conversationId: string; initialized: boolean; newestId: string | undefined }>({
    conversationId,
    initialized: false,
    newestId: undefined,
  });
  if (knownRef.current.conversationId !== conversationId) {
    knownRef.current = { conversationId, initialized: false, newestId: undefined };
  }
  if (!knownRef.current.initialized && messagesQuery.isSuccess) {
    knownRef.current.initialized = true;
    knownRef.current.newestId = messages[messages.length - 1]?.id;
  }
  const newMessageIds = useMemo(() => {
    const known = knownRef.current;
    const ids = new Set<string>();
    if (!known.initialized) {
      return ids;
    }
    const knownIndex = known.newestId ? messages.findIndex((m) => m.id === known.newestId) : -1;
    if (known.newestId && knownIndex === -1) {
      return ids;
    }
    for (const m of messages.slice(knownIndex + 1)) {
      if (m.id) {
        ids.add(m.id);
      }
    }
    return ids;
  }, [messages]);
  useEffect(() => {
    if (knownRef.current.initialized && messages.length > 0) {
      knownRef.current.newestId = messages[messages.length - 1]?.id;
    }
  }, [messages]);

  // Reset per-conversation scroll state on open. The actual "jump to newest" scroll
  // happens below, once the first page has data — doing it here raced the fetch and
  // left a cold-cache thread stranded at its oldest loaded message (#1099 review finding).
  useEffect(() => {
    if (previousConversationIdRef.current !== conversationId) {
      previousConversationIdRef.current = conversationId;
      lastMessageIdRef.current = undefined;
      isNearBottomRef.current = true;
    }
  }, [conversationId]);

  // Mark read once per conversation, as soon as it is not deferred (an auto-selected
  // thread waits for the trainer's first interaction).
  useEffect(() => {
    if (deferMarkRead || markedReadConversationIdRef.current === conversationId) {
      return;
    }
    markedReadConversationIdRef.current = conversationId;
    markReadMutation.mutate(conversationId);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- intentionally only re-runs when the conversation or deferral changes, not on every mutate identity change
  }, [conversationId, deferMarkRead]);

  // User-initiated input only; `scroll` is excluded because the pane scrolls itself to the newest message.
  function handleEngage() {
    if (deferMarkRead) {
      onEngage?.();
    }
  }

  // Scroll to the newest message once the first page for this conversation has
  // rendered, and again whenever a genuinely new message is appended (a realtime
  // push or a just-sent message) while the reader was already near the bottom.
  // An older page loaded via scroll-up never touches the newest message's id, so
  // it doesn't trigger this — that case is handled by the anchor-restore effect below.
  useEffect(() => {
    if (messages.length === 0) {
      return;
    }
    const newestMessageId = messages[messages.length - 1]?.id;
    const isFirstLoadForThisConversation = initializedConversationIdRef.current !== conversationId;
    const isNewestMessageChanged = newestMessageId !== lastMessageIdRef.current;
    lastMessageIdRef.current = newestMessageId;

    if (!isFirstLoadForThisConversation && !isNewestMessageChanged) {
      return;
    }

    if (isFirstLoadForThisConversation || isNearBottomRef.current) {
      initializedConversationIdRef.current = conversationId;
      isNearBottomRef.current = true;
      requestAnimationFrame(() => {
        scrollRef.current?.scrollTo({
          top: scrollRef.current.scrollHeight,
          behavior: isFirstLoadForThisConversation ? 'auto' : 'smooth',
        });
      });
    }
  }, [messages, conversationId]);

  // Restore the scroll anchor once an older page (loaded via scroll-up) lands,
  // so the message the reader was looking at doesn't jump under them.
  useEffect(() => {
    const justFinishedLoadingOlderPage = wasFetchingNextPageRef.current && !messagesQuery.isFetchingNextPage;
    wasFetchingNextPageRef.current = messagesQuery.isFetchingNextPage;

    if (!justFinishedLoadingOlderPage || pendingOlderPageAnchorRef.current === null) {
      return;
    }
    const anchor = pendingOlderPageAnchorRef.current;
    pendingOlderPageAnchorRef.current = null;
    requestAnimationFrame(() => {
      const el = scrollRef.current;
      if (el) {
        el.scrollTop = el.scrollHeight - anchor;
      }
    });
  }, [messagesQuery.isFetchingNextPage]);

  // Keep the list pinned to the bottom when its content grows after the
  // initial render — a chat image or a YouTube preview card that has no
  // declared dimensions reserves no space up front, so it can still widen
  // its row once it finishes loading, after the "jump to newest" scroll
  // above already ran. Only re-pins while the reader is already at the
  // bottom (isNearBottomRef), so it never yanks someone reading history,
  // and it never fights the older-page anchor-restore effect above, which
  // runs precisely when the reader has scrolled up and isNearBottomRef is
  // false. Setting scrollTop to scrollHeight lands exactly at the bottom,
  // so the scroll event handleScroll fires from also keeps isNearBottomRef
  // true — it doesn't need a separate guard against flipping it false.
  useEffect(() => {
    const content = contentRef.current;
    const container = scrollRef.current;
    if (!content || !container) {
      return;
    }

    const observer = new ResizeObserver(() => {
      if (!isNearBottomRef.current) {
        return;
      }
      container.scrollTop = container.scrollHeight;
    });
    observer.observe(content);

    return () => observer.disconnect();
  }, [conversationId]);

  function handleScroll() {
    const el = scrollRef.current;
    if (!el) {
      return;
    }
    isNearBottomRef.current = el.scrollHeight - el.scrollTop - el.clientHeight <= SCROLL_BOTTOM_THRESHOLD_PX;

    if (el.scrollTop > SCROLL_TOP_THRESHOLD_PX) {
      return;
    }
    if (messagesQuery.hasNextPage && !messagesQuery.isFetchingNextPage) {
      pendingOlderPageAnchorRef.current = el.scrollHeight - el.scrollTop;
      void messagesQuery.fetchNextPage();
    }
  }

  function handleTyping() {
    const now = Date.now();
    if (now - lastTypingSentAtRef.current < TYPING_THROTTLE_MS) {
      return;
    }
    lastTypingSentAtRef.current = now;
    invokeHub('SendTyping', conversationId);
  }

  const ownInitials = user ? `${user.firstName[0] ?? ''}${user.lastName[0] ?? ''}`.toUpperCase() : '';

  return (
    <div
      className="flex h-full min-w-0 flex-1 flex-col bg-background"
      onPointerDownCapture={handleEngage}
      onWheelCapture={handleEngage}
      onTouchStartCapture={handleEngage}
      onKeyDownCapture={handleEngage}
      onFocusCapture={handleEngage}
    >
      <ThreadHeader participant={participant} showClientPanel={showClientPanel} onToggleClientPanel={onToggleClientPanel} isFormer={isFormer} />

      <div ref={scrollRef} onScroll={handleScroll} data-testid="thread-message-list" className="flex-1 overflow-y-auto px-6 py-5">
        <div ref={contentRef} className="flex flex-col gap-3">
          {messagesQuery.isFetchingNextPage && (
            <p className="py-1 text-center text-caption text-muted-foreground">{t('inbox.thread.loadingOlder')}</p>
          )}
          {visibleMessages.map((message, index) => {
            const isOwn = Boolean(user) && message.senderId === user?.publicId;
            const sourceId = message.eventSourceId ?? undefined;
            const isNew = Boolean(message.id) && newMessageIds.has(message.id as string);
            const enterClass = !isNew
              ? ''
              : message.kind === ChatMessageKind.Event
                ? 'animate-message-in-up'
                : isOwn
                  ? 'animate-message-in-right'
                  : 'animate-message-in-left';
            return (
              <div
                key={message.id ?? index}
                className={`flex flex-col gap-3 ${enterClass}`}
                data-message-enter={isNew ? '' : undefined}
              >
                {startsNewDay(message, visibleMessages[index - 1]) && message.timestamp && <DateSeparator iso={message.timestamp} />}
                {message.kind === ChatMessageKind.Event ? (
                  <EventBanner
                    message={message}
                    isOwn={isOwn}
                    participantName={participant.name ?? ''}
                    followUp={message.id ? followUps.get(message.id) : undefined}
                    sourceFollowUp={sourceId ? sourceFollowUps.get(sourceId) : undefined}
                    requestedScope={sourceId && sourceId === cooperation?.sourceId ? cooperation.requestedScope : undefined}
                  />
                ) : (
                  <MessageBubble
                    message={message}
                    isOwn={isOwn}
                    otherParticipant={participant}
                    ownInitials={ownInitials}
                    ownAvatarBlobUrl={user?.avatarBlobUrl ?? undefined}
                  />
                )}
              </div>
            );
          })}
        </div>
      </div>

      {isOtherPartyTyping && <TypingIndicator />}

      {isPlanLocked ? (
        <PlanLockedComposer />
      ) : (
        <Composer
          conversationId={conversationId}
          onSend={async (payload) => {
            await sendMessageMutation.mutateAsync(payload);
          }}
          isSending={sendMessageMutation.isPending}
          onTyping={handleTyping}
          isLocked={isSendLocked}
        />
      )}
    </div>
  );
}
