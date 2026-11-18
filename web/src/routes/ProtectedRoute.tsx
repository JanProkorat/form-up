import { Navigate, Outlet, useLocation } from 'react-router-dom';
import { CheckoutActivationWatcher } from '@/components/subscription/CheckoutActivationWatcher';
import { useAuthStore } from '@/stores/auth';

export default function ProtectedRoute() {
  const isAuthenticated = useAuthStore((s) => s.isAuthenticated);
  const isInitialized = useAuthStore((s) => s.isInitialized);
  const user = useAuthStore((s) => s.user);
  const location = useLocation();

  if (!isInitialized) {
    return null;
  }

  if (!isAuthenticated) {
    return <Navigate to="/login" replace state={{ from: location.pathname + location.search }} />;
  }

  // Block unverified users — redirect to verification page
  if (user && !user.emailConfirmed) {
    return <Navigate to="/verify-email" replace />;
  }

  // Clients use the mobile app — redirect them away from the web portal
  if (user?.roles.includes('Client') && !user.roles.some((r) => ['Trainer', 'Nutritionist', 'Admin'].includes(r))) {
    return <Navigate to="/download-app" replace />;
  }

  return (
    <>
      <Outlet />
      <CheckoutActivationWatcher />
    </>
  );
}
