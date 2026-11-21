/**
 * Shared harness for the client Automations tab tests: mounts the real tab and serves the four loads it makes
 * (the client's rows, the global rules, the coach's forms, the subscription) from one mutable state.
 * Payloads are typed with the generated API types, so a changed contract breaks the typecheck.
 */
import { Outlet } from 'react-router-dom';
import {
  AutomationActionType,
  AutomationRuleSource,
  AutomationScheduleUnit,
  AutomationTriggerKind,
  DayOfWeek,
  QuestionnaireType,
} from '@/api/generated';
import type {
  AutomationRuleDto,
  ClientAutomationItem,
  GetClientAutomationsResponse,
  GetMySubscriptionResponse,
  OverrideClientAutomationRequest,
  SubscriptionFeaturesDto,
} from '@/api/generated';
import type { QuestionnaireSummaryDto } from '@/api/questionnaires';
import ClientAutomationsTab from '@/components/client-detail/automations/ClientAutomationsTab';
import { Toaster } from '@/components/ui/toast';
import { useToastStore } from '@/stores/toast';
import { CLIENT_ID, clientDashboard } from '@/test/clientDetail';
import { HttpResponse, apiUrl, http } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';
import { renderWithProviders } from '@/test/render';

export const RULE_ID = '00000000-0000-0000-2308-000000000001';
export const RULE_FORM_ID = '00000000-0000-0000-2308-0000000000f1';
export const OTHER_FORM_ID = '00000000-0000-0000-2308-0000000000f2';
export const THIRD_FORM_ID = '00000000-0000-0000-2308-0000000000f3';

export const RULE_FORM_TITLE = 'Weekly check-in';
export const OTHER_FORM_TITLE = 'Knee rehab check-in';
export const THIRD_FORM_TITLE = 'Travel week check-in';

export interface ClientAutomationsState {
  items: ClientAutomationItem[];
  rules: AutomationRuleDto[];
  forms: QuestionnaireSummaryDto[];
  features: SubscriptionFeaturesDto;
}

function form(
  publicId: string,
  title: string,
  overrides: Partial<QuestionnaireSummaryDto> = {},
): QuestionnaireSummaryDto {
  return {
    publicId,
    title,
    type: QuestionnaireType.CheckIn,
    isActive: true,
    isDefault: false,
    questionCount: 5,
    usedForClientCount: 0,
    dateCreated: '2026-01-01T00:00:00Z',
    editedAt: '2026-01-01T00:00:00Z',
    ...overrides,
  };
}

/** The global weekly check-in rule: every Thursday at 18:00, sending the rule's own form. */
export function checkInRule(overrides: AutomationRuleDto = {}): AutomationRuleDto {
  return {
    ruleId: RULE_ID,
    name: 'Weekly check-in',
    actionType: AutomationActionType.SendForm,
    questionnaireId: RULE_FORM_ID,
    triggerKind: AutomationTriggerKind.Schedule,
    scheduleInterval: 1,
    scheduleUnit: AutomationScheduleUnit.Weeks,
    localTime: '18:00:00',
    scheduleWeekday: DayOfWeek.Thursday,
    answerWindowHours: 48,
    reminderAfterHours: 24,
    notifyCoachOnMissedWeek: true,
    oncePerPlan: false,
    skipWhilePreviousUnanswered: false,
    enabled: true,
    ...overrides,
  };
}

/** The client's row for the check-in rule; the fields mirror the rule unless `overrides` customises them. */
export function checkInItem(overrides: ClientAutomationItem = {}): ClientAutomationItem {
  const rule = checkInRule();
  return {
    ...rule,
    source: AutomationRuleSource.Default,
    isPaused: false,
    nextSendAt: '2026-06-18T18:00:00Z',
    ...overrides,
  };
}

export function newClientAutomationsState(overrides: Partial<ClientAutomationsState> = {}): ClientAutomationsState {
  return {
    items: [checkInItem()],
    rules: [checkInRule()],
    forms: [
      form(RULE_FORM_ID, RULE_FORM_TITLE),
      form(OTHER_FORM_ID, OTHER_FORM_TITLE),
      form(THIRD_FORM_ID, THIRD_FORM_TITLE, { isActive: false }),
      form('00000000-0000-0000-2308-0000000000a1', 'Intake form', { type: QuestionnaireType.Onboarding }),
    ],
    features: {},
    ...overrides,
  };
}

/** Serves the tab's loads from `state`; each is read per request, so a test can swap them. */
export function serveClientAutomations(state: ClientAutomationsState): void {
  server.use(
    http.get(apiUrl(`/trainer/clients/${CLIENT_ID}/automations`), () => {
      const body: GetClientAutomationsResponse = { items: state.items };
      return HttpResponse.json(body);
    }),
    http.get(apiUrl('/trainer/automations/rules'), () => HttpResponse.json({ rules: state.rules })),
    http.get(apiUrl('/trainer/questionnaires'), () => HttpResponse.json({ questionnaires: state.forms })),
    http.get(apiUrl('/users/me/subscription'), () => {
      const body: GetMySubscriptionResponse = { features: state.features };
      return HttpResponse.json(body);
    }),
  );
}

/** An error body the way `SendProblemAsync` writes it: the code is a top-level `errorCode`. */
export function problemWithCode(status: number, errorCode: string) {
  return HttpResponse.json({ status, errorCode }, { status, headers: { 'Content-Type': 'application/problem+json' } });
}

/**
 * Answers PUT .../automations/{ruleId}/override and records each body. `respond` decides the answer;
 * the default is 204.
 */
export function serveOverride(respond: () => Response = () => new HttpResponse(null, { status: 204 })) {
  const bodies: OverrideClientAutomationRequest[] = [];
  server.use(
    http.put(apiUrl(`/trainer/clients/${CLIENT_ID}/automations/${RULE_ID}/override`), async ({ request }) => {
      bodies.push((await request.json()) as OverrideClientAutomationRequest);
      return respond();
    }),
  );
  return bodies;
}

/** Mounts the Automations tab of the seeded client, signed in as a trainer. */
export function renderAutomationsTab() {
  useToastStore.setState({ toasts: [] });
  return renderWithProviders(
    [
      {
        element: (
          <>
            <Outlet />
            <Toaster />
          </>
        ),
        children: [
          { path: '*', element: <ClientAutomationsTab clientId={CLIENT_ID} dashboard={clientDashboard()} /> },
        ],
      },
    ],
    { auth: { roles: ['Trainer'] } },
  );
}
