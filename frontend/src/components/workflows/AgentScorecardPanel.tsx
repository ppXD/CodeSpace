import { Ic } from "@/_imported/ai-code-space/icons";
import type { AgentRunScorecard, HarnessScore, TeamCostRollup } from "@/api/agents";
import { useAgentScorecard, useTeamCost } from "@/hooks/use-agents";

/**
 * The team's agent-run scorecard — the measurement spine that turns "is the agent working" into an auditable
 * NUMBER. A headline strip (overall success rate, P50/P95 latency, runs scored, est. spend) over a per-harness
 * comparison table, surfaced at the top of the Agents library. Read-only + team-scoped at the source.
 *
 * <p>Success + latency come from the scorecard; the estimated USD spend comes from the SEPARATE cost rollup
 * (<c>/api/agents/cost</c>) — a real figure, qualified by an unknown-cost count, not fabricated. The cost stat
 * renders only when a rollup is supplied, so the pure view stays cost-free when given only a scorecard.</p>
 *
 * This is the wired surface; {@link AgentScorecardView} is the pure renderer (data in, markup out) the tests drive.
 */
export function AgentScorecardPanel() {
  const scorecard = useAgentScorecard();
  const cost = useTeamCost();

  if (scorecard.isLoading) return null;

  if (scorecard.error) {
    return (
      <div className="sc-panel">
        <ScorecardHead />
        <div className="sc-empty">Couldn't load the scorecard</div>
      </div>
    );
  }

  return <AgentScorecardView card={scorecard.data} cost={cost.data} />;
}

/** Pure renderer — markup for a scorecard (or the empty state when no runs have been scored yet). */
export function AgentScorecardView({ card, cost }: { card: AgentRunScorecard | undefined; cost?: TeamCostRollup }) {
  const overall = card?.overall;

  if (!overall || overall.total === 0) {
    return (
      <div className="sc-panel">
        <ScorecardHead />
        <div className="sc-empty">No runs scored yet — run an agent and its success rate + latency will appear here.</div>
      </div>
    );
  }

  return (
    <div className="sc-panel">
      <ScorecardHead />

      <div className="sc-stats">
        <Stat label="Success rate" value={formatRate(overall.successRate)} accent />
        <Stat label="P50 latency" value={formatDuration(overall.p50DurationSeconds)} />
        <Stat label="P95 latency" value={formatDuration(overall.p95DurationSeconds)} />
        <Stat label="Runs scored" value={`${overall.succeeded}/${overall.total}`} />
        {cost && <Stat label="Est. cost" value={formatUsd(cost.estimatedCostUsd)} />}
      </div>

      <table className="tbl sc-table">
        <thead>
          <tr>
            <th style={{ width: "34%" }}>Harness</th>
            <th className="col-right">Success</th>
            <th className="col-right">Runs</th>
            <th className="col-right">P50</th>
            <th className="col-right">P95</th>
          </tr>
        </thead>
        <tbody>
          {card.harnesses.map((h) => <HarnessRow key={h.harness} score={h} />)}
        </tbody>
      </table>
    </div>
  );
}

function ScorecardHead() {
  return <div className="sc-head"><Ic.Zap size={12} /> Agent scorecard</div>;
}

/** One headline metric. `accent` lifts the lead stat (success rate) in the warm Claude tone. */
function Stat({ label, value, accent }: { label: string; value: string; accent?: boolean }) {
  return (
    <div className="sc-stat" data-accent={accent ? "true" : undefined}>
      <div className="sc-stat-value">{value}</div>
      <div className="sc-stat-label">{label}</div>
    </div>
  );
}

/** One per-harness comparison row — success rate, terminal-run count, and the latency percentiles. */
function HarnessRow({ score }: { score: HarnessScore }) {
  return (
    <tr>
      <td><span className="sc-harness">{score.harness}</span></td>
      <td className="col-right"><span className="sc-rate">{formatRate(score.successRate)}</span> <span className="sc-rate-n">{score.succeeded}/{score.total}</span></td>
      <td className="col-right">{score.total}</td>
      <td className="col-right">{formatDuration(score.p50DurationSeconds)}</td>
      <td className="col-right">{formatDuration(score.p95DurationSeconds)}</td>
    </tr>
  );
}

/** 0..1 → a whole-number percentage (no decimals — the success rate reads as a clean "75%"). */
function formatRate(rate: number): string {
  return `${Math.round(rate * 100)}%`;
}

/** Estimated spend → "$12.40"; an em-dash when nothing in the window could be priced (null, distinct from $0.00). */
function formatUsd(usd: number | null): string {
  if (usd === null) return "—";
  return `$${usd.toFixed(2)}`;
}

/** Seconds → a compact human duration ("8s", "1m 30s", "2h 5m"); an em-dash when there's no latency to show. */
function formatDuration(seconds: number | null): string {
  if (seconds === null) return "—";
  if (seconds < 60) return `${Math.round(seconds)}s`;

  const mins = Math.floor(seconds / 60);
  if (mins < 60) {
    const rem = Math.round(seconds % 60);
    return rem === 0 ? `${mins}m` : `${mins}m ${rem}s`;
  }

  const hours = Math.floor(mins / 60);
  const remMins = mins % 60;
  return remMins === 0 ? `${hours}h` : `${hours}h ${remMins}m`;
}
