import { createContext } from "react";

import type { RunAttempt } from "@/api/workflows";

/**
 * The lineage's rerun provenance, shared down the run detail so any node surface (Canvas node, Activity phase) can
 * show its own rerun history without prop-drilling. `rerunsByNode` maps a node id → the attempts that re-ran it.
 * Null when there's no lineage context (a nested sub-workflow, or before the ladder loads).
 */
export interface RerunProvenance {
  attempts: readonly RunAttempt[];
  rerunsByNode: ReadonlyMap<string, RunAttempt[]>;
}

export const RerunProvenanceContext = createContext<RerunProvenance | null>(null);
