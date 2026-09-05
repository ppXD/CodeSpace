import { useEffect, useRef, useState } from "react";

import { tasksApi, type RoutePlan, type RoutePreviewInput } from "@/api/tasks";

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

/**
 * B1: the route-preview lane's debounced fetch. Once the goal settles, ask the backend where this launch WOULD
 * go — which effort tier, recipe and projection, under which bounds, and whether the router wants the operator
 * to confirm before anything runs. Read-only end to end: the endpoint opens no session and stages no run.
 *
 * <p>Pass `null` to disable (the composer does so for an explicitly chosen tier — that is already the operator's
 * decision, so there is nothing to preview and nothing to confirm). Disabled reads as ANSWERED, so the launch
 * gate opens immediately.</p>
 *
 * <p><b>`answered` is the load-bearing return value, not `route`.</b> A gate built on `route?.needsConfirmCard`
 * alone is OPEN for the whole debounce window and the whole in-flight request — one to three seconds in which a
 * risky goal can be typed and launched before the router has said a word. `answered` is false from the moment
 * the goal changes until a reply (or a failure) lands for THAT goal, so the composer can hold Launch until the
 * question has actually been asked and answered.</p>
 *
 * <p>Failure still opens the gate: a transport fault records a reply with a null route, which makes `answered`
 * true and `failed` true — the composer says the preview is unavailable and allows the launch. Only a genuinely
 * outstanding question closes it. This is the deliberate trade: a preview OUTAGE must not be able to block
 * launching, but a preview still IN FLIGHT must.</p>
 *
 * <p>Staleness is handled by DERIVATION, not by clearing state in the effect (the lint-enforced
 * no-sync-setState-in-effect rule): every reply is stored WITH the key that produced it and exposed only while
 * that key is current; a sequence guard drops out-of-order resolutions of one key.</p>
 */
export function useRoutePreview(input: RoutePreviewInput | null) {
  const [reply, setReply] = useState<{ key: string; route: RoutePlan | null; deploymentAutonomyCeiling: string } | null>(null);
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
        const result = await tasksApi.routePreview(JSON.parse(key) as RoutePreviewInput);
        if (seq.current !== mySeq) return;
        setReply({ key, route: result.route ?? null, deploymentAutonomyCeiling: result.deploymentAutonomyCeiling ?? "" });
      } catch {
        // A failed preview is NOT a failed launch — record the miss (which counts as ANSWERED, so the gate
        // opens) and leave the route null so no card renders and nothing is blocked.
        if (seq.current === mySeq) setReply(prev => ({ key, route: null, deploymentAutonomyCeiling: prev?.deploymentAutonomyCeiling ?? "" }));
      } finally {
        if (seq.current === mySeq) setPendingKey(p => (p === key ? null : p));
      }
    }, ROUTE_PREVIEW_DEBOUNCE_MS);

    return () => clearTimeout(timer);
  }, [key]);

  const current = key !== null && reply?.key === key ? reply : null;

  return {
    route: current?.route ?? null,
    /** A reply arrived for the current key but carried no route — the preview is unavailable; say so, gate nothing. */
    failed: current !== null && current.route === null,
    loading: pendingKey !== null && pendingKey === key,
    /** Whether the question for the CURRENT input has been settled. False through the debounce window AND the in-flight request; true when disabled or when a reply/failure has landed. */
    answered: key === null || current !== null,
    /** This deployment's autonomy ceiling, read off the NEWEST reply rather than the current key's — deliberately
     *  un-keyed, because unlike a route it is a constant of the deployment, so the last observation can never be
     *  stale for a different request. It therefore survives the operator switching to an explicit tier, which
     *  disables the preview entirely. "" until some reply has carried it (the composer then states today's wording;
     *  the SERVER clamps either way). */
    deploymentAutonomyCeiling: reply?.deploymentAutonomyCeiling ?? "",
  };
}
