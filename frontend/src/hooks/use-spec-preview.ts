import { useEffect, useRef, useState } from "react";

import { tasksApi, type TaskSpecSuggestion } from "@/api/tasks";

/** Fire after the goal has been stable this long — the compile is a model call; keystrokes must never race it. */
export const SPEC_PREVIEW_DEBOUNCE_MS = 800;
/** Below this the goal is too thin to compile anything honest — no call, no card. */
export const SPEC_PREVIEW_MIN_GOAL_LENGTH = 12;

/**
 * The spec-preview lane's debounced fetch (P5-7): once the goal settles, ask the backend to compile it into
 * launch-contract suggestions. Best-effort BY DESIGN — a null suggestion (no structured model, transport fault,
 * thin goal) simply renders no card; an error is indistinguishable from "nothing to suggest" on purpose, because
 * the launch composer must never surface a failure for an optional enhancement. A sequence guard drops stale
 * replies when the goal or repo changes mid-flight.
 */
export function useSpecPreview(goal: string, repositoryId?: string | null) {
  const [suggestion, setSuggestion] = useState<TaskSpecSuggestion | null>(null);
  const [grounded, setGrounded] = useState(false);
  const [loading, setLoading] = useState(false);
  const seq = useRef(0);

  useEffect(() => {
    const text = goal.trim();
    const mySeq = ++seq.current;

    if (text.length < SPEC_PREVIEW_MIN_GOAL_LENGTH) {
      setSuggestion(null);
      setLoading(false);
      return;
    }

    const timer = setTimeout(async () => {
      setLoading(true);
      try {
        const result = await tasksApi.specPreview({ goal: text, repositoryId: repositoryId ?? undefined });
        if (seq.current !== mySeq) return;
        setSuggestion(result.suggestion ?? null);
        setGrounded(result.grounded);
      } catch {
        if (seq.current === mySeq) setSuggestion(null);
      } finally {
        if (seq.current === mySeq) setLoading(false);
      }
    }, SPEC_PREVIEW_DEBOUNCE_MS);

    return () => clearTimeout(timer);
  }, [goal, repositoryId]);

  return { suggestion, grounded, loading };
}
