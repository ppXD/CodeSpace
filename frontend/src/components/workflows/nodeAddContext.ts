import { createContext } from "react";

/**
 * Lets a custom React Flow node ask the editor to open the "add a node here" picker, anchored at the
 * clicked screen point and sourced from this node (the new node is auto-linked from it). A React
 * context — not node `data` — so the callback identity stays stable and we don't rebuild every node's
 * data on each render. React Flow propagates context from above `<ReactFlow>` into custom nodes.
 *
 * Null when no provider is mounted (e.g. WorkflowNode rendered outside the editor) → the node renders
 * no "+" button.
 */
export type NodeAddRequest = (sourceNodeId: string, screen: { x: number; y: number }) => void;

export const NodeAddContext = createContext<NodeAddRequest | null>(null);
