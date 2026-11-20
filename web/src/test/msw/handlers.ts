import { http, HttpResponse } from 'msw';
import type { RequestHandler } from 'msw';

// Relative axios URLs resolve against the jsdom origin (see vitest.config.ts).
export const apiUrl = (path: string) => `http://localhost${path}`;

/** Shared handlers. Empty on purpose: tests declare what they expect with `server.use(...)`. */
export const defaultHandlers: RequestHandler[] = [];

/** An RFC 7807-style error body the way the backend sends it. */
export function problem(status: number, code: string) {
  return HttpResponse.json(
    { errors: [{ name: 'generalErrors', reason: 'invalid', code }] },
    { status, headers: { 'Content-Type': 'application/problem+json' } },
  );
}

export { http, HttpResponse };
