import { Ic } from "@/_imported/ai-code-space/icons";

import type { CockpitFilter, DecisionSummary, TodaySummary } from "./cockpit";

/**
 * The four cockpit status cards — the at-a-glance "is anything on fire / is the system working" row. Each card is a
 * filter toggle (clicking arms it, narrowing the zones below; clicking the armed one clears it). Liveness is restrained
 * by design: only the decision + live cards animate (a breathing pulse / a flowing tick) and only while non-zero, so the
 * row reads as a calm dashboard, not a game HUD. Usage/cost isn't tracked yet, so the fourth card reports runs today.
 */
export interface CockpitMetrics {
  decisions: DecisionSummary;
  liveCount: number;
  agentsActive: number;
  failed: number;
  suspended: number;
  today: TodaySummary;
}

export function CockpitCards({ metrics, filter, onFilter }: { metrics: CockpitMetrics; filter: CockpitFilter; onFilter: (f: CockpitFilter) => void }) {
  const { decisions, liveCount, agentsActive, failed, suspended, today } = metrics;
  const stuck = failed + suspended;

  return (
    <div className="cockpit-cards">
      <StatusCard tone="decision" label="Needs decision" value={decisions.count} armed={filter === "decisions"} onClick={() => onFilter("decisions")}
        sub={decisions.count === 0 ? "All clear" : joinDot([decisions.oldestAge && `oldest ${decisions.oldestAge}`, decisions.highRisk > 0 && `${decisions.highRisk} high risk`])}>
        {decisions.count > 0 && <span className="cockpit-pulse" aria-hidden="true" />}
      </StatusCard>

      <StatusCard tone="live" label="Live runs" value={liveCount} armed={filter === "live"} onClick={() => onFilter("live")}
        sub={liveCount === 0 ? "Idle" : agentsActive > 0 ? `${agentsActive} agent${agentsActive === 1 ? "" : "s"} active` : "in progress"}>
        {liveCount > 0 && <span className="cockpit-flow" aria-hidden="true"><i /><i /><i /></span>}
      </StatusCard>

      <StatusCard tone="failed" label="Failed / stuck" value={stuck} armed={filter === "failed"} onClick={() => onFilter("failed")}
        sub={stuck === 0 ? "None" : joinDot([failed > 0 && `${failed} failed`, suspended > 0 && `${suspended} suspended`])}>
        {stuck > 0 && <Ic.Triangle size={12} aria-hidden="true" />}
      </StatusCard>

      <StatusCard tone="today" label="Runs today" value={today.count} armed={filter === "today"} onClick={() => onFilter("today")}
        sub={`${today.count} run${today.count === 1 ? "" : "s"}`}>
        <Sparkline data={today.hourly} />
      </StatusCard>
    </div>
  );
}

function StatusCard({ tone, label, value, sub, armed, onClick, children }: {
  tone: string; label: string; value: number; sub: string; armed: boolean; onClick: () => void; children?: React.ReactNode;
}) {
  return (
    <button type="button" className="cockpit-card" data-tone={tone} data-armed={armed || undefined} data-zero={value === 0 || undefined}
      aria-pressed={armed} onClick={onClick}>
      <div className="cockpit-card-top">
        <span className="cockpit-card-label">{label}</span>
        <span className="cockpit-card-glyph">{children}</span>
      </div>
      <div className="cockpit-card-value">{value}</div>
      <div className="cockpit-card-sub">{sub}</div>
    </button>
  );
}

/** A tiny line sparkline (the day's hourly run histogram) — decorative, so it scales to fill and is aria-hidden. */
function Sparkline({ data }: { data: number[] }) {
  const max = Math.max(1, ...data);
  const n = data.length;
  const points = data.map((v, i) => `${(i / (n - 1)) * 100},${20 - (v / max) * 18 - 1}`).join(" ");

  return (
    <svg className="cockpit-spark" viewBox="0 0 100 20" preserveAspectRatio="none" aria-hidden="true">
      <polyline points={points} fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinejoin="round" strokeLinecap="round" vectorEffect="non-scaling-stroke" />
    </svg>
  );
}

/** Join the truthy parts with " · " — used for the card subtext (e.g. "oldest 14m · 2 high risk"). */
function joinDot(parts: (string | false | null | undefined)[]): string {
  return parts.filter(Boolean).join(" · ");
}
