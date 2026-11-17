import { create } from 'zustand';

/** Polling cadence while the checkout is awaiting its webhook: quick at first, then slower, then none. */
export const CHECKOUT_WATCH_FAST_PHASE_MS = 120_000;
export const CHECKOUT_WATCH_SLOW_PHASE_MS = 900_000;
export const CHECKOUT_WATCH_FAST_INTERVAL_MS = 5_000;
export const CHECKOUT_WATCH_SLOW_INTERVAL_MS = 30_000;

const STORAGE_KEY = 'checkoutActivationWatch';

interface StoredWatch {
  startedAt: number;
  userId: string;
  /** Plan an in-place upgrade waits for; absent for a checkout watch. */
  targetPlanCode?: string;
}

function isStoredWatch(value: unknown): value is StoredWatch {
  if (typeof value !== 'object' || value === null) return false;
  const candidate = value as Record<string, unknown>;
  return (
    typeof candidate.startedAt === 'number' &&
    Number.isFinite(candidate.startedAt) &&
    typeof candidate.userId === 'string' &&
    candidate.userId.length > 0 &&
    (candidate.targetPlanCode === undefined ||
      (typeof candidate.targetPlanCode === 'string' && candidate.targetPlanCode.length > 0))
  );
}

function clearStoredWatch(): void {
  try {
    sessionStorage.removeItem(STORAGE_KEY);
  } catch {
    // Storage unavailable: the in-memory watch still works for this page load.
  }
}

function writeStoredWatch(watch: StoredWatch): void {
  try {
    sessionStorage.setItem(STORAGE_KEY, JSON.stringify(watch));
  } catch {
    // Storage unavailable: the watch simply does not survive a reload.
  }
}

/** Reads the watch a previous page load left behind; drops it when malformed, expired or from the future. */
function readStoredWatch(): StoredWatch | null {
  try {
    const raw = sessionStorage.getItem(STORAGE_KEY);
    if (raw === null) return null;
    const parsed: unknown = JSON.parse(raw);
    const now = Date.now();
    if (isStoredWatch(parsed) && parsed.startedAt <= now && now - parsed.startedAt < CHECKOUT_WATCH_SLOW_PHASE_MS) {
      return parsed;
    }
  } catch {
    // Fall through: unreadable or unparsable data is treated as no watch.
  }
  clearStoredWatch();
  return null;
}

/** Poll interval for a watch started at `startedAt`; false once polling is over (focus refresh continues). */
export function checkoutWatchInterval(startedAt: number, now: number): number | false {
  const elapsed = now - startedAt;
  if (elapsed < CHECKOUT_WATCH_FAST_PHASE_MS) return CHECKOUT_WATCH_FAST_INTERVAL_MS;
  if (elapsed < CHECKOUT_WATCH_SLOW_PHASE_MS) return CHECKOUT_WATCH_SLOW_INTERVAL_MS;
  return false;
}

interface CheckoutActivationState {
  /** Epoch ms when the return page gave up waiting; null when no checkout is awaiting its webhook. */
  startedAt: number | null;
  /** The coach (publicId) who made the purchase; the watch is only valid for that session. */
  userId: string | null;
  /** The plan a pending upgrade must land on; null for a checkout watch, which settles on any paid plan. */
  targetPlanCode: string | null;
  start: (userId: string, targetPlanCode?: string) => void;
  stop: () => void;
}

const initial = readStoredWatch();

export const useCheckoutActivationStore = create<CheckoutActivationState>((set) => ({
  startedAt: initial?.startedAt ?? null,
  userId: initial?.userId ?? null,
  targetPlanCode: initial?.targetPlanCode ?? null,
  start: (userId, targetPlanCode) => {
    const startedAt = Date.now();
    writeStoredWatch({ startedAt, userId, ...(targetPlanCode ? { targetPlanCode } : {}) });
    set({ startedAt, userId, targetPlanCode: targetPlanCode ?? null });
  },
  stop: () => {
    clearStoredWatch();
    set({ startedAt: null, userId: null, targetPlanCode: null });
  },
}));
