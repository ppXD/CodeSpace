import { Ic } from "@/_imported/ai-code-space/icons";
import type { LessonArmSlice, RunScorecardTrend } from "@/api/agents";
import { useAgentScorecardTrend } from "@/hooks/use-agents";

/** Connect the Agents window to the durable north-star history already recorded by the backend. */
export function AgentLearningTrendPanel({ days }: { days: number }) {
  const trend = useAgentScorecardTrend(days);

  if (trend.isLoading && !trend.data) return null;

  if (trend.error) {
    return (
      <section className="lt-panel" aria-label="Learning impact">
        <TrendHead />
        <div className="lt-empty">Couldn't load learning measurements.</div>
      </section>
    );
  }

  return <AgentLearningTrendView trend={trend.data} />;
}

/** Pure renderer for the real `/scorecard-trend` response shape. */
export function AgentLearningTrendView({ trend }: { trend: RunScorecardTrend | undefined }) {
  if (!trend || (trend.buckets.length === 0 && trend.byLessonArm.length === 0)) {
    return (
      <section className="lt-panel" aria-label="Learning impact">
        <TrendHead />
        <div className="lt-empty">No durable learning measurements in this window.</div>
      </section>
    );
  }

  return (
    <section className="lt-panel" aria-label="Learning impact">
      <div className="lt-title-row">
        <TrendHead />
        <span className="lt-denominator">{trend.scoredRuns} scored {trend.scoredRuns === 1 ? "run" : "runs"} · since {formatDay(trend.since)}</span>
      </div>

      {trend.buckets.length > 0 && (
        <div className="lt-days" aria-label="Daily unattended solve with delivery trend">
          {trend.buckets.map((bucket) => (
            <div className="lt-day" key={bucket.day} title={dayHint(bucket.runs, bucket.suspendedRuns, bucket.legacyRuns)}>
              <div className="lt-bar-track" aria-hidden="true">
                <div className="lt-bar" style={{ height: bucket.unattendedSolveWithDeliveryRate === null ? 2 : `${Math.max(bucket.unattendedSolveWithDeliveryRate * 100, 4)}%` }} />
              </div>
              <span className="lt-day-rate">{formatRate(bucket.unattendedSolveWithDeliveryRate)}</span>
              <span className="lt-day-label">{formatDay(bucket.day)}</span>
              {(bucket.suspendedRuns > 0 || bucket.legacyRuns > 0) && <span className="lt-day-gap">{bucket.suspendedRuns} parked · {bucket.legacyRuns} legacy</span>}
            </div>
          ))}
        </div>
      )}

      <div className="lt-arm-title">Lesson experiment</div>
      {trend.byLessonArm.length === 0 ? (
        <div className="lt-empty">No run in this window carried a lesson experiment arm.</div>
      ) : (
        <div className="lt-table-wrap">
          <table className="tbl lt-table">
            <thead><tr><th>Arm</th><th className="col-right">Unattended delivery</th><th className="col-right">Human touch</th><th className="col-right">Agent avg (priced)</th><th className="col-right">Brain avg (priced)</th><th className="col-right">Solved</th><th className="col-right">Delivered</th><th className="col-right">Runs</th></tr></thead>
            <tbody>{trend.byLessonArm.map((slice) => <LessonArmRow key={slice.arm} slice={slice} />)}</tbody>
          </table>
        </div>
      )}
      <p className="lt-note">Arm rates are observational. Cost averages use priced runs only; unknown runs are shown rather than counted as free. These measurements do not change launch policy.</p>
    </section>
  );
}

function TrendHead() {
  return <div className="lt-head"><Ic.Zap size={12} /> Learning impact</div>;
}

function LessonArmRow({ slice }: { slice: LessonArmSlice }) {
  return (
    <tr>
      <td><span className="lt-arm">{armLabel(slice.arm)}</span></td>
      <td className="col-right"><strong>{formatRate(slice.unattendedSolveWithDeliveryRate)}</strong> <span className="lt-fraction">{slice.unattendedSolvedWithDeliveryRuns}/{slice.runs}</span></td>
      <td className="col-right"><strong>{formatRate(slice.humanInterventionRate)}</strong> <span className="lt-fraction">{formatTouches(slice.avgHumanTouches)}/run</span></td>
      <CostCell average={slice.avgCostPerPricedRunUsd} total={slice.totalCostUsd} unknown={slice.unknownCostRuns} />
      <CostCell average={slice.avgBrainPlaneCostPerPricedRunUsd} total={slice.brainPlaneUsd} unknown={slice.unknownBrainCostRuns} />
      <td className="col-right">{slice.solvedRuns}/{slice.runs}</td>
      <td className="col-right">{slice.deliveredRuns}/{slice.runs}</td>
      <td className="col-right">{slice.runs}</td>
    </tr>
  );
}

function CostCell({ average, total, unknown }: { average: number | null; total: number | null; unknown: number }) {
  return (
    <td className="col-right lt-cost" title={total === null ? "No run in this arm had a known price" : `${formatUsd(total)} known total`}>
      <span>{average === null ? "Not priced" : formatUsd(average)}</span>
      {unknown > 0 && <span className="lt-unknown">{unknown} unknown</span>}
    </td>
  );
}

function armLabel(arm: string): string {
  if (arm === "injected") return "Lessons injected";
  if (arm === "withheld") return "Lessons withheld";
  if (arm === "none") return "No eligible lessons";
  if (arm === "unmeasured") return "Outside experiment";
  return arm;
}

function formatRate(rate: number | null): string {
  return rate === null ? "Not measured" : `${Math.round(rate * 100)}%`;
}

function formatTouches(value: number): string {
  return value.toLocaleString("en", { maximumFractionDigits: 2 });
}

function formatUsd(value: number): string {
  return `$${value.toFixed(2)}`;
}

function formatDay(value: string): string {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : new Intl.DateTimeFormat("en", { month: "short", day: "numeric", timeZone: "UTC" }).format(date);
}

function dayHint(runs: number, suspended: number, legacy: number): string {
  return `${runs} scored · ${suspended} parked · ${legacy} legacy`;
}
