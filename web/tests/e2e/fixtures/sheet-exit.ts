import type { Locator, Page } from '@playwright/test';

export interface ExitRecord {
  closedWhileConnected: boolean;
  closedAnimationName: string;
}

type RecorderWindow = Window & { __sheetExit?: Record<string, ExitRecord> };

/**
 * Records, inside the page, whether the element was seen with
 * `data-state="closed"` while still connected, and its computed
 * `animation-name` at that moment. Exit animations (200ms) are shorter than a
 * few sequential Playwright round trips under load, so the closed state has to
 * be captured in the page rather than polled for after the click.
 */
export async function recordExit(locator: Locator, key: string): Promise<void> {
  await locator.evaluate((element, recorderKey) => {
    const recorderWindow = window as RecorderWindow;
    const records = (recorderWindow.__sheetExit ??= {});
    records[recorderKey] = { closedWhileConnected: false, closedAnimationName: '' };
    new MutationObserver(() => {
      if (element.getAttribute('data-state') === 'closed' && element.isConnected) {
        records[recorderKey] = {
          closedWhileConnected: true,
          closedAnimationName: getComputedStyle(element).animationName,
        };
      }
    }).observe(element, { attributes: true, attributeFilter: ['data-state'] });
  }, key);
}

export const readExit = (page: Page, key: string): Promise<ExitRecord | undefined> =>
  page.evaluate((recorderKey) => (window as RecorderWindow).__sheetExit?.[recorderKey], key);
