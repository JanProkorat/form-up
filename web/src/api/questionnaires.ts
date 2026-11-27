import api from '@/lib/api';
import { apiClient } from './client';
import {
  FormResponseState,
  QuestionnaireResponseKind,
  QuestionnaireType,
  type ClientFormAnswer,
  type ClientFormItem,
  type GetClientFormsResponse,
} from './generated';

export { FormResponseState, QuestionnaireResponseKind, QuestionnaireType };
export type { ClientFormAnswer, ClientFormItem, GetClientFormsResponse };

export interface QuestionDto {
  publicId: string;
  orderIndex: number;
  type: string;
  label: string;
  helperText?: string | null;
  isRequired: boolean;
  isHidden: boolean;
  config?: string | null;
  mappedField?: string | null;
}

export interface QuestionnaireDto {
  publicId: string;
  title: string;
  description?: string | null;
  type: QuestionnaireType;
  isActive: boolean;
  isDefault: boolean;
  questions: QuestionDto[];
}

export interface QuestionnaireSummaryDto {
  publicId: string;
  title: string;
  description?: string | null;
  type: QuestionnaireType;
  isActive: boolean;
  isDefault: boolean;
  questionCount: number;
  /** Distinct clients with a non-cancelled response to this form. */
  usedForClientCount: number;
  /** Whole percent of last week's check-ins that were answered; absent for other forms or when none were sent. */
  lastWeekAnswerRatePercent?: number | null;
  dateCreated: string;
  /** Last edit time: the update timestamp, or the creation time when never edited. */
  editedAt: string;
}

export interface UpdateQuestionDto {
  publicId?: string | null;
  orderIndex: number;
  type: string;
  label: string;
  helperText?: string | null;
  isRequired: boolean;
  isHidden: boolean;
  config?: string | null;
  mappedField?: string | null;
}

interface GetTrainerQuestionnairesResponse {
  questionnaires: QuestionnaireSummaryDto[];
}

export interface TrainerQuestionnaireFilters {
  type?: QuestionnaireType;
  search?: string;
}

export async function getTrainerQuestionnaires(
  filters: TrainerQuestionnaireFilters = {},
): Promise<QuestionnaireSummaryDto[]> {
  const { data } = await api.get<GetTrainerQuestionnairesResponse>('/trainer/questionnaires', {
    params: { type: filters.type, search: filters.search || undefined },
  });
  return data.questionnaires;
}

export async function getTrainerQuestionnaire(publicId: string): Promise<QuestionnaireDto> {
  const { data } = await api.get<QuestionnaireDto>(`/trainer/questionnaires/${publicId}`);
  return data;
}

export async function createQuestionnaire(
  title: string,
  description?: string,
  type: QuestionnaireType = QuestionnaireType.Onboarding,
): Promise<QuestionnaireDto> {
  const { data } = await api.post<QuestionnaireDto>('/trainer/questionnaires', { title, description, type });
  return data;
}

export async function updateQuestionnaire(publicId: string, payload: {
  title: string;
  description?: string | null;
  type?: QuestionnaireType;
  isActive: boolean;
  isDefault: boolean;
  questions: UpdateQuestionDto[];
}): Promise<QuestionnaireDto> {
  const { data } = await api.put<QuestionnaireDto>(`/trainer/questionnaires/${publicId}`, { publicId, ...payload });
  return data;
}

export async function deleteQuestionnaire(publicId: string): Promise<void> {
  await api.delete(`/trainer/questionnaires/${publicId}`);
}

export interface ResponseAnswerDto {
  questionPublicId: string;
  questionLabel: string;
  questionType: string;
  mappedField?: string | null;
  valueText?: string | null;
  valueNumber?: number | null;
  valueJson?: string | null;
  fileUrl?: string | null;
}

export interface ClientResponseDto {
  responsePublicId: string;
  questionnaireTitle: string;
  submittedAt?: string | null;
  answerCount: number;
  answers: ResponseAnswerDto[];
}

export interface ClientResponseItem {
  responsePublicId: string;
  questionnaireTitle: string;
  status: string;
  submittedAt?: string | null;
  dateCreated: string;
  answerCount: number;
  answers: ResponseAnswerDto[];
}

export interface ClientResponsesDto {
  responses: ClientResponseItem[];
}

export async function assignQuestionnaire(clientPublicId: string, questionnairePublicId: string): Promise<void> {
  await api.post<void>(`/trainer/clients/${clientPublicId}/assign-questionnaire`, { questionnairePublicId });
}

export async function cancelQuestionnaire(clientPublicId: string): Promise<void> {
  await api.post<void>(`/trainer/clients/${clientPublicId}/cancel-questionnaire`);
}

export async function replaceQuestionnaire(clientPublicId: string, questionnairePublicId: string): Promise<void> {
  await api.post<void>(`/trainer/clients/${clientPublicId}/replace-questionnaire`, { questionnairePublicId });
}

/** @deprecated Use getClientQuestionnaireResponses (plural) instead */
export async function getClientQuestionnaireResponse(clientId: string): Promise<ClientResponseDto> {
  const { data } = await api.get<ClientResponseDto>(`/trainer/clients/${clientId}/questionnaire-response`);
  return data;
}

export async function getClientQuestionnaireResponses(clientId: string): Promise<ClientResponsesDto> {
  const { data } = await api.get<ClientResponsesDto>(`/trainer/clients/${clientId}/questionnaire-responses`);
  return data;
}

/** Cache key of a client's forms; shared by every screen that lists them. */
export const clientFormsKey = (clientId: string) => ['client-forms', clientId] as const;

/** The forms a coach sent one client, current and previous collaboration. */
export function getClientForms(clientId: string, signal?: AbortSignal): Promise<GetClientFormsResponse> {
  return apiClient.getClientFormsEndpoint(clientId, signal);
}

/** Marks a submitted form as opened by the coach. The server answers 409 for a form that is not submitted. */
export function markClientFormSeen(clientId: string, responsePublicId: string): Promise<void> {
  return apiClient.markClientFormSeenEndpoint(clientId, responsePublicId);
}
