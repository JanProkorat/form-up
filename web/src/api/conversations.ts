/**
 * Inbox/conversations API module (#1095).
 *
 * Wraps the NSwag-generated conversation endpoints. Thin pass-throughs —
 * the generated client already builds correct query strings/request
 * bodies, so there's nothing to reshape here (contrast `api/clients.ts`,
 * which normalises optional-array fields on the response).
 */
import { apiClient } from '@/api/client';
import type {
  ClientListFilter,
  ConversationDto,
  GenerateChatImageUploadUrlResponse,
  GetConversationFilterCountsResponse,
  GetMessagesResponse,
  ParticipantDto,
  SendMessageResponse,
} from '@/api/generated';

export type {
  ClientListFilter,
  ConversationDto,
  GenerateChatImageUploadUrlResponse,
  GetConversationFilterCountsResponse,
  GetMessagesResponse,
  ParticipantDto,
  SendMessageResponse,
};

/** GET /conversations — the caller's conversation list, newest activity first. */
export async function getConversations(archived: boolean, filter?: ClientListFilter): Promise<ConversationDto[]> {
  return apiClient.getConversationsEndpoint(archived, filter);
}

/** GET /conversations/filter-counts — per-chip counts for the filter dropdown. Trainer/Nutritionist only. */
export async function getConversationFilterCounts(): Promise<GetConversationFilterCountsResponse> {
  return apiClient.getConversationFilterCountsEndpoint();
}

/**
 * POST /conversations — gets or creates a conversation with the given
 * participant (a `ClientProfile.PublicId`). 404s with `NOT_LINKED_TO_CLIENT`
 * when no conversation exists yet and the caller holds no live link.
 */
export async function startConversation(participantId: string): Promise<ConversationDto> {
  return apiClient.startConversationEndpoint({ participantId });
}

export interface GetMessagesParams {
  /** Last message's id from the previous page — omit for the newest page. */
  cursor?: string;
  limit?: number;
}

/** GET /conversations/{id}/messages — newest-first, cursor-paginated. */
export async function getMessages(conversationId: string, params: GetMessagesParams = {}): Promise<GetMessagesResponse> {
  return apiClient.getMessagesEndpoint(conversationId, params.limit, params.cursor);
}

export interface SendMessagePayload {
  /** ≤4000 chars, validated server-side. Optional when `imageUploadId` is set (image-only message). */
  text?: string;
  /** References a staged upload from `requestChatImageUploadUrl`. Omit for a text-only message. */
  imageUploadId?: string;
  /** Client-reported layout hint, read from the file before upload. Never trusted for security. */
  imageWidth?: number;
  /** Client-reported layout hint, read from the file before upload. Never trusted for security. */
  imageHeight?: number;
  /** Retry-safe send id (GUID): a repeat with the same id returns the original message instead of a new one. */
  clientMessageId?: string;
}

/** POST /conversations/{id}/messages — text, an image, or both. */
export async function sendMessage(conversationId: string, payload: SendMessagePayload): Promise<SendMessageResponse> {
  return apiClient.sendMessageEndpoint(conversationId, payload);
}

export interface ChatImageUploadUrlPayload {
  contentType: string;
  sizeBytes: number;
}

/**
 * POST /conversations/{id}/messages/image-upload-url — mints a short-lived pre-signed PUT URL
 * plus the `uploadId` to reference from `sendMessage`'s `imageUploadId`. The caller PUTs the raw
 * file bytes to `uploadUrl` directly (not through this API client — see Composer.tsx).
 */
export async function requestChatImageUploadUrl(
  conversationId: string,
  payload: ChatImageUploadUrlPayload,
): Promise<GenerateChatImageUploadUrlResponse> {
  return apiClient.generateChatImageUploadUrlEndpoint(conversationId, payload);
}

/** POST /conversations/{id}/read — marks every message from the other party read. */
export async function markConversationRead(conversationId: string): Promise<void> {
  await apiClient.markConversationReadEndpoint(conversationId);
}
