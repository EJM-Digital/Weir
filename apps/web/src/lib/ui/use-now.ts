import { useEffect, useState } from "react";

/**
 * The current time, refreshed on every whole `intervalMs` of the clock while the tab is visible, so "min ago" and
 * countdowns move between server updates. Paused while the tab is hidden, so anything driven by it —
 * such as Processing's overdue-look check — cannot keep refetching every few seconds in a background
 * tab forever (#719). Ticks once immediately on becoming visible again, so the display catches up
 * without waiting for the next interval. Ticks land on the clock's own boundaries, not a fixed distance apart, so a
 * countdown shows every whole second once instead of drifting against it.
 */
export function useNow(intervalMs: number): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    let id: number | null = null;
    function arm() {
      id = window.setTimeout(
        () => {
          tick();
          arm();
        },
        intervalMs - (Date.now() % intervalMs),
      );
    }
    function tick() {
      setNow(Date.now());
    }
    function start() {
      tick();
      arm();
    }
    function stop() {
      if (id != null) {
        window.clearTimeout(id);
        id = null;
      }
    }
    function onVisibilityChange() {
      if (document.hidden) {
        stop();
      } else if (id == null) {
        start();
      }
    }
    if (!document.hidden) start();
    document.addEventListener("visibilitychange", onVisibilityChange);
    return () => {
      document.removeEventListener("visibilitychange", onVisibilityChange);
      stop();
    };
  }, [intervalMs]);
  return now;
}
