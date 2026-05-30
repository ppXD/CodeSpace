import type { RetryPolicy } from "@/api/workflows";

/**
 * Retry-on-failure editor for a node's inspector. A cross-cutting engine setting (not part of any
 * node's own config schema), so it lives outside {@link SchemaForm}. Controlled + presentational:
 * `value == null` means "no retry" (the checkbox is off); toggling on seeds a sensible default.
 *
 * Bounds mirror the backend's `RetryPlan` caps so the editor can never emit a policy that
 * save-time validation would reject.
 */
export const RETRY_MAX_ATTEMPTS_CAP = 10;
export const RETRY_MAX_BACKOFF_SECONDS = 60;
const DEFAULT_MAX_ATTEMPTS = 3;

interface NodeRetryEditorProps {
  value: RetryPolicy | null | undefined;
  onChange: (next: RetryPolicy | null) => void;
}

export function NodeRetryEditor({ value, onChange }: NodeRetryEditorProps) {
  const enabled = value != null;

  // Toggling on seeds a default that actually retries (maxAttempts > 1); toggling off clears the
  // policy entirely so the saved definition omits it (run-once, the non-breaking default).
  const toggle = (on: boolean) =>
    onChange(on ? { maxAttempts: DEFAULT_MAX_ATTEMPTS, backoffSeconds: 0 } : null);

  const patch = (next: Partial<RetryPolicy>) => {
    if (value) onChange({ ...value, ...next });
  };

  return (
    <section className="wf-inspector-section">
      <label className="wf-form-check">
        <input type="checkbox" checked={enabled} onChange={(e) => toggle(e.target.checked)} />
        <span>Retry on failure <span className="wf-form-help-inline">— re-run this node if it fails</span></span>
      </label>

      {enabled && value && (
        <div className="wf-retry-fields">
          <label className="wf-form-row">
            <span className="wf-form-label">Max attempts</span>
            <input
              className="wf-form-input"
              type="number"
              min={1}
              max={RETRY_MAX_ATTEMPTS_CAP}
              value={value.maxAttempts}
              onChange={(e) => patch({ maxAttempts: clampInt(e.target.value, 1, RETRY_MAX_ATTEMPTS_CAP, DEFAULT_MAX_ATTEMPTS) })}
            />
          </label>

          <label className="wf-form-row">
            <span className="wf-form-label">Backoff (seconds)</span>
            <input
              className="wf-form-input"
              type="number"
              min={0}
              max={RETRY_MAX_BACKOFF_SECONDS}
              step={0.5}
              value={value.backoffSeconds}
              onChange={(e) => patch({ backoffSeconds: clampNum(e.target.value, 0, RETRY_MAX_BACKOFF_SECONDS, 0) })}
            />
          </label>

          <p className="wf-retry-hint">
            Counts the first try. Waits between attempts; each retry shows on the run timeline.
            Up to {RETRY_MAX_ATTEMPTS_CAP} attempts / {RETRY_MAX_BACKOFF_SECONDS}s.
          </p>
        </div>
      )}
    </section>
  );
}

function clampInt(raw: string, min: number, max: number, fallback: number): number {
  const n = Number.parseInt(raw, 10);
  return Number.isNaN(n) ? fallback : Math.min(Math.max(n, min), max);
}

function clampNum(raw: string, min: number, max: number, fallback: number): number {
  const n = Number(raw);
  return Number.isNaN(n) ? fallback : Math.min(Math.max(n, min), max);
}
