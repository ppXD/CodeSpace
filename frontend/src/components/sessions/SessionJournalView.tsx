import { useState } from "react";
import type {
  JournalAgentCard,
  JournalAttempt,
  JournalStep,
  JournalTurn,
  JournalView,
} from "@/api/sessions";
import type { WorkflowRunStatus } from "@/api/workflows";

/// The Session Journal — a run's work rendered as a CHRONOLOGICAL transcript (decision · spawn · tool · thinking · …),
/// built alongside the Room behind a ?view=journal toggle. Backend-authored: the frontend renders steps by `kind` and
/// owns no copy / order / status. Prop-driven (the route fetches via useRunJournal and passes `journal`).
export function SessionJournalView({
  journal,
  onFocusRun,
}: {
  teamSlug: string;
  journal: JournalView;
  onFocusRun: (runId: string) => void;
}) {
  return (
    <div className="journal-room jr-root">
      <JournalHeader journal={journal} />
      <div className="jr-feed">
        {journal.turns.map((turn) => (
          <JournalTurnBlock key={turn.turnIndex} turn={turn} onFocusRun={onFocusRun} />
        ))}
      </div>
      <p className="jr-foot">
        Every decision, action, and its reasoning appears in true execution order — nothing folded away silently.
      </p>
    </div>
  );
}

function JournalHeader({ journal }: { journal: JournalView }) {
  const focused = journal.turns.find((t) => t.focused);
  const agents = focused ? focused.steps.reduce((n, s) => n + s.agents.length, 0) : 0;
  return (
    <header className="jr-head">
      <div className="jr-eyebrow">
        <span className="jr-dot" /> Session Journal
      </div>
      <h1 className="jr-title">{journal.title}</h1>
      <div className="jr-strip">
        <Stat k="Turns" v={String(journal.turns.length)} />
        {agents > 0 && <Stat k="Agents" v={String(agents)} />}
        {focused && <Stat k="Steps" v={String(focused.stepCount)} />}
      </div>
    </header>
  );
}

function Stat({ k, v }: { k: string; v: string }) {
  return (
    <div className="jr-stat">
      <div className="jr-stat-k">{k}</div>
      <div className="jr-stat-v">{v}</div>
    </div>
  );
}

function JournalTurnBlock({ turn, onFocusRun }: { turn: JournalTurn; onFocusRun: (runId: string) => void }) {
  return (
    <div className="jr-turn">
      <div className="jr-turn-head">
        <span className="jr-turn-idx">Turn {turn.turnIndex}</span>
        <div className="jr-ask">
          <div className="jr-who">You asked</div>
          {turn.userMessage && <div className="jr-msg">{turn.userMessage}</div>}
          {turn.attempts.length > 1 && <AttemptPager attempts={turn.attempts} onFocusRun={onFocusRun} />}
        </div>
        <RunPill status={turn.status} />
      </div>

      {turn.focused ? (
        <div className="jr-steps">
          {turn.steps.map((step) => (
            <StepRow key={step.id} step={step} />
          ))}
          {turn.steps.length === 0 && <div className="jr-empty">No steps recorded yet.</div>}
        </div>
      ) : (
        <button className="jr-card" onClick={() => onFocusRun(turn.runId)}>
          <div className="jr-card-top">
            <span className="jr-card-msg">{turn.summary ?? "(in progress)"}</span>
            <RunPill status={turn.status} />
          </div>
          <div className="jr-card-open">▸ Open to replay this turn's steps</div>
        </button>
      )}
    </div>
  );
}

function AttemptPager({ attempts, onFocusRun }: { attempts: JournalAttempt[]; onFocusRun: (runId: string) => void }) {
  return (
    <div className="jr-attempts" role="tablist" aria-label="Attempts">
      {attempts.map((a) => (
        <button
          key={a.runId}
          role="tab"
          aria-selected={a.focused}
          className={`jr-att jr-att-${attemptTone(a.status)}`}
          title={a.error ?? undefined}
          onClick={() => onFocusRun(a.runId)}
        >
          <span className="jr-att-mk" />
          Attempt {a.attemptNumber}
          {attemptLabel(a)}
        </button>
      ))}
    </div>
  );
}

/// The pager suffix — "· current" only for the attempt actually FOCUSED (the one walked into the steps; none on a
/// collapsed turn), else "· failed" for a failed attempt. Keys off the backend's `focused` flag, never a positional
/// fallback (which would mislabel the oldest attempt "current" on a collapsed turn, where nothing is focused).
function attemptLabel(a: JournalAttempt): string {
  if (a.focused) return " · current";
  if (a.status === "Failure") return " · failed";
  return "";
}

