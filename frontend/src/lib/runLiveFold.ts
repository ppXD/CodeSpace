import type { RunRecordView } from "@/api/workflows";

/**
 * Per-node LIVE signals folded from the run's raw ledger tail — the O(1) summary a node footer paints while a run is in
 * flight (an in-progress external call, streaming token counts, a suspend/wait, and fan-out branch tallies). Every field
 * is optional: a node shows only the signals its records have produced so far. Counts only — never the streamed text —
 * so memory stays flat under tens-of-deltas-per-second.
 */
export interface NodeLiveSignals {
  call?: { target: string; method: string; startedAtMs: number };
  stream?: { chars: number; deltas: number; streaming: boolean };
  wait?: { kind: string; sinceMs: number; deadlineAtMs?: number; payload?: unknown };
  branches?: { done: number; failed: number; running: number; waiting: number; total?: number };
  lastEventSeq: number;
}

/**
 * The whole run's live-signal state: per-node signals keyed by nodeId, the highest folded `sequence` (dedup cursor), and
 * a `terminal` flag once a `run.*` end record lands. Immutable — {@link foldRecord} returns a NEW state (with a new
 * `byNode` Map and a new signals object for ONLY the touched node) when a record changes something, and the SAME
 * reference otherwise, so a per-node selector re-renders exactly the one footer whose signals moved.
 */
export type RunLiveState = { byNode: Map<string, NodeLiveSignals>; lastSeq: number; terminal: boolean };

/** One fan-out branch's lifecycle status, tracked per (nodeId, iterationKey) to recompute a node's branch tallies. */
type BranchStatus = "running" | "done" | "failed" | "waiting";

/**
 * The public {@link RunLiveState} plus the internal per-(node, iterationKey) branch-status bookkeeping the branch tallies
 * are recomputed from. Kept OFF {@link NodeLiveSignals} so the snapshot a footer sees carries no engine-internal fields;
 * carried on the state and cloned immutably alongside `byNode`.
 */
interface RunLiveStateInternal extends RunLiveState {
  branchTracks: Map<string, Map<string, BranchStatus>>;
}

/** A fresh, empty live state — no nodes, cursor at 0, not terminal. */
export function emptyRunLiveState(): RunLiveState {
  const state: RunLiveStateInternal = { byNode: new Map(), lastSeq: 0, terminal: false, branchTracks: new Map() };
  return state;
}

/**
 * Fold one raw ledger record into the live state. Pure and defensive: `payloadJson` is parsed in a try/catch and every
 * field treated as optional, so a malformed or foreign payload degrades to a no-op rather than throwing. Returns a NEW
 * state only when the record actually changed a signal (dedup drops, unknown types, and no-nodeId records return the
 * SAME reference); when it does change one node, only that node's signals object is reallocated.
 */
export function foldRecord(state: RunLiveState, r: RunRecordView): RunLiveState {
  const internal = state as RunLiveStateInternal;

  if (r.sequence <= internal.lastSeq) return state;

  if (isTerminalType(r.recordType)) {
    if (internal.terminal) return state;
    return build(internal.byNode, r.sequence, true, internal.branchTracks);
  }

  if (!r.nodeId) return state;

  const payload = parsePayload(r.payloadJson);
  const change = foldNodeRecord(internal.byNode.get(r.nodeId), r, payload, internal.branchTracks);
  if (!change) return state;

  const byNode = new Map(internal.byNode);
  byNode.set(r.nodeId, change.next);

  return build(byNode, r.sequence, internal.terminal, change.tracks ?? internal.branchTracks);
}

interface NodeChange {
  next: NodeLiveSignals;
  tracks?: Map<string, Map<string, BranchStatus>>;
}

/**
 * Apply one node-scoped record to a node's previous signals. Returns the new signals (and any new branch-tracking map)
 * when a field actually moves; null when the record is a no-op for this node — so the caller can keep the same references
 * and skip the re-render. A later record of a different type clears a pending `wait`.
 */
