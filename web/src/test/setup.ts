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

// Add jsdom gaps here only when a test hits them (Radix: ResizeObserver, pointer capture,
// scrollIntoView; media queries: matchMedia). The pilot page needs none of them yet.
