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

/**
 * An RFC 7807 problem body with a top-level `status` and `errorCode`, the shape the billing and admin endpoints
 * send; the API client reads the status off the body for the endpoints that declare a problem response.
 */
export function rfc7807(status: number, errorCode: string) {
  return HttpResponse.json({ status, title: 'Problem', errorCode }, { status, headers: { 'Content-Type': 'application/problem+json' } });
}

export { http, HttpResponse };
