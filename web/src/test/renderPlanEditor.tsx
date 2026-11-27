/** Mounts the real ClientPlanEditorPage on its route (state and requests: `clientPlanEditor.ts`); the client's nutrition tab is a stand-in. */
import { Outlet } from 'react-router-dom';
import { Toaster } from '@/components/ui/toast';
import ClientPlanEditorPage from '@/pages/ClientPlanEditorPage';
import { useToastStore } from '@/stores/toast';
import { CLIENT_ID } from '@/test/clientDetail';
import { PLAN_ID } from '@/test/clientPlanEditor';
import { renderWithProviders } from '@/test/render';

export function renderPlanEditor() {
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
          { path: '/clients/:clientId/nutrition/plans/:planId', element: <ClientPlanEditorPage /> },
          { path: '/clients/:clientId/nutrition', element: <div data-testid="nutrition-tab" /> },
        ],
      },
    ],
    {
      initialEntries: [`/clients/${CLIENT_ID}/nutrition/plans/${PLAN_ID}`],
      auth: { roles: ['Nutritionist'] },
    },
  );
}
