/**
 * Client detail, Forms tab, by plan entitlement: forms on (everything), only check-ins on (check-in answers
 * readable, nothing sent), both off (paywall). Also the tab's lock icon and the Mark reviewed request.
 */
import { describe, expect, it } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import { FormResponseState, QuestionnaireResponseKind } from '@/api/generated';
import type { ClientFormItem, GetClientFormsResponse, GetTrainerQuestionnairesResponse } from '@/api/generated';
import {
  CLIENT_ID,
  newClientDetailState,
  renderFormsTab,
  serveClientDetail,
  type CoachFeatures,
} from '@/test/clientDetail';
import { HttpResponse, apiUrl, http } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';

const CHECK_IN_ID = '00000000-0000-0000-1538-000000000001';
const QUESTIONNAIRE_ID = '00000000-0000-0000-1538-000000000002';

const CHECK_IN: ClientFormItem = {
  responsePublicId: CHECK_IN_ID,
  kind: QuestionnaireResponseKind.CheckIn,
  questionnaireTitle: 'Weekly check-in',
  state: FormResponseState.Answered,
  sentAt: '2026-11-09T08:00:00Z',
  submittedAt: '2026-11-10T08:00:00Z',
  isNew: true,
  checkInWeekStart: '2026-11-09',
  answers: [{ questionPublicId: '00000000-0000-0000-1538-0000000000a1', questionLabel: 'How was the week?', valueText: 'Fine' }],
};

const QUESTIONNAIRE: ClientFormItem = {
  responsePublicId: QUESTIONNAIRE_ID,
  kind: QuestionnaireResponseKind.Questionnaire,
  questionnaireTitle: 'Diet questionnaire',
  state: FormResponseState.Answered,
  sentAt: '2026-11-01T08:00:00Z',
  submittedAt: '2026-11-02T08:00:00Z',
  answers: [{ questionPublicId: '00000000-0000-0000-1538-0000000000a2', questionLabel: 'Allergies?', valueText: 'None' }],
};

const NO_FORMS_FEATURE: CoachFeatures = { canSendQuestionnaires: false, canUseWeeklyCheckIns: true };
const NO_FEATURES: CoachFeatures = { canSendQuestionnaires: false, canUseWeeklyCheckIns: false };

function serve(features: CoachFeatures, current: ClientFormItem[]) {
  serveClientDetail(newClientDetailState(), features);
  const body: GetClientFormsResponse = { current, previous: [] };
  const library: GetTrainerQuestionnairesResponse = { questionnaires: [] };
  server.use(
    http.get(apiUrl(`/trainer/clients/${CLIENT_ID}/forms`), () => HttpResponse.json(body)),
    // The Send a form dialog loads the coach's questionnaires.
    http.get(apiUrl('/trainer/questionnaires'), () => HttpResponse.json(library)),
  );
}

function problem(status: number) {
  return HttpResponse.json({ status }, { status, headers: { 'Content-Type': 'application/problem+json' } });
}

