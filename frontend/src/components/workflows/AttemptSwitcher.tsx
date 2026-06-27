import { Ic } from "@/_imported/ai-code-space/icons";
import type { RunAttempt, WorkflowRunStatus } from "@/api/workflows";

/**
 * The lineage's attempt ladder as a row of pills — the original run plus every rerun, oldest first, the newest flagged
 * "latest". Picking one drives the whole detail (Activity + Canvas + facts) to that attempt. Hidden for a never-rerun
 * run (a single attempt has nothing to switch between).
 */
export function AttemptSwitcher({ attempts, selectedRunId, onSelect }: { attempts: RunAttempt[]; selectedRunId: string; onSelect: (runId: string) => void }) {
  if (attempts.length < 2) return null;

  return (
    <div className="attempt-switcher" role="tablist" aria-label="Run attempts">
      <span className="attempt-switcher-label">Attempts</span>
      {attempts.map((a) => (
        <button
          key={a.runId}
          type="button"
          role="tab"
          aria-selected={a.runId === selectedRunId}
          className="attempt-pill"
          data-selected={a.runId === selectedRunId || undefined}
          onClick={() => onSelect(a.runId)}
          title={`Attempt ${a.attemptNumber} · ${a.status}`}
        >
          <AttemptGlyph status={a.status} />
          Attempt {a.attemptNumber}
          {a.isLatest && <span className="attempt-pill-latest">latest</span>}
        </button>
      ))}
    </div>
  );
}

/** The attempt's outcome glyph — a tick when it succeeded, a cross when it failed/stopped, a clock while it's still live. */
function AttemptGlyph({ status }: { status: WorkflowRunStatus }) {
  if (status === "Success") return <span className="attempt-glyph" data-tone="success"><Ic.Check size={12} aria-hidden="true" /></span>;
  if (status === "Failure" || status === "Cancelled") return <span className="attempt-glyph" data-tone="failed"><Ic.X size={12} aria-hidden="true" /></span>;
  return <span className="attempt-glyph" data-tone="live"><Ic.Clock size={12} aria-hidden="true" /></span>;
}
