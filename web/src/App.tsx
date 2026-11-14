import { useEffect } from 'react';
import {
  Route,
  RouterProvider,
  createBrowserRouter,
  createRoutesFromElements,
  useRouteError,
} from 'react-router-dom';
import { QueryClientProvider } from '@tanstack/react-query';
import { queryClient } from '@/lib/queryClient';
import { Toaster } from '@/components/ui/toast';
import { useAuthStore } from '@/stores/auth';
import ProtectedRoute from '@/routes/ProtectedRoute';
import RoleGuard from '@/routes/RoleGuard';
import AppShell from '@/components/layout/AppShell';
import EntryPage from '@/pages/EntryPage';
import LoginForm from '@/components/entry/LoginForm';
import ForgotPasswordForm from '@/components/entry/ForgotPasswordForm';
import ResetPasswordForm from '@/components/entry/ResetPasswordForm';
import RegisterPage from '@/pages/RegisterPage';
import VerifyEmailPage from '@/pages/VerifyEmailPage';
import InviteAcceptPage from '@/pages/InviteAcceptPage';
import BankAccountUndoPage from '@/pages/BankAccountUndoPage';
import EmailTurnOffPage from '@/pages/EmailTurnOffPage';
import DownloadAppPage from '@/pages/DownloadAppPage';
import ClientsPage from '@/pages/ClientsPage';
import ClientDetailPage from '@/pages/ClientDetailPage';
import InboxPage from '@/pages/InboxPage';
import FormsPage from '@/pages/FormsPage';
import AutomationsPage from '@/pages/AutomationsPage';
import PackagesPage from '@/pages/PackagesPage';
import AdminPlansPage from '@/pages/AdminPlansPage';
import AdminCoachesPage from '@/pages/AdminCoachesPage';
import AdminPromotionsPage from '@/pages/AdminPromotionsPage';
import AdminCoachProfilePage from '@/pages/AdminCoachProfilePage';
import FormBuilderPage from '@/pages/FormBuilderPage';
import IngredientsPage from '@/pages/IngredientsPage';
import RecipesPage from '@/pages/RecipesPage';
import PlanTemplatesPage from '@/pages/PlanTemplatesPage';
import PlanTemplateEditorPage from '@/pages/PlanTemplateEditorPage';
import ClientPlanEditorPage from '@/pages/ClientPlanEditorPage';
import LapseKeepClientsPage from '@/pages/LapseKeepClientsPage';
import PlanChoicePage from '@/pages/PlanChoicePage';
import PlanChoiceGuard from '@/routes/PlanChoiceGuard';
import PlanPickerPage from '@/pages/PlanPickerPage';
import RegisterPlanPage from '@/pages/RegisterPlanPage';
import BillingReturnPage from '@/pages/BillingReturnPage';
import {
  BILLING_CANCEL_PATH,
  BILLING_PORTAL_RETURN_PATH,
  BILLING_SUCCESS_PATH,
  KEEP_CLIENTS_PATH,
  PLAN_CHOICE_PATH,
  PLAN_PICKER_PATH,
  REGISTER_PLAN_PATH,
} from '@/components/subscription/planLinks';
import ProfilePage from '@/pages/ProfilePage';
import SettingsPage from '@/pages/SettingsPage';
import NotFoundPage from '@/pages/NotFoundPage';
import { ErrorFallback } from '@/ErrorBoundary';

/** Route render errors that escape AppShell's RouteErrorBoundary land here, same fallback as the app root. */
function RootErrorElement() {
  const error = useRouteError();
  return <ErrorFallback error={error instanceof Error ? error : null} />;
}

/*
 * A data router (not <BrowserRouter>) so screens can veto navigation with
 * useBlocker. Created once at module scope: building it inside App's render
 * would reset history state on every re-render.
 */
