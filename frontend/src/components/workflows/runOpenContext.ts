import { createContext } from "react";

/**
 * Lets a custom run-canvas node ask the run viewer to open ANOTHER run full-view — used by a
 * flow.subworkflow node to jump to the child run it spawned. A React context (not node `data`) so the
 * callback identity stays stable and we don't rebuild every node's data on each render; React Flow
 * propagates context from above `<ReactFlow>` into custom nodes.
 *
 * Null when no provider is mounted (e.g. a node rendered without a run-viewer host) → the node shows no
 * "open" affordance.
 */
export const RunOpenContext = createContext<((runId: string) => void) | null>(null);
