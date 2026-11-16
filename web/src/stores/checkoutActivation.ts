import { create } from 'zustand';

/** How long the coach's subscription is polled after a checkout whose webhook has not landed yet. */
export const CHECKOUT_WATCH_WINDOW_MS = 120_000;

interface CheckoutActivationState {
  /** Epoch ms until which the watcher polls; null when no checkout is awaiting its webhook. Not persisted. */
  watchUntil: number | null;
  start: () => void;
  stop: () => void;
}

export const useCheckoutActivationStore = create<CheckoutActivationState>((set) => ({
  watchUntil: null,
  start: () => set({ watchUntil: Date.now() + CHECKOUT_WATCH_WINDOW_MS }),
  stop: () => set({ watchUntil: null }),
}));
