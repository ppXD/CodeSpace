import { useState } from "react";

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

  // Local raw-string buffers so an in-progress edit can hold text the committed number can't represent —
  // a `type=number` input reports "" for a mid-typed "2." (decimals were impossible) and refills the instant
  // you blank it to retype. The buffers use type=text and only commit when the string parses to a valid
  // number; they re-seed from the value when it changes from OUTSIDE this edit (toggle, node switch, undo)
  // via the adjust-during-render "reset state on prop change" pattern, and normalise on blur.
  const [maxRaw, setMaxRaw] = useState<string>(value ? String(value.maxAttempts) : "");
  const [maxSeen, setMaxSeen] = useState<number | undefined>(value?.maxAttempts);
  if (value?.maxAttempts !== maxSeen) { setMaxSeen(value?.maxAttempts); setMaxRaw(value ? String(value.maxAttempts) : ""); }

  const [backoffRaw, setBackoffRaw] = useState<string>(value ? String(value.backoffSeconds) : "");
  const [backoffSeen, setBackoffSeen] = useState<number | undefined>(value?.backoffSeconds);
  if (value?.backoffSeconds !== backoffSeen) { setBackoffSeen(value?.backoffSeconds); setBackoffRaw(value ? String(value.backoffSeconds) : ""); }

  const onMaxChange = (raw: string) => {
    setMaxRaw(raw);
    const n = Number.parseInt(raw, 10);
    if (!Number.isNaN(n)) patch({ maxAttempts: clamp(n, 1, RETRY_MAX_ATTEMPTS_CAP) });
  };

  const onBackoffChange = (raw: string) => {
    setBackoffRaw(raw);
    // Let "" and a trailing-dot "2." sit in the buffer uncommitted so the decimal can be finished.
    if (raw === "" || raw.endsWith(".")) return;
    const n = Number(raw);
    if (!Number.isNaN(n)) patch({ backoffSeconds: clamp(n, 0, RETRY_MAX_BACKOFF_SECONDS) });
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
              type="text"
              inputMode="numeric"
              value={maxRaw}
              onChange={(e) => onMaxChange(e.target.value)}
              onBlur={() => setMaxRaw(String(value.maxAttempts))}
            />
          </label>

          <label className="wf-form-row">
            <span className="wf-form-label">Backoff (seconds)</span>
            <input
              className="wf-form-input"
              type="text"
              inputMode="decimal"
              value={backoffRaw}
              onChange={(e) => onBackoffChange(e.target.value)}
              onBlur={() => setBackoffRaw(String(value.backoffSeconds))}
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

const clamp = (n: number, min: number, max: number) => Math.min(Math.max(n, min), max);