function StepRow({ step }: { step: JournalStep }) {
  return (
    <div className={`jr-step jr-tone-${toneClass(step.tone)}${step.milestone ? " jr-key" : ""}`}>
      <span className="jr-node" />
      <div className="jr-line">
        <span className="jr-time">{stepTime(step.at)}</span>
        <span className="jr-title-row">
          <span className={`jr-kind jr-kind-${step.kind}`}>{kindLabel(step.kind)}</span>
          <span className="jr-step-title">{step.title}</span>
        </span>
      </div>

      {step.rationale && (
        <div className="jr-why">
          <span className="jr-why-lead">{step.rationale}</span>
        </div>
      )}

      {step.detail &&
        (step.kind === "thinking" ? (
          <div className="jr-think">{step.detail}</div>
        ) : (
          <div className={`jr-detail jr-detail-${toneClass(step.tone)}`}>{step.detail}</div>
        ))}

      {step.agents.length > 0 && (
        <div className="jr-agents">
          {step.agents.map((a) => (
            <AgentCard key={a.agentRunId} card={a} />
          ))}
        </div>
      )}

      {step.deferred.length > 0 && (
        <div className="jr-deferred">
          {step.deferred.map((d) => (
            <span key={d.subtaskId} className="jr-defer">
              {d.subtaskId} deferred · waiting on {d.waitingOn.join(", ")}
            </span>
          ))}
        </div>
      )}
    </div>
  );
}

function AgentCard({ card }: { card: JournalAgentCard }) {
  const [open, setOpen] = useState(false);
  const meta: string[] = [];
  if (card.model) meta.push(card.model);
  if (card.durationMs != null) meta.push(formatMs(card.durationMs));
  if (card.tokens != null) meta.push(`${formatTokens(card.tokens)} tok`);
  if (card.toolCount != null) meta.push(`${card.toolCount} tools`);
  return (
    <div className={`jr-acard${open ? " jr-open" : ""}`}>
      <button className="jr-acard-h" onClick={() => setOpen((v) => !v)}>
        <div className="jr-acard-row1">
          <span className="jr-goal">{card.label}</span>
          {card.resumed && <span className="jr-chip-resume">⟳ resumed</span>}
          <AgentPill status={card.status} />
        </div>
        <div className="jr-acard-meta">
          {meta.map((m, i) => (
            <span key={i} className="jr-m">
              {m}
            </span>
          ))}
        </div>
        {card.files.length > 0 && (
          <div className="jr-acard-hint">
            <span className="jr-caret">▸</span>
            {card.files.length} {card.files.length === 1 ? "file" : "files"}
          </div>
        )}
      </button>
      {open && card.files.length > 0 && (
        <div className="jr-files">
          {card.files.map((f) => (
            <div key={f.path} className="jr-file">
              <span className="jr-path">{f.path}</span>
              <span className="jr-ds">
                {f.additions != null && <span className="jr-add">+{f.additions}</span>}
                {f.deletions != null && <span className="jr-del">−{f.deletions}</span>}
              </span>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

function RunPill({ status }: { status: WorkflowRunStatus }) {
  const tone = runTone(status);
  return (
    <span className={`jr-pill jr-pill-${tone}`}>
      <span className="jr-tick" />
      {status}
    </span>
  );
}

function AgentPill({ status }: { status: string }) {
  const tone = agentTone(status);
  return (
    <span className={`jr-pill jr-pill-${tone}`}>
      <span className="jr-tick" />
      {status}
    </span>
  );
}

// ── pure helpers ──

function toneClass(tone: string): string {
  switch (tone) {
    case "Success":
      return "ok";
    case "Warning":
      return "warn";
    case "Error":
      return "err";
    default:
      return "info";
  }
}

function runTone(status: WorkflowRunStatus): string {
  switch (status) {
    case "Success":
      return "ok";
    case "Failure":
      return "err";
    case "Cancelled":
      return "idle";
    case "Suspended":
      return "warn"; // paused — awaiting a human / timer / callback, not actively running
    default:
      return "run";
  }
}

function attemptTone(status: WorkflowRunStatus): string {
  return status === "Failure" ? "fail" : status === "Success" ? "ok" : "run";
}

function agentTone(status: string): string {
  switch (status) {
    case "Succeeded":
      return "ok";
    case "Failed":
    case "TimedOut":
      return "err";
    case "Cancelled":
      return "idle";
    case "NeedsReview":
      return "warn";
    default:
      return "run";
  }
}

function kindLabel(kind: string): string {
  switch (kind) {
    case "decision":
      return "decision";
    case "tool":
      return "tool";
    case "agent":
      return "agent";
    case "thinking":
      return "thinking";
    case "model_call":
      return "model";
    case "lifecycle":
      return "lifecycle";
    default:
      return kind;
  }
}

function stepTime(iso: string): string {
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? "" : d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" });
}

function formatMs(ms: number): string {
  if (ms < 1000) return `${ms}ms`;
  const s = Math.round(ms / 1000);
  if (s < 60) return `${s}s`;
  const m = Math.floor(s / 60);
  return `${m}m ${s % 60}s`;
}

function formatTokens(n: number): string {
  return n >= 1000 ? `${(n / 1000).toFixed(1)}k` : String(n);
}
