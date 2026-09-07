import { useEffect, useRef, useState } from "react";

import { tasksApi, type RoutePlan, type RoutePreviewInput, type TaskAcceptanceCompatibility } from "@/api/tasks";

/** Fire after the goal has been stable this long — the classifier may be a model call; keystrokes must never race it. */
export const ROUTE_PREVIEW_DEBOUNCE_MS = 700;
/**
 * The shortest goal worth asking about. Deliberately TINY: this is the launch GATE's input, so anything the
 * minimum skips is a task that launches with no confirm at all — and short goals are exactly where the danger
 * lives ("drop prod db" is 12 characters, "rm -rf /" is 8). The earlier 12-character floor, copied from the
 * spec-preview lane, silently exempted them. Only a goal too short to classify at all is skipped, and the
 * composer's own non-blank requirement already blocks launching those.
 */
export const ROUTE_PREVIEW_MIN_GOAL_LENGTH = 3;

/** Debounced routing preview bound to the full input. Responses are exposed only for the current key and
 * generation; an expiring reference triggers a refresh. A failed preview remains an explicit legacy-path fallback.
 * Routing advice is not consent. Actual launch authority is checked by the server on every request. */
type PreviewReply = { key: string; generation: number; route: RoutePlan | null; deploymentAutonomyCeiling: string; routeSnapshotId?: string; refreshAfterMs?: number; launchAttempted?: boolean; acceptanceCompatibility?: TaskAcceptanceCompatibility | null };

export function useRoutePreview(input: RoutePreviewInput | null) {
  const [{ reply, generation }, setState] = useState<{ reply: PreviewReply | null; generation: number }>({ reply: null, generation: 0 });
  const [pendingKey, setPendingKey] = useState<string | null>(null);
  const seq = useRef(0);

  // The key IS the serialized request — so it identifies the reply AND carries the payload, which removes any
  // question of the effect firing with a newer input than the key it was scheduled for.
  const key = input === null || input.taskText.trim().length < ROUTE_PREVIEW_MIN_GOAL_LENGTH
    ? null
    : JSON.stringify(input);

  useEffect(() => {
    const mySeq = ++seq.current;

    if (key === null) return;

    const timer = setTimeout(async () => {
      setPendingKey(key);
      try {
        const startedAt = performance.now();
        const result = await tasksApi.routePreview(JSON.parse(key) as RoutePreviewInput);
        if (seq.current !== mySeq) return;
        const lifetime = Date.parse(result.expiresAt ?? "") - Date.parse(result.createdAt ?? "");
        const refreshAfterMs = Number.isFinite(lifetime) ? Math.max(0, lifetime - (performance.now() - startedAt)) : undefined;
        setState(previous => ({ ...previous, reply: { key, generation, route: result.route ?? null, deploymentAutonomyCeiling: result.deploymentAutonomyCeiling ?? "", routeSnapshotId: result.routeSnapshotId, refreshAfterMs, acceptanceCompatibility: result.acceptanceCompatibility } }));
      } catch {
        // A failed preview is NOT a failed launch — record the miss (which counts as ANSWERED, so the gate
        // opens for ordinary launch) and leave compatibility unknown for mandatory command adoption.
        if (seq.current === mySeq) setState(previous => ({ ...previous, reply: { key, generation, route: null, deploymentAutonomyCeiling: previous.reply?.deploymentAutonomyCeiling ?? "" } }));
      } finally {
        if (seq.current === mySeq) setPendingKey(p => (p === key ? null : p));
      }
    }, ROUTE_PREVIEW_DEBOUNCE_MS);

    return () => clearTimeout(timer);
  }, [key, generation]);

  const current = key !== null && reply?.key === key && reply.generation === generation ? reply : null;
  const refreshAfterMs = current?.refreshAfterMs;
  const launchAttempted = current?.launchAttempted;

  useEffect(() => {
    if (refreshAfterMs === undefined || launchAttempted) return;
    const timer = setTimeout(() => setState(previous => ({ ...previous, generation: previous.generation + 1 })), refreshAfterMs);
    return () => clearTimeout(timer);
  }, [refreshAfterMs, launchAttempted]);

  return {
    route: current?.route ?? null,
    routeSnapshotId: current?.routeSnapshotId,
    acceptanceCompatibility: current?.acceptanceCompatibility ?? null,
    /** Preserve an attempted reference through expiry until the server resolves whether it committed a run. */
    markLaunchAttempt: (snapshotId: string) => setState(previous => previous.reply?.routeSnapshotId === snapshotId ? { ...previous, reply: { ...previous.reply, launchAttempted: true } } : previous),
    /** Only a conclusive success or rejection ends this admission intent; transport failures keep the reference. */
    releaseReference: (snapshotId: string) => setState(previous => previous.reply?.routeSnapshotId === snapshotId ? { reply: null, generation: previous.generation + 1 } : previous),
    /** A reply arrived for the current key but carried no route — the preview is unavailable; say so, gate nothing. */
    failed: current !== null && current.route === null,
    loading: pendingKey !== null && pendingKey === key,
    /** Whether the question for the CURRENT input has been settled. False through the debounce window AND the in-flight request; true when disabled or when a reply/failure has landed. */
    answered: key === null || current !== null,
    /** This deployment's autonomy ceiling, read off the NEWEST reply rather than the current key's — deliberately
     *  un-keyed, because unlike a route it is a constant of the deployment, so the last observation can never be
     *  stale for a different request. Explicit tiers also request adapter metadata, without model classification. "" until some reply has carried it (the composer then states today's wording;
     *  the SERVER clamps either way). */
    deploymentAutonomyCeiling: reply?.deploymentAutonomyCeiling ?? "",
  };
}
