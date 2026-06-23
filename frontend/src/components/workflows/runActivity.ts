import type { PhaseAgentRef, RunPhase, RunTimelineEvent } from "@/api/workflows";

/**
 * The activity stream's pure model — the merge that turns the run's two read planes (the narrative timeline events +
 * the phase tree's agent groupings) into ONE chronological list the Activity tab scrolls. Source-agnostic and
 * generic: it never switches on a phase `kind` or an event `kind` beyond ranking authored phases over raw ones, so a
 * single-agent run, a map fan-out, and a supervisor wave all flow through the same path. No React, no hooks — unit-testable.
 */

/** An agent wave — a phase's claimed agents, rendered as the terminal-tile grid at the phase's position in the stream. */
export interface AgentWave {
  /** The owning phase id (the React key). */
  id: string;
  /** The phase label ("Implement", "Spawn 3 agents", "code"). */
  label: string;
  /** When the phase began — the wave's chronological anchor (null until it has started). */
  startedAt: string | null;
  /** The agents this wave OWNS (deduped so an agent never appears in two waves). */
  agents: PhaseAgentRef[];
}

/** One item in the composed activity stream — a narrative event row, an agent-wave block, or a folded run of detail events. */
export type ActivityItem =
  | { kind: "event"; key: string; at: string; event: RunTimelineEvent }
  | { kind: "wave"; key: string; at: string | null; wave: AgentWave }
  | { kind: "fold"; key: string; at: string; events: RunTimelineEvent[] };

type EventItem = Extract<ActivityItem, { kind: "event" }>;

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
      label: p.label,
      startedAt: p.startedAt ?? null,
      // The agents this phase claimed, also collapsed by id so a (defensively) duplicated ref in one phase's list
      // can't render the same tile twice / collide on the React key.
      agents: [...new Map(p.agents.filter((a) => bestByAgent.get(a.agentRunId) === p).map((a) => [a.agentRunId, a])).values()],
    }))
    .filter((w) => w.agents.length > 0);
}

/**
 * Merge the narrative events + the agent waves into ONE chronological stream. Events sort by their `occurredAt`; a
 * wave sorts by its phase `startedAt`, falling back to the earliest event among its own agents, then to the end (so
 * an unanchored wave never silently vanishes). On an equal timestamp an event sorts BEFORE a wave, so a wave lands
 * just after the "spawned" event that announced it. The final index tie-break keeps the sort stable + deterministic.
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
 * The Activity stream the UI renders: the merged event+wave stream (see mergeActivityStream) with each run of TWO OR
 * MORE consecutive DETAIL events collapsed into one "fold" item — so the story shows milestones + waves and tucks the
 * structural churn (node started/completed, file edits) behind a "N steps" disclosure at its chronological spot. A
 * wave or a milestone event flushes the run; a LONE detail stays inline (a one-row fold isn't worth it — the renderer
 * just dims it). An absent level reads as a milestone (forward-tolerance), never silently folded.
 */
export function composeActivity(events: readonly RunTimelineEvent[], waves: readonly AgentWave[]): ActivityItem[] {
  const merged = mergeActivityStream(events, waves);
  const out: ActivityItem[] = [];
  let run: EventItem[] = [];
  // The fold's key is anchored to the item BEFORE it (the last non-detail), NOT its first detail — so the key (and
  // thus the disclosure's open state) survives a live poll that backfills an earlier-sorting detail to the run's front
  // or re-sorts its members. A boundary precedes at most one fold, so keys stay unique.
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

// ── shared agent-tile presentation (used by both the collapsed tile and the expanded terminal footer) ──

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

/** Human run duration — "45s", "2m 17s", "1h 3m"; null/undefined → "—" (unknown / not started). Sub-second floors to "0s". */
export function formatDuration(ms: number | null | undefined): string {
  if (ms == null) return "—";

  const s = Math.floor(ms / 1000);
  if (s < 60) return `${s}s`;

  const m = Math.floor(s / 60);
  if (m < 60) return `${m}m ${s % 60}s`;

  return `${Math.floor(m / 60)}h ${m % 60}m`;
}

/** A wave's per-state agent counts (queued folds Queued/pending; failed folds every terminal-error state) — drives the fleet card's dots + summary line off the same phase refs, no per-agent fetch. */
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

/** The fleet card's summary line — "8 agents · 4 done · 2 running · 1 queued · 1 failed"; a zero category drops, the total pluralizes. */
export function formatBreakdown(b: WaveBreakdown): string {
  const head = `${b.total} ${b.total === 1 ? "agent" : "agents"}`;

  const parts = [
    b.done > 0 && `${b.done} done`,
    b.running > 0 && `${b.running} running`,
    b.queued > 0 && `${b.queued} queued`,
    b.failed > 0 && `${b.failed} failed`,
  ].filter(Boolean);

  return parts.length > 0 ? `${head} · ${parts.join(" · ")}` : head;
}

/** Compare two ISO timestamps; a null sorts LAST so an unanchored item goes to the end rather than the top. */
function cmpAt(a: string | null, b: string | null): number {
  if (a === b) return 0;
  if (a === null) return 1;
  if (b === null) return -1;
  return a < b ? -1 : 1;
}
