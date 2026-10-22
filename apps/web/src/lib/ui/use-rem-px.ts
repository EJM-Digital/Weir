import { useState } from "react";

const DEFAULT_REM_PX = 16;

/** The size of one rem in pixels, read once: the type scale is in rem, so measured space is compared in rem. */
export function useRemPx(): number {
  const [rem] = useState(
    () =>
      parseFloat(getComputedStyle(document.documentElement).fontSize) ||
      DEFAULT_REM_PX,
  );
  return rem;
}
