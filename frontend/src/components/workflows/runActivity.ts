import type { PhaseAgentRef, RunPhase, RunTimelineEvent } from "@/api/workflows";

import { parseIterationKey } from "./mapBranches";
import type { RerunTarget } from "./RerunMenu";

/** Agent-run statuses (and the node-status "Failure" fallback for a missing row) that count as a genuine failure to rerun — NOT the catch-all `tileState==="failed"`, which folds the Pending/Skipped missing-row fallbacks in too. */
const FAILED_STATUSES = new Set(["Failed", "Cancelled", "TimedOut", "NeedsReview", "Failure"]);

/** The failed top-level map-branch indices of a <c>kind: "map"</c> wave (each agent keyed <c>"&lt;wave.id&gt;#&lt;i&gt;"</c>). */
export function failedMapIndices(wave: AgentWave): number[] {
  if (wave.kind !== "map") return [];
  return wave.agents
    .filter((a) => FAILED_STATUSES.has(a.status))
    .map((a) => parseIterationKey(a.iterationKey ?? ""))
    .filter((s) => s.length === 1 && s[0].containerId === wave.id)
    .map((s) => s[0].index)
    .sort((x, y) => x - y);
}

/** A wave's PHASE-LEVEL rerun target: a map fan-out → bulk "Rerun N failed items"; a failed agent step → "Rerun from here"; otherwise none (supervisor / authored / plain phases are model-owned or non-rerunnable here). */
export function phaseRerunTarget(wave: AgentWave, rerunnableNodeIds?: ReadonlySet<string>): RerunTarget | null {
  if (wave.kind === "map") {
    const failed = failedMapIndices(wave);
    return failed.length > 0 ? { kind: "mapItem", mapNodeId: wave.id, failedIndices: failed, totalCount: wave.agents.length } : null;
  }
  if ((wave.kind === "agent" || wave.kind === "node") && wave.agents.some((a) => FAILED_STATUSES.has(a.status))) {
    // Offer "Rerun from here" only where the server's gate would ACCEPT a from-node rerun for this node (its closure
    // has no suspendable/container node). When the gate set is absent (no run-detail in scope), fall back to offering
    // — the endpoint's 422 stays the honest backstop.
    if (rerunnableNodeIds && !rerunnableNodeIds.has(wave.id)) return null;
    return { kind: "node", nodeId: wave.id };
  }
  return null;
}

/** The per-item rerun target for one FOCUSED agent of a map wave (its terminal open) — "Rerun item #i". Null if the agent isn't a top-level map branch of this wave. */
export function itemRerunTarget(agent: PhaseAgentRef, wave: AgentWave): RerunTarget | null {
  if (wave.kind !== "map") return null;
  const segs = parseIterationKey(agent.iterationKey ?? "");
  if (segs.length !== 1 || segs[0].containerId !== wave.id) return null;
  return { kind: "mapItem", mapNodeId: wave.id, focusedIndex: segs[0].index, failedIndices: failedMapIndices(wave), totalCount: wave.agents.length };
}

/**
 * The run's pure phase/agent model — groups the phase tree into agent WAVES (each phase's claimed agents) and the
 * shared agent-status helpers the outline + the Activity tiles both read. Source-agnostic: it never switches on a
 * phase `kind` beyond ranking authored phases over the raw ones, so a single-agent run, a map fan-out, and a
 * supervisor wave all flow through the same path. No React, no hooks — unit-testable.
 */

/** An agent wave — the agents one phase claimed (deduped so an agent never appears in two waves). */
export interface AgentWave {
  /** The owning phase id (the React key + the outline / tiles filter key). For a <c>kind: "map"</c> wave this is the flow.map node id the Rerun control forks from. */
  id: string;
  /** The owning phase kind — "map" marks a flow.map fan-out (rerunnable per item); "phase"/"agent"/"node"/"decision" otherwise. */
  kind: string;
  /** The phase label ("Implement", "Spawn 3 agents", "code"). */
  label: string;
  /** When the phase began (null until it has started). */
  startedAt: string | null;
  /** The agents this wave OWNS. */
  agents: PhaseAgentRef[];
}

