import { useCallback, useEffect, useRef, useState } from 'react';
import { AppState } from 'react-native';
import * as Haptics from 'expo-haptics';

const TICK_MS = 500;
const MS_PER_MINUTE = 60_000;
const MS_PER_SECOND = 1000;

type StepTimer = { status: 'running'; endsAt: number } | { status: 'done' };

export type StepTimerView =
  | { status: 'idle' }
  | { status: 'running'; remainingSeconds: number }
  | { status: 'done' };

type Timers = Readonly<Record<number, StepTimer>>;

const hasRunning = (timers: Timers): boolean => Object.values(timers).some((timer) => timer.status === 'running');

/**
 * Per-step countdowns for cook mode. Each timer counts down to a fixed end time, so the remaining time stays correct
 * after the app was in the background; when it reaches zero it gives one haptic and reads as done. The interval is
 * cleared when no timer runs and on unmount. Nothing is scheduled outside the app.
 */
export function useStepTimers() {
  const [timers, setTimers] = useState<Timers>({});
  const [now, setNow] = useState(() => Date.now());
  const timersRef = useRef<Timers>(timers);

  const commit = useCallback((next: Timers) => {
    timersRef.current = next;
    setTimers(next);
  }, []);

  const tick = useCallback(() => {
    const current = Date.now();
    setNow(current);

    const expired = Object.entries(timersRef.current).filter(
      ([, timer]) => timer.status === 'running' && timer.endsAt <= current,
    );
    if (expired.length === 0) return;

    const next: Record<number, StepTimer> = { ...timersRef.current };
    for (const [index] of expired) {
      next[Number(index)] = { status: 'done' };
    }
    commit(next);
    void Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success).catch(() => undefined);
  }, [commit]);

  const running = hasRunning(timers);

  useEffect(() => {
    if (!running) return undefined;
    const interval = setInterval(tick, TICK_MS);
    const subscription = AppState.addEventListener('change', (state) => {
      if (state === 'active') tick();
    });
    return () => {
      clearInterval(interval);
      subscription.remove();
    };
  }, [running, tick]);

  const start = useCallback(
    (index: number, minutes: number) => {
      const current = timersRef.current[index];
      if (current?.status === 'running') return;
      const startedAt = Date.now();
      setNow(startedAt);
      commit({ ...timersRef.current, [index]: { status: 'running', endsAt: startedAt + minutes * MS_PER_MINUTE } });
    },
    [commit],
  );

  const viewOf = useCallback(
    (index: number): StepTimerView => {
      const timer = timers[index];
      if (timer === undefined) return { status: 'idle' };
      if (timer.status === 'done') return { status: 'done' };
      return { status: 'running', remainingSeconds: Math.max(0, Math.ceil((timer.endsAt - now) / MS_PER_SECOND)) };
    },
    [timers, now],
  );

  return { start, viewOf };
}
