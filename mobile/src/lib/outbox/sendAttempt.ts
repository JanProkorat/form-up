/** One outgoing message attempt: the trimmed text and the idempotency id sent with it. */
export type SendAttempt = { text: string; clientMessageId: string };

/**
 * Picks the attempt for a send: a retry of the same trimmed text reuses the failed attempt's id,
 * anything else (first send, edited text, previous send succeeded) gets a fresh id.
 */
export function resolveSendAttempt(
  previous: SendAttempt | null,
  text: string,
  newId: () => string,
): SendAttempt {
  const trimmed = text.trim();
  if (previous && previous.text === trimmed) return previous;
  return { text: trimmed, clientMessageId: newId() };
}
