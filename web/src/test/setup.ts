import '@testing-library/jest-dom/vitest';
import { cleanup } from '@testing-library/react';
import { afterAll, afterEach, beforeAll } from 'vitest';
import { server } from '@/test/msw/server';

// i18n reads this key at import time; pin English so copy assertions are stable.
localStorage.setItem('lang', 'en');

// Any request without a handler is a test bug, not a network call.
beforeAll(() => server.listen({ onUnhandledFrame: 'error' }));
afterEach(() => {
  cleanup();
  server.resetHandlers();
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
