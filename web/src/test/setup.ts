import '@testing-library/jest-dom/vitest';
import { cleanup, configure } from '@testing-library/react';
import { afterAll, afterEach, beforeAll } from 'vitest';
import { server } from '@/test/msw/server';

// findBy*/waitFor default to 1 s; a page still loading on a busy runner then fails as "Unable to find".
configure({ asyncUtilTimeout: 5_000 });

// i18n reads this key at import time; pin English so copy assertions are stable.
localStorage.setItem('lang', 'en');

// Any request without a handler is a test bug, not a network call. msw's 'error' only fails the request,
// and pages often swallow a failed load, so record each one and fail the test after it runs.
const unhandledRequests: string[] = [];
beforeAll(() =>
  server.listen({
    onUnhandledFrame({ frame, defaults }) {
      defaults.error();
      const { request } = frame.data as { request?: Request };
      if (request) {
        const url = new URL(request.url);
        unhandledRequests.push(`${request.method} ${url.pathname}${url.search}`);
      }
    },
  }),
);
afterEach(() => {
  cleanup();
  server.resetHandlers();
  const found = unhandledRequests.splice(0);
  if (found.length > 0) {
    throw new Error(`Requests without an msw handler (add one to the test's fakes):\n  ${found.join('\n  ')}`);
  }
});
afterAll(() => server.close());

// jsdom gaps, added only where a test hit them. Radix menus and dialogs need pointer capture
// and scrollIntoView; its size observers need ResizeObserver; the inbox thread scrolls with scrollTo.
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
globalThis.ResizeObserver ??= ResizeObserverStub;
Element.prototype.hasPointerCapture ??= () => false;
Element.prototype.setPointerCapture ??= () => {};
Element.prototype.releasePointerCapture ??= () => {};
Element.prototype.scrollIntoView ??= () => {};
Element.prototype.scrollTo ??= () => {};
// The theme store reads the OS colour scheme when it is imported (the landing nav's theme toggle).
window.matchMedia ??= (query: string): MediaQueryList =>
  ({
    matches: false,
    media: query,
    onchange: null,
    addEventListener() {},
    removeEventListener() {},
    addListener() {},
    removeListener() {},
    dispatchEvent: () => false,
  }) satisfies MediaQueryList;
