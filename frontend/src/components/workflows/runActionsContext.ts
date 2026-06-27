import { createContext } from "react";

/**
 * Carries the OWNING run's id (and whether it's terminal) down to deeply-nested canvas nodes — used by the
 * fan-out card's Rerun control, which needs the run id to POST a map-branch rerun. A React context (not node
 * `data`) so it stays stable and we don't rebuild every node's data on each render; React Flow propagates
 * context from above `<ReactFlow>` into custom nodes.
 *
 * Null when no provider is mounted (e.g. a node rendered without a run-viewer host, or a still-live run where a
 * rerun makes no sense) → the node shows no rerun affordance.
 */
export const RunActionsContext = createContext<RunActions | null>(null);

export interface RunActions {
  /** The id of the run being viewed — the rerun forks from it. */
  runId: string;
  /** True once the run reached a terminal state (Success / Failure / Cancelled) — only then is a rerun meaningful. */
  isTerminal: boolean;
}