/** Authored semantic phases ("phase") own their agents over the raw decision / node / map phases that also list them. */
function phaseRank(p: RunPhase): number {
  return p.kind === "phase" ? 1 : 0;
}

/**
 * Group the phase tree into agent WAVES, assigning each agent run to exactly ONE phase so it never renders twice. A
 * supervisor run lists the same agent under its spawn decision AND its authored semantic phase — the authored phase
 * wins (higher rank, then earliest `order`), and the decision phase keeps only agents no authored phase claimed. A
 * node / map run has one phase per agent, so each becomes its own wave. A phase left with no claimed agents (e.g. a
 * plan / stop decision, or a spawn whose agents the authored phase took) contributes no wave. Waves keep phase order.
 */
export function buildWaves(phases: readonly RunPhase[]): AgentWave[] {
  const bestByAgent = new Map<string, RunPhase>();

  for (const p of phases) {
    for (const a of p.agents) {
      const cur = bestByAgent.get(a.agentRunId);
      const better = !cur || phaseRank(p) > phaseRank(cur) || (phaseRank(p) === phaseRank(cur) && p.order < cur.order);
      if (better) bestByAgent.set(a.agentRunId, p);
    }
  }

  return phases
    .map((p): AgentWave => ({
      id: p.id,
      kind: p.kind,
      label: p.label,
      startedAt: p.startedAt ?? null,
      // The agents this phase claimed, also collapsed by id so a (defensively) duplicated ref in one phase's list
      // can't render the same tile twice / collide on the React key.
      agents: [...new Map(p.agents.filter((a) => bestByAgent.get(a.agentRunId) === p).map((a) => [a.agentRunId, a])).values()],
    }))
    .filter((w) => w.agents.length > 0);
}

/** One item on the composed Activity timeline — a narrative event row, an agent-wave (a phase's agents), or a folded run of detail events. */
export type ActivityItem =
  | { kind: "event"; key: string; at: string; event: RunTimelineEvent }
  | { kind: "wave"; key: string; at: string | null; wave: AgentWave }
  | { kind: "fold"; key: string; at: string; events: RunTimelineEvent[] };

type EventItem = Extract<ActivityItem, { kind: "event" }>;

/**
 * Merge the narrative events + the agent waves into ONE chronological stream. Events sort by `occurredAt`; a wave sorts
 * by its phase `startedAt`, falling back to the earliest event among its own agents, then to the end (so an unanchored
 * wave never silently vanishes). On an equal timestamp an event sorts BEFORE a wave, so a wave lands just after the
 * "spawned" event that announced it. The final index tie-break keeps the sort stable + deterministic.
 */
export function mergeActivityStream(events: readonly RunTimelineEvent[], waves: readonly AgentWave[]): ActivityItem[] {
  const items: ActivityItem[] = [
    ...events.map((e): ActivityItem => ({ kind: "event", key: `e:${e.id}`, at: e.occurredAt, event: e })),
    ...waves.map((w): ActivityItem => ({ kind: "wave", key: `w:${w.id}`, at: w.startedAt ?? earliestAgentEvent(w, events), wave: w })),
  ];

  return items
    .map((it, i) => ({ it, i }))
    .sort((a, b) => cmpAt(a.it.at, b.it.at) || typeRank(a.it) - typeRank(b.it) || a.i - b.i)
    .map(({ it }) => it);
}

/**
 * The Activity timeline the UI renders: the merged event+wave stream with each run of TWO OR MORE consecutive DETAIL
 * events collapsed into one "fold" item — so the story reads as milestones + phase waves and tucks the structural churn
 * (node started/completed, file edits) behind a "N steps" disclosure at its chronological spot. A wave or a milestone
 * event flushes the run; a LONE detail stays inline (the renderer dims it). An absent level reads as a milestone
 * (forward-tolerant), never silently folded. The fold key anchors to the item BEFORE it, so a live-poll backfill of an
 * earlier-sorting detail can't reset the disclosure's open state.
 */
