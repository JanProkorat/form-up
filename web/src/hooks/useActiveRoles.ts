import { useQuery } from '@tanstack/react-query';
import { getMyCoachRoles, rolesKeys } from '@/api/roles';
import { useAuthStore } from '@/stores/auth';

const COACH_ROLES = new Set(['trainer', 'nutritionist']);

export interface ActiveRoles {
  /** Active role names in their canonical casing (Admin, Client, Trainer, Nutritionist). */
  roles: string[];
  hasRole: (role: string) => boolean;
  /** True while the coach-role lookup is in flight; coach roles are not yet known. */
  isLoading: boolean;
  /** True when the coach-role lookup failed; coach roles must be treated as absent. */
  isError: boolean;
  /**
   * Admin whose token has neither a Trainer nor a Nutritionist role (the backend's rule). Independent of the
   * coach-role lookup: an Admin with a removed coach role is not admin-only.
   */
  isAdminOnly: boolean;
}

function canonical(role: string): string {
  const lower = role.toLowerCase();
  return lower.charAt(0).toUpperCase() + lower.slice(1);
}

/**
 * The viewer's active roles. Trainer/Nutritionist come only from GET /users/me/roles
 * (the JWT keeps removed coach roles); Admin/Client come from the JWT.
 */
export function useActiveRoles(): ActiveRoles {
  const user = useAuthStore((s) => s.user);
  const tokenRoles = (user?.roles ?? []).map((role) => role.toLowerCase());
  // The endpoint is Trainer/Nutritionist-only — an admin/client-only token would get a 403.
  const hasCoachToken = tokenRoles.some((role) => COACH_ROLES.has(role));

  const query = useQuery({
    queryKey: rolesKeys.mine,
    queryFn: getMyCoachRoles,
    enabled: hasCoachToken,
  });

  const coachRoles = query.data ? query.data.roles.map((r) => r.role.toLowerCase()) : [];
  const roles = [...tokenRoles.filter((role) => !COACH_ROLES.has(role)), ...coachRoles].map(canonical);

  const isLoading = hasCoachToken && query.isPending;
  const isError = hasCoachToken && query.isError;
  // Same rule as the backend: decided from the token roles alone, so a removed coach role (which the JWT keeps)
  // still means "not admin-only", and it is known immediately, without waiting for the roles lookup.
  const isAdminOnly = tokenRoles.includes('admin') && !hasCoachToken;

  return {
    roles,
    hasRole: (role) => roles.includes(canonical(role)),
    isLoading,
    isError,
    isAdminOnly,
  };
}
