import { useEffect, useRef } from "react";

/**
 * Calls `onFrame` with the time of every animation frame while the component is mounted. The browser pauses
 * frames for a tab nobody is looking at, so nothing is drawn there. The latest `onFrame` is the one called,
 * so it may close over the current props without restarting the loop.
 */
export function useAnimationFrame(onFrame: (time: number) => void): void {
  const latest = useRef(onFrame);
  useEffect(() => {
    latest.current = onFrame;
  });
  useEffect(() => {
    let handle = 0;
    const step = (time: number) => {
      latest.current(time);
      handle = requestAnimationFrame(step);
    };
    handle = requestAnimationFrame(step);
    return () => cancelAnimationFrame(handle);
  }, []);
}