export function composeActivity(events: readonly RunTimelineEvent[], waves: readonly AgentWave[]): ActivityItem[] {
  const merged = mergeActivityStream(events, waves);
  const out: ActivityItem[] = [];
  let run: EventItem[] = [];
  let boundary = "start";

  const flush = () => {
    if (run.length >= 2) out.push({ kind: "fold", key: `fold:${boundary}`, at: run[0].at, events: run.map((i) => i.event) });
    else out.push(...run);
    run = [];
  };

  for (const item of merged) {
    if (item.kind === "event" && item.event.level === "Detail") { run.push(item); continue; }

    flush();
    out.push(item);
    boundary = item.key;
  }

  flush();
  return out;
}

/** The earliest event time among a wave's agents — the fallback anchor when the phase carries no startedAt yet. */
function earliestAgentEvent(wave: AgentWave, events: readonly RunTimelineEvent[]): string | null {
  const ids = new Set(wave.agents.map((a) => a.agentRunId));
  let earliest: string | null = null;

  for (const e of events) {
    if (e.agentRunId && ids.has(e.agentRunId) && (earliest === null || e.occurredAt < earliest)) earliest = e.occurredAt;
  }

  return earliest;
}

function typeRank(it: ActivityItem): number {
  return it.kind === "event" ? 0 : 1;
}

/** Compare two ISO timestamps; a null sorts LAST so an unanchored item goes to the end rather than the top. */
function cmpAt(a: string | null, b: string | null): number {
  if (a === b) return 0;
  if (a === null) return 1;
  if (b === null) return -1;
  return a < b ? -1 : 1;
}

// ── shared agent-status presentation (the tile + the outline roll-up read these) ──

/** running while in flight; done on success; waiting when queued; failed on any terminal error. The render axis. */
export type TileState = "running" | "waiting" | "done" | "failed";

export function tileState(status: string): TileState {
  if (status === "Running") return "running";
  if (status === "Queued") return "waiting";
  if (status === "Succeeded") return "done";
  return "failed";   // Failed / Cancelled / TimedOut / anything else terminal
}

/** Compact token count — thousands as "15.4k", smaller counts verbatim. */
export function formatTokens(n: number): string {
  return n >= 1000 ? `${(n / 1000).toFixed(1)}k` : `${n}`;
}

/** Cost in USD — "$0.0045" for sub-dollar (4dp, trailing zeros trimmed), "$12.30" for a dollar or more. */
export function formatUsd(usd: number): string {
  if (usd >= 1) return `$${usd.toFixed(2)}`;
  return `$${usd.toFixed(4).replace(/0+$/, "").replace(/\.$/, "")}`;
}

/** Human run duration — "45s", "2m 17s", "1h 3m"; null/undefined → "—" (unknown / not started). Sub-second floors to "0s". */
export function formatDuration(ms: number | null | undefined): string {
  if (ms == null) return "—";

  const s = Math.floor(ms / 1000);
  if (s < 60) return `${s}s`;

  const m = Math.floor(s / 60);
  if (m < 60) return `${m}m ${s % 60}s`;

  return `${Math.floor(m / 60)}h ${m % 60}m`;
}

/** A wave's per-state agent counts (queued folds Queued/pending; failed folds every terminal-error state) — drives the outline phase's done/total roll-up off the same phase refs, no per-agent fetch. */
export interface WaveBreakdown {
  total: number;
  running: number;
  done: number;
  queued: number;
  failed: number;
}

export function waveBreakdown(agents: readonly PhaseAgentRef[]): WaveBreakdown {
  const b: WaveBreakdown = { total: agents.length, running: 0, done: 0, queued: 0, failed: 0 };

  for (const a of agents) {
    const s = tileState(a.status);
    if (s === "running") b.running++;
    else if (s === "done") b.done++;
    else if (s === "waiting") b.queued++;
    else b.failed++;
  }

  return b;
}
