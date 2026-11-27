import { Outlet, useLocation } from 'react-router-dom';
import { useActiveRoles } from '@/hooks/useActiveRoles';
import ForbiddenPage from '@/pages/ForbiddenPage';

/** The only portal areas an admin-only account may open: the system libraries and the Admin section. */
const ADMIN_ONLY_PATHS = ['/recipes', '/ingredients', '/admin'];

function isAdminOnlyPath(pathname: string): boolean {
  return ADMIN_ONLY_PATHS.some((path) => pathname === path || pathname.startsWith(`${path}/`));
}

/**
 * Answers an admin-only account (Admin, no coach role) with the 403 page on every signed-in route outside
 * `ADMIN_ONLY_PATHS`. Everyone else passes through untouched and keeps their own guards.
 */
export default function AdminOnlyGuard() {
  const { pathname } = useLocation();
  const { isAdminOnly } = useActiveRoles();

  if (isAdminOnly && !isAdminOnlyPath(pathname)) {
    return <ForbiddenPage />;
  }

  return <Outlet />;
}