describe('Forms tab by entitlement', () => {
  it('forms on: lists every form, shows the filters and Send a form', async () => {
    serve({}, [CHECK_IN, QUESTIONNAIRE]);
    renderFormsTab();

    expect(await screen.findByRole('button', { name: 'Send a form' })).toBeInTheDocument();
    expect(screen.getAllByTestId('form-list-card')).toHaveLength(2);
    expect(screen.getByRole('group', { name: 'Filter forms' })).toBeInTheDocument();
    expect(screen.queryByTestId('forms-checkins-only-hint')).not.toBeInTheDocument();
  });

  it('check-ins only: lists check-ins, no Send, no filters, points to the forms feature', async () => {
    serve(NO_FORMS_FEATURE, [QUESTIONNAIRE, CHECK_IN]);
    renderFormsTab();

    const hint = await screen.findByTestId('forms-checkins-only-hint');
    expect(within(hint).getByRole('link', { name: 'See plans' })).toHaveAttribute('href', '/plan');
    const cards = screen.getAllByTestId('form-list-card');
    expect(cards).toHaveLength(1);
    expect(within(cards[0]).getByText('Weekly check-in')).toBeInTheDocument();
    expect(screen.queryByText('Diet questionnaire')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Send (a|another) form/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('group', { name: 'Filter forms' })).not.toBeInTheDocument();
    expect(screen.queryByRole('region', { name: /higher plan/ })).not.toBeInTheDocument();
  });

  it('check-ins only: a ?form= link to a questionnaire falls back to the check-in', async () => {
    serve(NO_FORMS_FEATURE, [QUESTIONNAIRE, CHECK_IN]);
    renderFormsTab(`?form=${QUESTIONNAIRE_ID}`);

    const detail = await screen.findByTestId('form-detail');
    expect(within(detail).getByText('How was the week?')).toBeInTheDocument();
    expect(within(detail).queryByText('Allergies?')).not.toBeInTheDocument();
  });

  it('check-ins only: without any check-in the empty state asks for no form and offers no Send', async () => {
    serve(NO_FORMS_FEATURE, [QUESTIONNAIRE]);
    renderFormsTab();

    const empty = await screen.findByTestId('forms-empty');
    expect(within(empty).getByText(/answers a weekly check-in/)).toBeInTheDocument();
    expect(within(empty).queryByRole('button', { name: /Send/ })).not.toBeInTheDocument();
    expect(within(empty).getByTestId('forms-checkins-only-hint')).toBeInTheDocument();
    expect(screen.queryByText('Diet questionnaire')).not.toBeInTheDocument();
  });

  it('both off: shows the forms paywall and nothing else', async () => {
    serve(NO_FEATURES, [CHECK_IN, QUESTIONNAIRE]);
    renderFormsTab();

    expect(await screen.findByRole('region', { name: /higher plan/ })).toBeInTheDocument();
    expect(screen.queryByTestId('form-list-card')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Send a form' })).not.toBeInTheDocument();
  });
});

describe('Forms tab lock icon', () => {
  it('stays unlocked with check-ins only', async () => {
    serve(NO_FORMS_FEATURE, [CHECK_IN]);
    renderFormsTab();

    await screen.findByTestId('forms-checkins-only-hint');
    expect(within(screen.getByRole('tab', { name: /^Forms/ })).queryByText('Not included in your plan')).not.toBeInTheDocument();
  });

  it('locks when both features are missing', async () => {
    serve(NO_FEATURES, [CHECK_IN]);
    renderFormsTab();

    await screen.findByRole('region', { name: /higher plan/ });
    expect(within(screen.getByRole('tab', { name: /^Forms/ })).getByText('Not included in your plan')).toBeInTheDocument();
  });
});

describe('Forms tab in check-ins-only mode: review and errors', () => {
  it('Mark reviewed still sends its request; opening the check-in sends none', async () => {
    serve(NO_FORMS_FEATURE, [CHECK_IN]);
    const seen: string[] = [];
    server.use(
      http.post(apiUrl(`/trainer/clients/${CLIENT_ID}/forms/${CHECK_IN_ID}/seen`), ({ request }) => {
        seen.push(new URL(request.url).pathname);
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const { user } = renderFormsTab();

    const button = await screen.findByTestId('mark-reviewed');
    expect(seen).toHaveLength(0);
    await user.click(button);

    await waitFor(() => expect(seen).toEqual([`/trainer/clients/${CLIENT_ID}/forms/${CHECK_IN_ID}/seen`]));
  });

  it('a 409 on Mark reviewed stays silent', async () => {
    serve(NO_FORMS_FEATURE, [CHECK_IN]);
    let calls = 0;
    server.use(
      http.post(apiUrl(`/trainer/clients/${CLIENT_ID}/forms/${CHECK_IN_ID}/seen`), () => {
        calls += 1;
        return problem(409);
      }),
    );
    const { user } = renderFormsTab();

    await user.click(await screen.findByTestId('mark-reviewed'));

    await waitFor(() => expect(calls).toBe(1));
    await waitFor(() => expect(screen.getByTestId('mark-reviewed')).toBeEnabled());
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.queryByText(/Couldn't mark the check-in/)).not.toBeInTheDocument();
  });

  it('a 404 shows the no-link message, not the paywall', async () => {
    serveClientDetail(newClientDetailState(), NO_FORMS_FEATURE);
    server.use(http.get(apiUrl(`/trainer/clients/${CLIENT_ID}/forms`), () => problem(404)));
    renderFormsTab();

    const alert = await screen.findByTestId('forms-error');
    expect(alert).toHaveTextContent(/no longer available because the collaboration has ended/);
    expect(screen.queryByRole('region', { name: /higher plan/ })).not.toBeInTheDocument();
  });
});
