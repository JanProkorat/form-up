/**
 * Client detail, Automations tab: the check-in form a coach picks for one client in the override drawer (#2308).
 * The override PUT treats a missing `checkInQuestionnaireId` as "back to the rule's form", so these tests pin
 * what every save sends, and how a refused save looks.
 */
import { describe, expect, it } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import { AutomationRuleSource, DayOfWeek } from '@/api/generated';
import {
  OTHER_FORM_ID,
  OTHER_FORM_TITLE,
  RULE_FORM_TITLE,
  checkInItem,
  newClientAutomationsState,
  problemWithCode,
  renderAutomationsTab,
  serveClientAutomations,
  serveOverride,
} from '@/test/clientAutomations';

const RULE_NAME = 'Weekly check-in';

/** Opens the override drawer of the check-in row and returns it. */
async function openOverrideDrawer(user: ReturnType<typeof renderAutomationsTab>['user']) {
  await user.click(await screen.findByRole('button', { name: RULE_NAME }));
  return screen.findByRole('dialog');
}

/** A client who already follows another check-in form than the rule's. */
function itemWithPickedForm() {
  return checkInItem({
    source: AutomationRuleSource.Custom,
    checkInQuestionnaireId: OTHER_FORM_ID,
    checkInQuestionnaireName: OTHER_FORM_TITLE,
  });
}

describe('Automations tab: the check-in form of one client', () => {
  it('lists only active check-in forms and sends the picked one', async () => {
    serveClientAutomations(newClientAutomationsState());
    const bodies = serveOverride();
    const { user } = renderAutomationsTab();

    const drawer = await openOverrideDrawer(user);
    const select = within(drawer).getByLabelText('Check-in form');
    expect(within(select).getAllByRole('option').map((option) => option.textContent)).toEqual([
      expect.stringContaining(RULE_FORM_TITLE),
      expect.stringContaining(OTHER_FORM_TITLE),
    ]);
    expect(within(drawer).getByText(/Your rule sends Weekly check-in\./)).toBeInTheDocument();

    await user.selectOptions(select, OTHER_FORM_ID);
    await user.click(within(drawer).getByRole('button', { name: 'Save for QA' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(bodies).toHaveLength(1);
    expect(bodies[0].checkInQuestionnaireId).toBe(OTHER_FORM_ID);
  });

  it('keeps the picked form when only the weekday changes', async () => {
    serveClientAutomations(newClientAutomationsState({ items: [itemWithPickedForm()] }));
    const bodies = serveOverride();
    const { user } = renderAutomationsTab();

    const drawer = await openOverrideDrawer(user);
    expect(within(drawer).getByLabelText('Check-in form')).toHaveValue(OTHER_FORM_ID);

    await user.click(within(drawer).getByRole('button', { name: 'Mon' }));
    await user.click(within(drawer).getByRole('button', { name: 'Save for QA' }));

    await waitFor(() => expect(bodies).toHaveLength(1));
    expect(bodies[0].scheduleWeekday).toBe('Monday');
    expect(bodies[0].checkInQuestionnaireId).toBe(OTHER_FORM_ID);
  });

  it('goes back to the rule’s form with "Use the rule’s form"', async () => {
    serveClientAutomations(newClientAutomationsState({ items: [itemWithPickedForm()] }));
    const bodies = serveOverride();
    const { user } = renderAutomationsTab();

    const drawer = await openOverrideDrawer(user);
    await user.click(within(drawer).getByRole('button', { name: 'Use the rule’s form' }));
    expect(within(drawer).getByLabelText('Check-in form')).toHaveValue('');
    await user.click(within(drawer).getByRole('button', { name: 'Save for QA' }));

    await waitFor(() => expect(bodies).toHaveLength(1));
    // The server reads a missing field as "the rule's form"; the PUT itself proves the clear was saved.
    expect(bodies[0].checkInQuestionnaireId).toBeUndefined();
  });

  it('names the form the client gets in the row and in the drawer footer', async () => {
    serveClientAutomations(newClientAutomationsState({ items: [itemWithPickedForm()] }));
    const { user } = renderAutomationsTab();

    const row = (await screen.findByRole('button', { name: RULE_NAME })).closest('tr');
    expect(row).not.toBeNull();
    expect(within(row as HTMLElement).getByText(new RegExp(`Form: ${OTHER_FORM_TITLE}`))).toBeInTheDocument();

    const drawer = await openOverrideDrawer(user);
    expect(within(drawer).getByText(new RegExp(`Next send to QA: .* · ${OTHER_FORM_TITLE}`))).toBeInTheDocument();
  });

  it('shows the rule’s form in the row of a client who follows the rule', async () => {
    serveClientAutomations(
      newClientAutomationsState({ items: [checkInItem({ source: AutomationRuleSource.Custom, scheduleWeekday: DayOfWeek.Monday })] }),
    );
    renderAutomationsTab();

    const row = (await screen.findByRole('button', { name: RULE_NAME })).closest('tr');
    expect(
      within(row as HTMLElement).getByText(`Changed from every Thursday · Form: ${RULE_FORM_TITLE}`),
    ).toBeInTheDocument();
  });

  it('does not open the drawer of a check-in row the plan cannot customise', async () => {
    serveClientAutomations(newClientAutomationsState({ features: { canUsePerClientCheckInConfig: false } }));
    const { user } = renderAutomationsTab();

    const button = await screen.findByRole('button', { name: RULE_NAME });
    await waitFor(() => expect(button).toBeDisabled());
    expect(screen.getByText('Changing this check-in for one client comes with a higher plan.')).toBeInTheDocument();

    await user.click(button);
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('opens the paywall, not a toast, when the server refuses with SUBSCRIPTION_REQUIRED', async () => {
    serveClientAutomations(newClientAutomationsState());
    serveOverride(() => problemWithCode(403, 'SUBSCRIPTION_REQUIRED'));
    const { user } = renderAutomationsTab();

    const drawer = await openOverrideDrawer(user);
    await user.selectOptions(within(drawer).getByLabelText('Check-in form'), OTHER_FORM_ID);
    await user.click(within(drawer).getByRole('button', { name: 'Save for QA' }));

    expect(await screen.findByText('Weekly check-ins come with a higher plan')).toBeInTheDocument();
    expect(screen.queryByText('Could not save the rule.')).not.toBeInTheDocument();
  });

  it.each([
    [404, 'AUTOMATION_QUESTIONNAIRE_NOT_FOUND', 'The chosen form no longer exists.'],
    [400, 'AUTOMATION_QUESTIONNAIRE_NOT_USABLE', 'This form can’t be sent here. Choose an active form of the right type.'],
  ])('keeps the drawer and the pick when the server answers %i %s', async (status, code, message) => {
    serveClientAutomations(newClientAutomationsState());
    const bodies = serveOverride(() => problemWithCode(status, code));
    const { user } = renderAutomationsTab();

    const drawer = await openOverrideDrawer(user);
    await user.selectOptions(within(drawer).getByLabelText('Check-in form'), OTHER_FORM_ID);
    await user.click(within(drawer).getByRole('button', { name: 'Save for QA' }));

    expect(await screen.findByText(message)).toBeInTheDocument();
    expect(bodies).toHaveLength(1);
    expect(screen.getByRole('dialog')).toBeInTheDocument();
    expect(within(screen.getByRole('dialog')).getByLabelText('Check-in form')).toHaveValue(OTHER_FORM_ID);
  });
});
