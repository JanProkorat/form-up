/**
 * Harness for the form builder page tests: renders the real FormBuilderPage on `/forms/:id` inside a layout with a
 * sidebar-style link, and serves the one form it loads.
 */
import { Link, Outlet } from 'react-router-dom';
import type { RouteObject } from 'react-router-dom';
import { QuestionnaireType, type QuestionnaireDto } from '@/api/questionnaires';
import FormBuilderPage from '@/pages/FormBuilderPage';
import { HttpResponse, apiUrl, http } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';
import { renderWithProviders } from '@/test/render';

export const FORM_ID = 'cccccccc-0000-0000-0000-000000000001';

/** An Onboarding form (so the page makes no automation requests) with one short-text question. */
export function onboardingForm(overrides: Partial<QuestionnaireDto> = {}): QuestionnaireDto {
  return {
    publicId: FORM_ID,
    title: 'Intake form',
    description: null,
    type: QuestionnaireType.Onboarding,
    isActive: true,
    isDefault: false,
    questions: [
      {
        publicId: 'dddddddd-0000-0000-0000-000000000001',
        orderIndex: 0,
        type: 'ShortText',
        label: 'Your goal',
        helperText: null,
        isRequired: false,
        isHidden: false,
        config: null,
        mappedField: null,
      },
    ],
    ...overrides,
  };
}

export function serveForm(form: QuestionnaireDto): void {
  server.use(http.get(apiUrl(`/trainer/questionnaires/${form.publicId}`), () => HttpResponse.json(form)));
}

/** Mounts the builder with a link to `/clients` standing in for the sidebar, plus `/forms` and `/clients` targets. */
export function renderFormBuilderPage(initialEntries: string[] = [`/forms/${FORM_ID}`]) {
  const routes: RouteObject[] = [
    {
      element: (
        <>
          <Link to="/clients" aria-label="Clients nav" />
          <Outlet />
        </>
      ),
      children: [
        { path: '/forms/:id', element: <FormBuilderPage /> },
        { path: '/forms', element: <div data-testid="forms-list" /> },
        { path: '/clients', element: <div data-testid="clients-page" /> },
      ],
    },
  ];
  return renderWithProviders(routes, { initialEntries, auth: { roles: ['Trainer'] } });
}
