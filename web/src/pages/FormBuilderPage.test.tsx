import { act, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { FORM_ID, onboardingForm, renderFormBuilderPage, serveForm } from '@/test/formBuilder';

async function openBuilder() {
  serveForm(onboardingForm());
  const view = renderFormBuilderPage();
  const title = await screen.findByRole('textbox', { name: 'Form name' });
  return { ...view, title };
}

describe('FormBuilderPage unsaved edits', () => {
  it('asks before an in-app link drops unsaved edits and stays on the page', async () => {
    const { user, router, title } = await openBuilder();
    await user.type(title, ' v2');

    await user.click(screen.getByRole('link', { name: 'Clients nav' }));

    expect(await screen.findByRole('dialog', { name: 'Leave without saving?' })).toBeInTheDocument();
    expect(router.state.location.pathname).toBe(`/forms/${FORM_ID}`);
  });

  it('keeps the edit when the coach chooses to keep editing', async () => {
    const { user, router, title } = await openBuilder();
    await user.type(title, ' v2');
    await user.click(screen.getByRole('link', { name: 'Clients nav' }));

    await user.click(await screen.findByRole('button', { name: 'Keep editing' }));

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(router.state.location.pathname).toBe(`/forms/${FORM_ID}`);
    expect(screen.getByRole('textbox', { name: 'Form name' })).toHaveValue('Intake form v2');
  });

  it('leaves for the target page when the coach confirms', async () => {
    const { user, router, title } = await openBuilder();
    await user.type(title, ' v2');
    await user.click(screen.getByRole('link', { name: 'Clients nav' }));

    await user.click(await screen.findByRole('button', { name: 'Leave' }));

    expect(await screen.findByTestId('clients-page')).toBeInTheDocument();
    expect(router.state.location.pathname).toBe('/clients');
  });

  it('asks on browser back too', async () => {
    serveForm(onboardingForm());
    const { user, router } = renderFormBuilderPage(['/clients', `/forms/${FORM_ID}`]);
    await user.type(await screen.findByRole('textbox', { name: 'Form name' }), ' v2');

    await act(() => router.navigate(-1));

    expect(await screen.findByRole('dialog', { name: 'Leave without saving?' })).toBeInTheDocument();
    expect(router.state.location.pathname).toBe(`/forms/${FORM_ID}`);
  });

  it('navigates straight away when nothing was edited', async () => {
    const { user, router } = await openBuilder();

    await user.click(screen.getByRole('link', { name: 'Clients nav' }));

    expect(await screen.findByTestId('clients-page')).toBeInTheDocument();
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(router.state.location.pathname).toBe('/clients');
  });
});
