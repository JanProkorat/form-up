const ADMIN_LANDING_PATH = '/admin';
const COACH_ROLES = ['trainer', 'nutritionist'];

/** Where a signed-in user lands by default: admin-only accounts have no coach data, so they skip /clients. */
export function defaultLandingPath(roles: string[]): string {
  const lower = roles.map((role) => role.toLowerCase());
  const isAdminOnly = lower.includes('admin') && !lower.some((role) => COACH_ROLES.includes(role));
  return isAdminOnly ? ADMIN_LANDING_PATH : '/clients';
}

/** In-app path only: no protocol-relative, backslash, /login, or control/whitespace characters (browsers strip them). */
function isSafeInAppPath(value: unknown): value is string {
  if (
    typeof value !== 'string' ||
    !value.startsWith('/') ||
    value.startsWith('//') ||
    value.startsWith('/\\') ||
    value.startsWith('/login')
  ) {
    return false;
  }
  return ![...value].some((ch) => {
    const code = ch.charCodeAt(0);
    return code <= 0x20 || (code >= 0x7f && code <= 0x9f) || /\s/.test(ch);
  });
}

/** Query-string `returnTo` first (survives reloads and new tabs), then router state, then the role's default landing. */
export function resolveReturnTo(queryValue: string | null, state: unknown, roles: string[]): string {
  if (isSafeInAppPath(queryValue)) return queryValue;
  const from = (state as { from?: unknown } | null)?.from;
  return isSafeInAppPath(from) ? from : defaultLandingPath(roles);
}