const router = createBrowserRouter(
  createRoutesFromElements(
    <Route errorElement={<RootErrorElement />}>
      {/*
       * Public routes — MUST stay outside ProtectedRoute. ProtectedRoute
       * redirects an unauthenticated visitor to "/login", an unconfirmed
       * user to "/verify-email", and a client-only user to "/download-app";
       * mounting any of these inside ProtectedRoute re-enters the same
       * guard and infinite-loops instead of rendering.
       *
       * EntryPage is a pathless layout route: it renders the landing page
       * once plus one sign-in dialog, which is open on every child route
       * except "/" and renders whichever form route is active (via
       * useOutlet(), see that component). Do NOT collapse these into a
       * single `path="/:authMode?"` route — an optional param segment
       * swallows every unmatched path ahead of the catch-all NotFound
       * route.
       */}
      <Route element={<EntryPage />}>
        <Route path="/" element={null} />
        <Route path="/login" element={<LoginForm />} />
        <Route path="/forgot-password" element={<ForgotPasswordForm />} />
        <Route path="/auth/reset-password" element={<ResetPasswordForm />} />
      </Route>
      <Route path="/register" element={<RegisterPage />} />
      <Route path="/verify-email" element={<VerifyEmailPage />} />
      <Route path="/invite/accept" element={<InviteAcceptPage />} />
      <Route path="/bank-account/undo" element={<BankAccountUndoPage />} />
      <Route path="/email-off" element={<EmailTurnOffPage />} />
      <Route path="/download-app" element={<DownloadAppPage />} />

      <Route element={<ProtectedRoute />}>
       {/* Outside PlanChoiceGuard on purpose: the sign-up plan step and the Stripe return pages must stay reachable
           while a plan decision is pending, or the guard would bounce the coach before the purchase registers. */}
       <Route element={<RoleGuard allowedRoles={['Trainer', 'Nutritionist']} />}>
        <Route path={REGISTER_PLAN_PATH} element={<RegisterPlanPage />} />
        <Route path={BILLING_SUCCESS_PATH} element={<BillingReturnPage />} />
        <Route path={BILLING_CANCEL_PATH} element={<BillingReturnPage />} />
        <Route path={BILLING_PORTAL_RETURN_PATH} element={<BillingReturnPage />} />
       </Route>
       {/* Keeps a coach with an ended given plan on the picker; the picker route itself is full screen, no AppShell. */}
       <Route element={<PlanChoiceGuard />}>
        <Route element={<RoleGuard allowedRoles={['Trainer', 'Nutritionist']} />}>
          <Route path={PLAN_CHOICE_PATH} element={<PlanChoicePage />} />
        </Route>
        <Route element={<AppShell />}>
          <Route path="/clients" element={<ClientsPage />} />
          <Route path="/clients/:clientId" element={<ClientDetailPage />}>
            <Route path="nutrition" element={null} />
            <Route path="photos" element={null} />
            <Route path="payment" element={null} />
            <Route path="automations" element={null} />
            <Route path="forms" element={null} />
          </Route>
          <Route path="/inbox" element={<InboxPage />} />
          <Route path="/forms" element={<FormsPage />} />
          <Route path="/automations" element={<AutomationsPage />} />
          <Route path="/packages" element={<PackagesPage />} />
          <Route element={<RoleGuard allowedRoles={['Nutritionist']} />}>
            <Route path="/ingredients" element={<IngredientsPage />} />
            <Route path="/recipes" element={<RecipesPage />} />
            <Route path="/plan-templates" element={<PlanTemplatesPage />} />
          </Route>
          <Route element={<RoleGuard allowedRoles={['Admin']} />}>
            <Route path="/admin/plans" element={<AdminPlansPage />} />
            <Route path="/admin/promotions" element={<AdminPromotionsPage />} />
            <Route path="/admin/coach-accounts" element={<AdminCoachesPage />} />
            <Route path="/admin/coach-accounts/:coachId" element={<AdminCoachProfilePage />} />
          </Route>
          <Route element={<RoleGuard allowedRoles={['Trainer', 'Nutritionist']} />}>
            <Route path={KEEP_CLIENTS_PATH} element={<LapseKeepClientsPage />} />
            <Route path={PLAN_PICKER_PATH} element={<PlanPickerPage />} />
          </Route>
          <Route path="/profile" element={<ProfilePage />} />
          <Route path="/settings" element={<SettingsPage />} />
        </Route>
        {/* Full-bleed workspace: icon-rail sidebar, no main padding (PageTemplateEditor). */}
        <Route element={<AppShell variant="editor" />}>
          <Route path="/forms/:id" element={<FormBuilderPage />} />
          <Route element={<RoleGuard allowedRoles={['Nutritionist']} />}>
            <Route path="/plan-templates/:templateId" element={<PlanTemplateEditorPage />} />
            <Route path="/clients/:clientId/nutrition/plans/:planId" element={<ClientPlanEditorPage />} />
          </Route>
        </Route>
       </Route>
      </Route>

      <Route path="*" element={<NotFoundPage />} />
    </Route>,
  ),
);

export default function App() {
  const restoreSession = useAuthStore((s) => s.restoreSession);
  const isInitialized = useAuthStore((s) => s.isInitialized);

  useEffect(() => {
    restoreSession();
  }, [restoreSession]);

  if (!isInitialized) {
    return null;
  }

  return (
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
      <Toaster />
    </QueryClientProvider>
  );
}
