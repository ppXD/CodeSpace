import { useEffect, useRef, useState } from "react";

import { tasksApi, type RoutePlan } from "@/api/tasks";

/** Fire after the goal has been stable this long — the classifier may be a model call; keystrokes must never race it. */
export const ROUTE_PREVIEW_DEBOUNCE_MS = 700;
/** Below this the goal is too thin to classify honestly — no call, no card. */
export const ROUTE_PREVIEW_MIN_GOAL_LENGTH = 12;

/**
 * B1: the route-preview lane's debounced fetch. Once the goal settles, ask the backend where this launch WOULD
 * go — which effort tier, recipe and projection, under which bounds, and whether the router wants the operator
 * to confirm before anything runs. Read-only end to end: the endpoint opens no session and stages no run.
 *
 * <p>Only asked on the AUTO tier: an explicitly chosen tier is already the operator's decision, so there is
 * nothing to preview and nothing to confirm. `enabled: false` therefore means no call AND no card, and the
 * composer's launch gate reads clear again the instant a tier is picked.</p>
 *
 * <p>Best-effort BY DESIGN — a transport fault yields `failed: true` and NO card, so the launch stays allowed.
 * A preview outage must never be able to block launching, which is exactly the failure mode a hard gate on an
 * optional enhancement would create.</p>
 *
 * <p>Staleness is handled by DERIVATION, not by clearing state in the effect (the lint-enforced
 * no-sync-setState-in-effect rule): every reply is stored WITH the (goal, repo, effort) key that produced it
 * and exposed only while that key is current; a sequence guard drops out-of-order resolutions of one key.</p>
 */
export function useRoutePreview(goal: string, repositoryId: string | null | undefined, effort: string, enabled: boolean) {
  const [reply, setReply] = useState<{ key: string; route: RoutePlan | null } | null>(null);
  const [pendingKey, setPendingKey] = useState<string | null>(null);
  const seq = useRef(0);

  const text = goal.trim();
  const key = !enabled || text.length < ROUTE_PREVIEW_MIN_GOAL_LENGTH ? null : `${text} ${repositoryId ?? ""} ${effort}`;

  useEffect(() => {
    const mySeq = ++seq.current;

    if (key === null) return;

    const timer = setTimeout(async () => {
      setPendingKey(key);
      try {
        const result = await tasksApi.routePreview({ taskText: text, repositoryId: repositoryId ?? undefined, effort });
        if (seq.current !== mySeq) return;
        setReply({ key, route: result.route ?? null });
      } catch {
        // A failed preview is NOT a failed launch — record the miss so the composer can say so, and leave the
        // route null so no card is rendered and nothing is gated.
        if (seq.current === mySeq) setReply({ key, route: null });
      } finally {
        if (seq.current === mySeq) setPendingKey(p => (p === key ? null : p));
      }
    }, ROUTE_PREVIEW_DEBOUNCE_MS);

    return () => clearTimeout(timer);
  }, [key, text, repositoryId, effort]);

  const current = key !== null && reply?.key === key ? reply : null;

  return {
    route: current?.route ?? null,
    /** A reply arrived for the current key but carried no route — the preview is unavailable; say so, gate nothing. */
    failed: current !== null && current.route === null,
    loading: pendingKey !== null && pendingKey === key,
  };
}
