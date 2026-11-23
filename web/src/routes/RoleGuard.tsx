import { Navigate, Outlet } from 'react-router-dom';
import { useAuthStore } from '@/stores/auth';
import { useActiveRoles } from '@/hooks/useActiveRoles';

interface Props {
  allowedRoles: string[];
  /** Also lets an admin-only account (Admin, no coach role) through; an Admin with a coach role still needs an allowed role. */
  allowAdminOnly?: boolean;
}

/** Renders the child routes only when the viewer holds one of the allowed ACTIVE roles; fails closed on error. */
export default function RoleGuard({ allowedRoles, allowAdminOnly = false }: Props) {
  const user = useAuthStore((s) => s.user);
  const { hasRole, isLoading, isError, isAdminOnly } = useActiveRoles();

  if (!user) {
    return <Navigate to="/login" replace />;
  }

  if (isLoading) {
    return null;
  }

  const allowed = (allowAdminOnly && isAdminOnly) || allowedRoles.some(hasRole);
  if (isError || !allowed) {
    return <Navigate to="/clients" replace />;
  }

  return <Outlet />;
}
