/**
 * Creates or reuses the coach's weekly check-in rule through the API and puts it back afterwards.
 * A coach may have one enabled check-in rule, so the fixture adopts an existing one (and restores its
 * stored body on teardown) and only creates a rule, and deletes it again, when none exists.
 * A new rule uses a seeded form, never a temporary "E2E ..." form that another spec deletes mid-run.
 */
import { request as apiRequest, type APIRequestContext } from '@playwright/test';

export const CHECK_IN_WEEKDAY = 'Monday';
export const CHECK_IN_TIME = '18:00';

interface RuleDto {
  ruleId?: string;
  name?: string;
  message?: string;
  actionType?: string;
  questionnaireId?: string;
  triggerKind?: string;
  scheduleInterval?: number;
  scheduleUnit?: string;
  localTime?: string;
  scheduleWeekday?: string;
  answerWindowHours?: number;
  reminderAfterHours?: number;
  notifyCoachOnMissedWeek?: boolean;
  oncePerPlan?: boolean;
  skipWhilePreviousUnanswered?: boolean;
  stopAfterSends?: number;
  enabled?: boolean;
}

interface QuestionnaireSummary {
  publicId: string;
  title: string;
  isActive: boolean;
}

export interface CheckInRuleHandle {
  ruleId: string;
  name: string;
  /** Puts the coach's rules back as they were before the fixture ran. */
  restore: () => Promise<void>;
}

/** The PUT/POST body of a rule; the audience is left out so an update keeps it. */
function toBody(rule: RuleDto, overrides: Partial<RuleDto> = {}): RuleDto {
  const merged: RuleDto = { ...rule, ...overrides, localTime: (overrides.localTime ?? rule.localTime ?? '').slice(0, 5) };
  delete merged.ruleId;
  return merged;
}

async function login(baseURL: string, email: string): Promise<APIRequestContext> {
  const password = process.env['QA_SEED_PASSWORD'];
  if (!password) {
    throw new Error('[check-in-rule] QA_SEED_PASSWORD is not set. Copy .env.test.example to .env.test.');
  }
  const anonymous = await apiRequest.newContext({ baseURL });
  try {
    const response = await anonymous.post('/auth/login', { data: { email, password } });
    if (!response.ok()) {
      throw new Error(`[check-in-rule] POST /auth/login returned ${response.status()}.`);
    }
    const { accessToken } = (await response.json()) as { accessToken: string };
    return await apiRequest.newContext({ baseURL, extraHTTPHeaders: { Authorization: `Bearer ${accessToken}` } });
  } finally {
    await anonymous.dispose();
  }
}

async function listRules(api: APIRequestContext): Promise<RuleDto[]> {
  const response = await api.get('/trainer/automations/rules');
  if (!response.ok()) {
    throw new Error(`[check-in-rule] GET rules returned ${response.status()}.`);
  }
  return ((await response.json()) as { rules?: RuleDto[] }).rules ?? [];
}

async function putRule(api: APIRequestContext, ruleId: string, body: RuleDto): Promise<void> {
  const response = await api.put(`/trainer/automations/rules/${ruleId}`, { data: body });
  if (!response.ok()) {
    throw new Error(`[check-in-rule] PUT rule returned ${response.status()}: ${await response.text()}`);
  }
}

/**
 * Makes sure the QA trainer has an enabled check-in rule on Monday at 18:00 and returns a handle whose
 * `restore` undoes it. Call `restore` in a `finally` or an `afterEach`.
 */
export async function ensureCheckInRule(baseURL: string, email = 'qa.trainer@formup.test'): Promise<CheckInRuleHandle> {
  const api = await login(baseURL, email);
  try {
    const rules = await listRules(api);
    const existing = rules.find((rule) => rule.scheduleWeekday != null);

    if (existing?.ruleId) {
      const ruleId = existing.ruleId;
      await putRule(api, ruleId, toBody(existing, { enabled: true, scheduleWeekday: CHECK_IN_WEEKDAY, localTime: CHECK_IN_TIME }));
      return {
        ruleId,
        name: existing.name ?? '',
        restore: async () => {
          await putRule(api, ruleId, toBody(existing));
          await api.dispose();
        },
      };
    }

    const formsResponse = await api.get('/trainer/questionnaires', { params: { type: 'CheckIn' } });
    const forms = ((await formsResponse.json()) as { questionnaires?: QuestionnaireSummary[] }).questionnaires ?? [];
    const form = forms.find((candidate) => candidate.isActive && !candidate.title.startsWith('E2E '));
    if (!form) {
      throw new Error('[check-in-rule] The QA trainer has no active non-E2E check-in form.');
    }

    const name = `E2E weekly check-in ${Date.now().toString(36)}`;
    const created = await api.post('/trainer/automations/rules', {
      data: {
        name,
        actionType: 'SendForm',
        questionnaireId: form.publicId,
        triggerKind: 'Schedule',
        scheduleInterval: 1,
        scheduleUnit: 'Weeks',
        localTime: CHECK_IN_TIME,
        scheduleWeekday: CHECK_IN_WEEKDAY,
        answerWindowHours: 48,
        reminderAfterHours: 24,
        notifyCoachOnMissedWeek: true,
        oncePerPlan: false,
        skipWhilePreviousUnanswered: false,
        enabled: true,
        audience: 'All',
      },
    });
    if (!created.ok()) {
      throw new Error(`[check-in-rule] POST rule returned ${created.status()}: ${await created.text()}`);
    }
    const { ruleId } = (await created.json()) as { ruleId: string };
    return {
      ruleId,
      name,
      restore: async () => {
        await api.delete(`/trainer/automations/rules/${ruleId}`);
        await api.dispose();
      },
    };
  } catch (error) {
    await api.dispose();
    throw error;
  }
}
