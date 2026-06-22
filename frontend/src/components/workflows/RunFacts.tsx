import type { WorkflowRunDetail } from "@/api/workflows";
import { relativeTime } from "@/lib/codeTree";

/**
 * The compact run-facts strip in the Run Room's context rail — the run's standing metadata (how it started, when,
 * how long it has run, which release). Read-only; complements the decision inbox above it so the rail reads as a
 * full context panel rather than a lone inbox. A live run shows an elapsed-since-start duration.
 */
export function RunFacts({ run }: { run: WorkflowRunDetail }) {
  const duration = formatDuration(run.startedAt, run.completedAt);

  return (
    <dl className="run-facts">
      <Fact label="Source" value={sourceLabel(run.sourceType)} />
      {run.startedAt && <Fact label="Started" value={relativeTime(run.startedAt)} />}
      {duration && <Fact label="Duration" value={duration} />}
      <Fact label="Release" value={`v${run.workflowVersion}`} />
    </dl>
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

/** Elapsed time from start to completion (or to now, for a still-running run); empty when not started. */
function formatDuration(startedAt: string | null, completedAt: string | null): string {
  if (!startedAt) return "";

  const start = new Date(startedAt).getTime();
  const end = completedAt ? new Date(completedAt).getTime() : Date.now();
  const sec = Math.max(0, Math.round((end - start) / 1000));

  if (sec < 60) return `${sec}s`;
  const min = Math.floor(sec / 60);
  if (min < 60) return `${min}m ${sec % 60}s`;
  const hr = Math.floor(min / 60);
  return `${hr}h ${min % 60}m`;
}
