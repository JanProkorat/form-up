import { useRef } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';

import {
  CONVERSATIONS_QUERY_KEY,
  conversationMessagesQueryKey,
  sendMessage,
} from '@/api/conversations';
import { newClientId } from '@/lib/outbox/id';
import { resolveSendAttempt } from '@/lib/outbox/sendAttempt';
import type { SendAttempt } from '@/lib/outbox/sendAttempt';

/**
 * Sends one text message. The caller clears its draft only on success, so a failed send
 * keeps the text. A retry of the same text reuses one client message id, so the server
 * returns the original message instead of posting it twice.
 */
export function useSendMessage(conversationId: string) {
  const queryClient = useQueryClient();
  // The last attempt, kept so a retry of the same text reuses its id. Cleared on success.
  const lastAttempt = useRef<SendAttempt | null>(null);
  return useMutation({
    mutationFn: (text: string) => {
      const attempt = resolveSendAttempt(lastAttempt.current, text, newClientId);
      lastAttempt.current = attempt;
      return sendMessage(conversationId, attempt.text, attempt.clientMessageId);
    },
    onSuccess: () => {
      lastAttempt.current = null;
      void queryClient.invalidateQueries({ queryKey: conversationMessagesQueryKey(conversationId) });
      void queryClient.invalidateQueries({ queryKey: CONVERSATIONS_QUERY_KEY });
    },
  });
}
