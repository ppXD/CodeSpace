import type { PhaseAgentRef, RunPhase } from "@/api/workflows";

/**
 * The run's pure phase/agent model — groups the phase tree into agent WAVES (each phase's claimed agents) and the
 * shared agent-status helpers the outline + the Activity tiles both read. Source-agnostic: it never switches on a
 * phase `kind` beyond ranking authored phases over the raw ones, so a single-agent run, a map fan-out, and a
 * supervisor wave all flow through the same path. No React, no hooks — unit-testable.
 */

/** An agent wave — the agents one phase claimed (deduped so an agent never appears in two waves). */
export interface AgentWave {
  /** The owning phase id (the React key + the outline / tiles filter key). */
  id: string;
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
      label: p.label,
      startedAt: p.startedAt ?? null,
      // The agents this phase claimed, also collapsed by id so a (defensively) duplicated ref in one phase's list
      // can't render the same tile twice / collide on the React key.
      agents: [...new Map(p.agents.filter((a) => bestByAgent.get(a.agentRunId) === p).map((a) => [a.agentRunId, a])).values()],
    }))
    .filter((w) => w.agents.length > 0);
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
