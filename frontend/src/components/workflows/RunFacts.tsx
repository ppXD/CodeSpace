import { Ic } from "@/_imported/ai-code-space/icons";
import type { WorkflowRunDetail } from "@/api/workflows";
import { relativeTime } from "@/lib/codeTree";

/**
 * The run-facts panel in the Run Room's context rail — the run's standing metadata (how it started, when, how long
 * it has run, which release), in a framed rail card so it reads consistently with the outline + decision panels.
 * Read-only; a live run shows an elapsed-since-start duration.
 */
export function RunFacts({ run }: { run: WorkflowRunDetail }) {
  // Both from createdDate (immutable), not startedAt (reset on every resume → "Started 2m ago" / "Duration 0s" lies).
  const duration = formatDuration(run.createdDate, run.completedAt);

  return (
    <div className="rail-card">
      <div className="rail-card-head"><Ic.Box size={12} aria-hidden="true" /> Run</div>
      <dl className="run-facts">
        <Fact label="Source" value={sourceLabel(run.sourceType)} />
        <Fact label="Started" value={relativeTime(run.createdDate)} />
        {duration && <Fact label="Duration" value={duration} />}
        <Fact label="Release" value={`v${run.workflowVersion}`} />
      </dl>
    </div>
  );
}

function Fact({ label, value }: { label: string; value: string }) {
  return (
    <div className="run-facts-row">
      <dt className="run-facts-key">{label}</dt>
      <dd className="run-facts-val">{value}</dd>
    </div>
  );
}

/** A friendly source-type label — title-cased from the open `source_type` token (manual / webhook / schedule / …). */
function sourceLabel(sourceType: string): string {
  if (!sourceType) return "—";
  return sourceType.charAt(0).toUpperCase() + sourceType.slice(1);
}

/** Elapsed time from the run's creation to completion (or to now, for a still-running run); empty when absent. */
function formatDuration(createdDate: string | null, completedAt: string | null): string {
  if (!createdDate) return "";

  const start = new Date(createdDate).getTime();
  const end = completedAt ? new Date(completedAt).getTime() : Date.now();
  const sec = Math.max(0, Math.round((end - start) / 1000));

  if (sec < 60) return `${sec}s`;
  const min = Math.floor(sec / 60);
  if (min < 60) return `${min}m ${sec % 60}s`;
  const hr = Math.floor(min / 60);
  return `${hr}h ${min % 60}m`;
}
