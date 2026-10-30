import { useCallback, useEffect, useMemo, useRef, useState } from "react";

import { errorMessage } from "../../../../lib/api/error-message";
import type { ProcessingRuleSetWrite } from "../../../../lib/processing/rule-sets-api";
import { useProcessingRulesPreview } from "../../../../lib/processing/libraries-queries";
import type {
  ProcessingRulesPreviewRequest,
  ProcessingRulesPreviewResult,
} from "../../../../lib/processing/rules-preview-api";

/**
 * Long enough to wait for a pause in typing, short enough not to feel slow: a re-run on every
 * keystroke would queue behind the server's one-preview-at-a-time gate.
 */
const RERUN_DEBOUNCE_MS = 700;

export type PreviewTarget =
  | { mode: "library"; relativePath: string }
  | { mode: "anywhere"; absolutePath: string };

/**
 * Previews the rules on one file, and runs again a moment after the rules, the file or the library
 * change, so the result always matches what is on screen, saved or not.
 */
export function useRulesPreview({
  active,
  libraryId,
  target,
  rules,
}: {
  active: boolean;
  libraryId: number | "";
  target: PreviewTarget;
  rules: ProcessingRuleSetWrite | null;
}) {
  const { mutate, isPending } = useProcessingRulesPreview();
  const [result, setResult] = useState<ProcessingRulesPreviewResult | null>(
    null,
  );
  const [error, setError] = useState<string | null>(null);
  // The last request sent, and a change that came while one was running: the server runs one preview at a time and answers a
  // second with 409, which would wipe the result of the first.
  const sent = useRef<ProcessingRulesPreviewRequest | null>(null);
  const waiting = useRef<ProcessingRulesPreviewRequest | null>(null);

  const path =
    target.mode === "library"
      ? target.relativePath.trim()
      : target.absolutePath;
  const request = useMemo<ProcessingRulesPreviewRequest | null>(() => {
    if (!active || !path || libraryId === "") return null;
    return {
      libraryId,
      relativePath: target.mode === "library" ? path : undefined,
      absolutePath: target.mode === "anywhere" ? path : undefined,
      rules: rules ?? undefined,
    };
  }, [active, path, libraryId, target.mode, rules]);

  const run = useCallback(
    (next: ProcessingRulesPreviewRequest) => {
      sent.current = next;
      waiting.current = null;
      setError(null);
      mutate(next, {
        onSuccess: (data) => setResult(data),
        onError: (err) => {
          setResult(null);
          setError(errorMessage(err, "That file could not be previewed."));
        },
      });
    },
    [mutate],
  );

  useEffect(() => {
    if (!request) return undefined;
    const timer = setTimeout(() => {
      if (request === sent.current) return;
      if (isPending) {
        waiting.current = request;
        return;
      }
      run(request);
    }, RERUN_DEBOUNCE_MS);
    return () => clearTimeout(timer);
  }, [request, run, isPending]);

  useEffect(() => {
    if (!isPending && waiting.current) run(waiting.current);
  }, [isPending, run]);

  return {
    result,
    error,
    running: isPending,
    /** Null until there is a library and a file to preview. */
    runNow: request ? () => run(request) : null,
  };
}
