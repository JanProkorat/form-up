/**
 * Shared fakes for the admin-only access tests (#2088): the background requests the signed-in shell sends to a
 * coach, and a recorder for every request a session sends. An admin-only session registers none of these
 * handlers, so any coach-only request it sends fails the test as an unhandled request.
 */
import type { GetMyCoachRolesResponse } from '@/api/generated';
import { freeSubscription } from '@/test/planPicker';
import { HttpResponse, apiUrl, http } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';

/** Serves the coach roles lookup and the shell loads (subscription, inbox badge, notifications). */
export function serveCoachShell(coachRoles: string[]): void {
  const roles: GetMyCoachRolesResponse = {
    roles: coachRoles.map((role) => ({ role, clientCount: 0, sharedWithOtherRoleCount: 0 })),
  };
  server.use(
    http.get(apiUrl('/users/me/roles'), () => HttpResponse.json(roles)),
    http.get(apiUrl('/users/me/subscription'), () => HttpResponse.json(freeSubscription)),
    http.get(apiUrl('/conversations/filter-counts'), () => HttpResponse.json({ unreadMessages: 0 })),
    http.get(apiUrl('/client/notifications'), () => HttpResponse.json({ items: [] })),
  );
}

/** Records `METHOD /path` for every request the session sends, in order; call `stop()` when done. */
export function recordRequests(): { requests: string[]; stop: () => void } {
  const requests: string[] = [];
  const listener = ({ request }: { request: Request }) => {
    requests.push(`${request.method} ${new URL(request.url).pathname}`);
  };
  server.events.on('request:start', listener);
  return { requests, stop: () => server.events.removeListener('request:start', listener) };
}