function foldNodeRecord(prev: NodeLiveSignals | undefined, r: RunRecordView, payload: Record<string, unknown>, tracks: Map<string, Map<string, BranchStatus>>): NodeChange | null {
  let next: NodeLiveSignals | null = null;
  let newTracks: Map<string, Map<string, BranchStatus>> | undefined;

  const draft = (): NodeLiveSignals => {
    if (!next) next = prev ? { ...prev } : { lastEventSeq: r.sequence };
    next.lastEventSeq = r.sequence;
    return next;
  };

  if (r.recordType !== "node.suspended" && prev?.wait) delete draft().wait;

  switch (r.recordType) {
    case "external_call.started":
      draft().call = { target: readString(payload, "target") ?? "", method: readString(payload, "method") ?? "", startedAtMs: Date.parse(r.occurredAt) };
      break;

    case "external_call.completed":
    case "external_call.failed":
      if (prev?.call) delete draft().call;
      break;

    case "interaction.delta": {
      const base = prev?.stream ? { ...prev.stream } : { chars: 0, deltas: 0, streaming: true };
      const text = payload["text"];
      base.chars += typeof text === "string" ? text.length : 0;
      base.deltas += 1;
      base.streaming = true;
      draft().stream = base;
      break;
    }

    case "interaction.completed":
    case "interaction.failed":
      if (prev?.stream && prev.stream.streaming) draft().stream = { ...prev.stream, streaming: false };
      break;

    case "node.suspended": {
      const d = draft();
      d.wait = { kind: readString(payload, "waitKind") ?? readString(payload, "kind") ?? "", sinceMs: Date.parse(r.occurredAt), deadlineAtMs: parseDeadline(payload), payload };
      const applied = applyBranch(tracks, r.nodeId ?? "", r.iterationKey, "waiting");
      newTracks = applied.tracks;
      d.branches = applied.counts;
      break;
    }

    case "node.started":
    case "node.completed":
    case "node.failed": {
      const applied = applyBranch(tracks, r.nodeId ?? "", r.iterationKey, branchStatusFor(r.recordType));
      newTracks = applied.tracks;
      draft().branches = applied.counts;
      break;
    }
  }

  if (!next) return null;
  return { next, tracks: newTracks };
}

/** Set one branch's status and return a NEW branch-tracking map plus the node's recomputed tallies. */
function applyBranch(tracks: Map<string, Map<string, BranchStatus>>, nodeId: string, iterationKey: string, status: BranchStatus): { tracks: Map<string, Map<string, BranchStatus>>; counts: NonNullable<NodeLiveSignals["branches"]> } {
  const inner = new Map(tracks.get(nodeId) ?? []);
  inner.set(iterationKey, status);

  const outer = new Map(tracks);
  outer.set(nodeId, inner);

  return { tracks: outer, counts: countBranches(inner) };
}

/** Tally a node's per-branch statuses into done/failed/running/waiting counts. `total` stays undefined until a BE PR supplies the planned total. */
function countBranches(inner: Map<string, BranchStatus>): NonNullable<NodeLiveSignals["branches"]> {
  let done = 0;
  let failed = 0;
  let running = 0;
  let waiting = 0;

  for (const status of inner.values()) {
    if (status === "done") done += 1;
    else if (status === "failed") failed += 1;
    else if (status === "running") running += 1;
    else waiting += 1;
  }

  return { done, failed, running, waiting };
}

/** Map a settled `node.*` record type to the branch status it represents. */
function branchStatusFor(recordType: string): BranchStatus {
  if (recordType === "node.completed") return "done";
  if (recordType === "node.failed") return "failed";
  return "running";
}

/** Whether a record type ends the whole run (freezes the store). */
function isTerminalType(recordType: string): boolean {
  return recordType === "run.completed" || recordType === "run.failed" || recordType === "run.cancelled";
}

/** Assemble a live state without tripping the excess-property check on the internal branch-tracking field. */
function build(byNode: Map<string, NodeLiveSignals>, lastSeq: number, terminal: boolean, branchTracks: Map<string, Map<string, BranchStatus>>): RunLiveState {
  const state: RunLiveStateInternal = { byNode, lastSeq, terminal, branchTracks };
  return state;
}

/** Parse a record's `payloadJson` into a plain object; an empty object on any malformed/non-object payload — never throws. */
function parsePayload(payloadJson: string): Record<string, unknown> {
  try {
    const parsed: unknown = JSON.parse(payloadJson);
    return parsed && typeof parsed === "object" ? (parsed as Record<string, unknown>) : {};
  } catch {
    return {};
  }
}

/** Read a string field from a parsed payload; undefined when absent or non-string. */
function readString(payload: Record<string, unknown>, key: string): string | undefined {
  const value = payload[key];
  return typeof value === "string" ? value : undefined;
}

/** The suspend deadline in epoch ms from whichever of deadline/wakeAt/timeoutAt is present; undefined when none parses. */
function parseDeadline(payload: Record<string, unknown>): number | undefined {
  return parseInstant(payload["deadline"]) ?? parseInstant(payload["wakeAt"]) ?? parseInstant(payload["timeoutAt"]);
}

/** Coerce a payload instant (epoch-ms number or ISO string) to epoch ms; undefined when unparseable. */
function parseInstant(value: unknown): number | undefined {
  if (typeof value === "number" && Number.isFinite(value)) return value;

  if (typeof value === "string") {
    const parsed = Date.parse(value);
    return Number.isNaN(parsed) ? undefined : parsed;
  }

  return undefined;
}
