import {
  Background,
  BackgroundVariant,
  ConnectionLineType,
  ConnectionMode,
  Controls,
  MarkerType,
  MiniMap,
  ReactFlow,
  ReactFlowProvider,
  addEdge,
  applyEdgeChanges,
  applyNodeChanges,
  useReactFlow,
  type Connection,
  type Edge,
  type EdgeChange,
  type Node,
  type NodeChange,
} from "@xyflow/react";
import "@xyflow/react/dist/style.css";

import { createFileRoute, useNavigate } from "@tanstack/react-router";
import { useCallback, useEffect, useMemo, useRef, useState, type DragEvent } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import { ApiError } from "@/api/request";
import type {
  EdgeDefinition,
  NodeDefinition,
  NodeManifestDto,
  RetryPolicy,
  WorkflowActivationInput,
  WorkflowDefinition,
  WorkflowDetail,
  WorkflowVariable,
} from "@/api/workflows";
import { deriveActivations } from "@/lib/workflowActivations";
import { migrateLegacyPrTriggerConfig } from "@/lib/migrateTriggerConfig";
import { NodeRetryEditor } from "@/components/workflows/NodeRetryEditor";
import { SchemaForm } from "@/components/workflows/SchemaForm";
import { NodePresetPicker } from "@/components/workflows/NodePresetPicker";
import { PostMessageInputsEditor } from "@/components/workflows/PostMessageInputsEditor";
import { ERROR_HANDLE, errorRouteTarget, setErrorRoute } from "@/lib/workflowErrorRoute";
import { introspectScope } from "@/components/workflows/scope-introspection";
import { StartNodeInputsEditor } from "@/components/workflows/StartNodeInputsEditor";
import { SubworkflowEditor } from "@/components/workflows/SubworkflowEditor";
import { LoopEditor } from "@/components/workflows/LoopEditor";
import { VariableTablePanel } from "@/components/workflows/VariableTablePanel";
import { WorkflowNode, type WorkflowNodeData } from "@/components/workflows/WorkflowNode";
import { NodeAddContext, type NodeAddRequest } from "@/components/workflows/nodeAddContext";
import { NodeAddMenu } from "@/components/workflows/NodeAddMenu";
import { nodeIconFor } from "@/components/workflows/nodeIcon";
import { definitionToRfNodes, fitLoopSizes, LOOP_CONTAINER_W, LOOP_CONTAINER_H } from "@/components/workflows/definitionToRfNodes";
import { bodyStartTypeKey, CATCH_HANDLE, isBodyStartTypeKey, isContainerKind } from "@/components/workflows/workflowContainers";
import { useAlert } from "@/components/dialog";
import { WorkflowVariablesPanel } from "@/components/workflows/WorkflowVariablesPanel";
import { RunWorkflowModal } from "@/components/workflows/RunWorkflowModal";
import { RunViewerDialog } from "@/components/workflows/RunViewerDialog";
import { RunHistoryDialog } from "@/components/workflows/RunHistoryDialog";

// Five panels covering every scope the engine resolves:
//   - variables  → wf.*    (Variable table, scope=Workflow, persists immediately via API)
//   - team       → team.*  (Variable table, scope=Team — SHARED across every workflow in
//                           this team; the panel's tip surfaces that semantic so authors
//                           don't accidentally treat it as workflow-local)
//   - inputs     → input.*  (declared in workflow JSON, saved with the definition)
//   - outputs    → output.* (declared in workflow JSON, saved with the definition)
//   - system     → sys.*    (engine-injected, read-only documentation)
// Team variables are *also* manageable from Team Settings → Variables; the in-editor panel
// is a convenience so authors don't have to leave the canvas to add a shared value.
type OpenVarsPanel = "variables" | "team" | "inputs" | "outputs" | "system";
import { useNodeManifests, useRunWorkflowManually, useSystemVariables, useUpdateWorkflow, useWorkflow } from "@/hooks/use-workflows";
// Real per-scope variable lists feed the autocomplete picker + toolbar counts.
import { useTeamVariables, useWorkflowVariables } from "@/hooks/use-variables";
import { useProjects } from "@/hooks/use-projects";
import { projectVariablesApi, type VariableSummary } from "@/api/variables";
import { useQueries } from "@tanstack/react-query";

/**
 * Dify-style canvas editor. Three-pane layout:
 *
 *   [palette]        [ ───────── canvas ───────── ]        [inspector]
 *      ↓                  React Flow                          schema-driven form
 *      drag a node onto the canvas                            for the selected node
 *      to add it
 *
 * Generic by design — the palette + inspector are driven entirely by
 * `GET /api/workflows/node-manifests`. Any new node a plugin registers shows up
 * in the palette automatically, and its config + inputs forms come for free from
 * its JSON schemas. No editor code change required for new node types.
 */
export const Route = createFileRoute("/_app/teams/$teamSlug/workflows/$workflowId/")({
  component: WorkflowEditorPage,
});

function WorkflowEditorPage() {
  return (
    // ReactFlowProvider must wrap any component that calls useReactFlow().
    <ReactFlowProvider>
      <EditorShell />
    </ReactFlowProvider>
  );
}

function EditorShell() {
  const { teamSlug, workflowId } = Route.useParams();
  const navigate = useNavigate();
  const workflow = useWorkflow(workflowId);
  const manifests = useNodeManifests();
  const update = useUpdateWorkflow();

  if (workflow.isLoading || manifests.isLoading) {
    return (
      <section className="ct">
        <div className="ct-body"><div className="ct-empty"><div className="ct-empty-h">Loading editor…</div></div></div>
      </section>
    );
  }

  if (workflow.error instanceof ApiError || !workflow.data) {
    return (
      <section className="ct">
        <div className="ct-body">
          <div className="cn-banner cn-banner-err" style={{ margin: 16 }}>
            <div className="cn-banner-h">Agent not found</div>
            <div className="cn-banner-p">{workflow.error?.message ?? "It may have been deleted."}</div>
          </div>
        </div>
      </section>
    );
  }

  return (
    <Editor
      workflow={workflow.data}
      manifests={manifests.data ?? []}
      onBackToList={() => navigate({ to: "/teams/$teamSlug/workflows", params: { teamSlug } })}
      saving={update.isPending}
      onSave={(input) => update.mutateAsync({ workflowId, input })}
    />
  );
}

interface EditorProps {
  workflow: WorkflowDetail;
  manifests: NodeManifestDto[];
  onBackToList: () => void;
  saving: boolean;
  onSave: (input: {
    name: string;
    description: string | null;
    definition: WorkflowDefinition;
    activations: WorkflowActivationInput[];
  }) => Promise<unknown>;
}

function Editor({ workflow, manifests, onBackToList, saving, onSave }: EditorProps) {
  // Editable workflow header — start as a copy of the persisted name; the user can rename
  // in place via the input above the canvas.
  const [name, setName] = useState(workflow.name);

  // React Flow state derived from the workflow definition on first mount + every time the
  // user re-enters the route. Subsequent changes are owned by React Flow itself.
  const manifestByType = useMemo(() => new Map(manifests.map((m) => [m.typeKey, m])), [manifests]);

  // Live variable lists drive (a) the toolbar count badges on Variables/Team and (b) the
  // per-name autocomplete in the {{}} picker so {{team.test}} appears the moment the
  // operator saves it via the Team panel. React-Query's cache invalidation in
  // useSetTeamVariable/useSetWorkflowVariable means we don't need an explicit refresh here.
  const workflowVariablesQuery = useWorkflowVariables(workflow.id);
  const teamVariablesQuery = useTeamVariables();
  const systemVariablesQuery = useSystemVariables();
  const workflowVariablesList = workflowVariablesQuery.data ?? [];
  const teamVariablesList = teamVariablesQuery.data ?? [];
  const systemVariablesList = systemVariablesQuery.data ?? [];

  // Project variables — fan-out one query per project in the team. The backend
  // resolves {{project.{slug}.{name}}} against any project in the team (not just
  // the workflow's own), so the picker needs the full set so authors can pick
  // any legal ref. useQueries is the right primitive: dynamic N, all reads share
  // the same React Query cache key as the project-variable mutations (see
  // useSet/DeleteProjectVariable invalidations), so a write on one panel
  // refreshes the autocomplete here without a page reload.
  const projectsQuery = useProjects();
  const projectList = projectsQuery.data ?? [];
  const projectVarQueries = useQueries({
    queries: projectList.map((p) => ({
      queryKey: ["project-variables", p.id],
      queryFn: () => projectVariablesApi.list(p.id),
      staleTime: 60_000,
    })),
  });
  const projectVariablesForScope = useMemo(
    () => projectList.map((p, i) => ({
      slug: p.slug,
      variables: (projectVarQueries[i]?.data ?? []) as ReadonlyArray<VariableSummary>,
    })),
    [projectList, projectVarQueries],
  );

  const [nodes, setNodes] = useState<Node<WorkflowNodeData>[]>(() =>
    definitionToRfNodes(workflow.definition, manifestByType)
  );
  const [edges, setEdges] = useState<Edge[]>(() => definitionToRfEdges(workflow.definition));
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [unsaved, setUnsaved] = useState(false);
  // The node picker opened by a node's "+" — anchored at the click point, sourced from `fromId`.
  const [addMenu, setAddMenu] = useState<{ at: { x: number; y: number }; fromId: string } | null>(null);
  // A node's "+" was clicked → select it (so the new node auto-links from it) and open the picker.
  const onAddFrom = useCallback<NodeAddRequest>((nodeId, at) => {
    setSelectedId(nodeId);
    setAddMenu({ at, fromId: nodeId });
  }, []);

  // Lookup the selected node's definition (config/inputs come from React Flow node data).
  const selectedNode = nodes.find((n) => n.id === selectedId) ?? null;
  const selectedManifest = selectedNode ? manifestByType.get(selectedNode.data.typeKey) ?? null : null;

  // A manual Start node owns the workflow's input fields (Dify-style), so its inspector edits
  // `workflowInputs` directly and the redundant side-panel "Inputs" tab is hidden when one exists.
  const hasManualTrigger = nodes.some((n) => manifestByType.get(n.data.typeKey)?.isManual);

  // Per-node config + inputs state. The React Flow node holds the lightweight visual
  // data; the full config/inputs JSON lives here, keyed by node id, and gets folded
  // back into the WorkflowDefinition at save time.
  const [configs, setConfigs] = useState<Record<string, Record<string, unknown>>>(() => initialBag(workflow.definition, "config"));
  const [inputs, setInputs] = useState<Record<string, Record<string, unknown>>>(() => initialBag(workflow.definition, "inputs"));
  const [nodeLabels, setNodeLabels] = useState<Record<string, string>>(() =>
    Object.fromEntries(workflow.definition.nodes.map((n) => [n.id, n.label ?? ""]))
  );
  // Per-node retry-on-failure policy, keyed by node id. Only nodes that declared one are present;
  // absent = no retry. Folded back into each NodeDefinition.retry at save (rfToDefinition).
  const [retries, setRetries] = useState<Record<string, RetryPolicy>>(() =>
    Object.fromEntries(
      workflow.definition.nodes.filter((n) => n.retry).map((n) => [n.id, n.retry as RetryPolicy])
    )
  );

  // Workflow IO contract — inputs/outputs are part of the workflow JSON because they
  // define the workflow's external API. Persisted via the Save button. The wf.* and
  // team.* scopes live in the unified `variable` table; VariableTablePanel persists
  // every change immediately via API, so no editor-side state is needed for them.
  const [workflowInputs, setWorkflowInputs] = useState<import("@/api/workflows").WorkflowVariable[]>(workflow.definition.inputs ?? []);
  const [workflowOutputs, setWorkflowOutputs] = useState<import("@/api/workflows").WorkflowVariable[]>(workflow.definition.outputs ?? []);

  // Re-sync the variable scopes from props whenever the workflow refetches AND the user
  // has no unsaved local changes. Reason: useState's initializer only fires on first
  // mount, so a fresh fetch (after Save → cache invalidate → refetch) would otherwise
  // leave local state stuck at the original load. We compare against the previous
  // workflow snapshot so we only re-sync on a real prop change, never on every render.
  const lastSyncedVersionRef = useRef<{ id: string; lastModifiedDate: string }>({
    id: workflow.id,
    lastModifiedDate: workflow.lastModifiedDate,
  });
  const [openVarsPanel, setOpenVarsPanel] = useState<OpenVarsPanel | null>(null);
  const markVarsDirty = () => setUnsaved(true);

  // In-editor run + history. runFormOpen → the input form (when the workflow declares inputs);
  // viewerRunId → the live run-detail dialog; historyOpen → the runs list dialog.
  const alert = useAlert();
  const runManually = useRunWorkflowManually();
  const [runFormOpen, setRunFormOpen] = useState(false);
  const [viewerRunId, setViewerRunId] = useState<string | null>(null);
  const [historyOpen, setHistoryOpen] = useState(false);

  const { screenToFlowPosition, getViewport, getIntersectingNodes, getInternalNode } = useReactFlow();
  const canvasRef = useRef<HTMLDivElement | null>(null);

  // ─── Canvas events ──────────────────────────────────────────────────────────
  const onNodesChange = (changes: NodeChange[]) => {
    setNodes((nds) => {
      const next = applyNodeChanges(changes, nds) as Node<WorkflowNodeData>[];
      // NodeResizer emits "dimensions" changes as the user drags a corner. Record the new size on the
      // container's data so it (a) overrides auto-fit from here on and (b) round-trips into the definition.
      const resized = changes.flatMap((c) => (c.type === "dimensions" && c.dimensions ? [{ id: c.id, dim: c.dimensions }] : []));
      if (resized.length === 0) return next;
      const sizeById = new Map(resized.map((r) => [r.id, r.dim]));
      return next.map((n) =>
        sizeById.has(n.id) && isContainerKind(n.data.kind)
          ? { ...n, data: { ...n.data, size: { width: sizeById.get(n.id)!.width, height: sizeById.get(n.id)!.height } } }
          : n);
    });
    // A finished resize (resizing:false) is a save-worthy edit, same as a move/remove.
    if (changes.some((c) => c.type === "position" || c.type === "remove" || (c.type === "dimensions" && c.resizing === false))) setUnsaved(true);
  };
  const onEdgesChange = (changes: EdgeChange[]) => {
    setEdges((eds) => applyEdgeChanges(changes, eds));
    if (changes.some((c) => c.type === "remove")) setUnsaved(true);
  };

  // ─── Drag a step INTO / OUT of a container (loop / try) (Dify-style nesting) ──
  // While dragging a node, highlight the container it's hovering as a drop zone; on release, reparent
  // it (or unparent it when dragged out). A reparented node becomes part of the container's body — it
  // moves WITH the box and runs inside it (per loop iteration / inside the try scope).
  const containerUnder = (node: Node): string | null => {
    // A container CAN nest inside another container — but never inside itself or its own descendant
    // (that would make a node its own ancestor). Among the overlapping valid containers, the innermost
    // (deepest) wins, so dropping over a nested container targets it rather than its outer one.
    const insideDragged = (id: string): boolean => {
      let p = nodes.find((n) => n.id === id)?.parentId;
      while (p) { if (p === node.id) return true; p = nodes.find((n) => n.id === p)?.parentId; }
      return false;
    };
    const targets = getIntersectingNodes(node).filter((n) =>
      isContainerKind((n.data as Partial<WorkflowNodeData>)?.kind) && n.id !== node.id && !insideDragged(n.id));
    return targets.length === 0
      ? null
      : targets.reduce((deep, n) => (nodeDepth(n.id, nodes) > nodeDepth(deep.id, nodes) ? n : deep)).id;
  };

  const highlightContainer = (targetId: string | null) =>
    setNodes((nds) => {
      let changed = false;
      const next = nds.map((n) => {
        if (!isContainerKind((n.data as Partial<WorkflowNodeData>)?.kind)) return n;
        const want = n.id === targetId ? "wf-rf-loop-droptarget" : undefined;
        if ((n.className ?? undefined) === want) return n;
        changed = true;
        return { ...n, className: want };
      });
      return changed ? next : nds;
    });

  const onNodeDrag = (_e: unknown, node: Node) => highlightContainer(containerUnder(node));

  const onNodeDragStop = (_e: unknown, node: Node) => {
    const targetId = containerUnder(node);
    highlightContainer(null);

    // A container's entry marker (loop_start / try_start) can never be orphaned — if it would land
    // outside any container, leave it parented (extent:"parent" already clips a healthy one).
    if (isBodyStartTypeKey((node.data as Partial<WorkflowNodeData>)?.typeKey) && targetId === null) return;

    const currentParent = node.parentId ?? null;
    if (targetId === currentParent) return;   // no scope change

    const abs = getInternalNode(node.id)?.internals.positionAbsolute ?? node.position;
    const parentAbs = targetId ? (getInternalNode(targetId)?.internals.positionAbsolute ?? { x: 0, y: 0 }) : { x: 0, y: 0 };

    // No `extent: "parent"` — that would CLIP the child inside the box so it could never be dragged
    // back out. Keeping just parentId lets it move with the loop yet still be dragged out (→ unparent).
    setNodes((nds) => {
      const reparented = nds.map((n) =>
        n.id !== node.id
          ? n
          : targetId
            ? { ...n, parentId: targetId, position: { x: abs.x - parentAbs.x, y: abs.y - parentAbs.y } }
            : { ...n, parentId: undefined, position: abs });

      // Auto-fit every container to its (new) children, so dropping a container INTO another grows the
      // outer to wrap it — no overlap — and pulling one out shrinks it back.
      const loopSizes = fitLoopSizes(reparented.map((n) => ({
        id: n.id,
        parentId: n.parentId,
        x: n.position.x,
        y: n.position.y,
        isContainer: isContainerKind((n.data as Partial<WorkflowNodeData>)?.kind),
      })));

      // Recompute every node's depth-based zIndex against the NEW parent structure, so moving a whole
      // subtree (e.g. a container with its body) into/out of another re-stacks it correctly at any depth.
      // Top-level non-container nodes keep no explicit zIndex (default stacking), matching the load path.
      return reparented.map((n) => {
        const depth = nodeDepth(n.id, reparented);
        const isContainer = isContainerKind((n.data as Partial<WorkflowNodeData>)?.kind);
        const zIndex = depth === 0 && !isContainer ? undefined : depth;
        // A user-resized container keeps its explicit size; an auto one re-fits to its (new) children.
        const size = (n.data as Partial<WorkflowNodeData>).size ?? loopSizes.get(n.id);
        return isContainer && size ? { ...n, zIndex, style: { ...n.style, ...size } } : { ...n, zIndex };
      });
    });

    // Re-scoping severs any edge that now crosses the loop boundary — a step only connects to siblings
    // in the same scope (both top-level, or both in the same loop body). So dropping a step IN or
    // pulling it OUT auto-releases its now-invalid bindings.
    const parentOf = new Map(nodes.map((n) => [n.id, n.parentId ?? null]));
    parentOf.set(node.id, targetId);
    setEdges((eds) => eds.filter((e) => {
      if (e.source !== node.id && e.target !== node.id) return true;
      return (parentOf.get(e.source) ?? null) === (parentOf.get(e.target) ?? null);
    }));

    setUnsaved(true);
  };
  const onConnect = (params: Connection) => {
    setEdges((eds) => {
      // Error-handle drags go through the same helper as the inspector control, so a node keeps
      // at most one error route (drawing a new one replaces the old).
      if (params.sourceHandle === ERROR_HANDLE && params.source && params.target)
        return setErrorRoute(eds, params.source, params.target);

      // De-dupe — React Flow itself doesn't prevent duplicate edges; we do (keyed by handle too,
      // so a normal edge and an error/catch edge to the same target can coexist).
      const exists = eds.some((e) => e.source === params.source && e.target === params.target && (e.sourceHandle ?? null) === (params.sourceHandle ?? null));
      if (exists) return eds;
      // A try's catch edge is a normal (multi-allowed) branch edge — just style it as the failure path.
      const className = params.sourceHandle === CATCH_HANDLE ? "wf-rf-edge-catch" : undefined;
      return addEdge({ ...params, type: "default", animated: true, ...(className ? { className } : {}) }, eds);
    });
    setUnsaved(true);
  };

  /**
   * Live rule check fired by React Flow while the user drags a connection line. Returning
   * false makes the line render in the "invalid" state (no snap, dim colour) so the user
   * gets immediate feedback BEFORE they release the mouse. Same checks run again on
   * onConnect for safety. Rules — match what the backend DefinitionValidator enforces:
   *
   *   1. Source ≠ target (no self-loop)
   *   2. Edge doesn't already exist
   *   3. Target is not a Trigger (Triggers have no inputs)
   *   4. Source is not a Terminal (Terminals have no outputs)
   *   5. Adding the edge wouldn't create a cycle (DAG-only)
   */
  const isValidConnection = (params: Connection | Edge) => {
    const sourceId = "source" in params ? params.source : null;
    const targetId = "target" in params ? params.target : null;
    if (!sourceId || !targetId) return false;
    if (sourceId === targetId) return false;

    const exists = edges.some((e) => e.source === sourceId && e.target === targetId);
    if (exists) return false;

    const sourceNode = nodes.find((n) => n.id === sourceId);
    const targetNode = nodes.find((n) => n.id === targetId);
    if (!sourceNode || !targetNode) return false;
    if (targetNode.data.kind === "Trigger") return false;
    if (sourceNode.data.kind === "Terminal") return false;

    // Cycle check: would the new edge create source → … → source? Walk forward from
    // the target; if we reach the source, adding the edge closes a loop.
    if (createsCycle(sourceId, targetId, edges)) return false;

    return true;
  };

  // ─── Trigger-count guard ────────────────────────────────────────────────────
  // Engine assumes exactly one Trigger per workflow (the entry point — the engine
  // starts walking from it). The UI mirrors that constraint by disabling the trigger
  // palette items once one is on the canvas, so the user can't add a second.
  const hasTrigger = useMemo(() => nodes.some((n) => n.data.kind === "Trigger"), [nodes]);
  const isPaletteItemDisabled = (m: NodeManifestDto): { disabled: boolean; reason?: string } => {
    if (m.kind === "Trigger" && hasTrigger) {
      return { disabled: true, reason: "Only one trigger per workflow. Delete the existing trigger first." };
    }
    return { disabled: false };
  };

  // ─── Adding nodes — shared by drag-drop AND click-to-add ────────────────────
  // Position strategy:
  //   1. Explicit screen coords (drag-drop): convert via screenToFlowPosition
  //   2. With a selected node: 200px below the selected node (auto-link source → new)
  //   3. Otherwise: visible centre of the canvas (in flow coords)
  // Auto-link only happens for click-to-add AND only when the new node has a target
  // handle (Trigger nodes have no input, so we never auto-link INTO them).
  const addNodeFromManifest = (
    manifest: NodeManifestDto,
    options: { screen?: { x: number; y: number }; autoLink?: boolean } = {}
  ) => {
    const id = uniqueNodeId(manifest.typeKey, nodes);
    const position = computeAddPosition(manifest, options.screen);

    const isContainer = isContainerKind(manifest.kind);

    const newNode: Node<WorkflowNodeData> = {
      id,
      type: "wf",
      position,
      data: {
        nodeId: id,
        typeKey: manifest.typeKey,
        displayName: manifest.displayName,
        iconKey: manifest.iconKey,
        kind: manifest.kind,
        category: manifest.category,
        label: null,
        // A manual start node shows the current workflow input fields on its card.
        ...(manifest.isManual ? { inputFields: workflowInputs } : {}),
      },
      // A container (loop / try) is a box sized to hold its body subgraph. It's draggable from anywhere
      // on the box; its body steps render ABOVE it (React Flow parent/child z-order) so they stay
      // clickable and grabbing one drags the step, while grabbing empty space drags the whole box.
      ...(isContainer ? { style: { width: LOOP_CONTAINER_W, height: LOOP_CONTAINER_H }, zIndex: 0 } : {}),
    };

    // A container ships with its body-entry marker (flow.loop_start / flow.try_start) pre-placed
    // inside — the marker is never hand-dragged from the palette, so it's created here, parented to it.
    const startTypeKey = bodyStartTypeKey(manifest.typeKey);
    const startManifest = startTypeKey ? manifestByType.get(startTypeKey) : undefined;
    const startId = startTypeKey ? uniqueNodeId(startTypeKey, [...nodes, newNode]) : null;
    const startNode: Node<WorkflowNodeData> | null = startId && startTypeKey
      ? {
          // extent:"parent" locks the entry marker inside its container — it can never be dragged out.
          id: startId, type: "wf", parentId: id, position: { x: 40, y: 90 }, zIndex: 1, extent: "parent",
          data: {
            nodeId: startId, typeKey: startTypeKey, displayName: startManifest?.displayName ?? "Start",
            iconKey: startManifest?.iconKey ?? "play", kind: "Regular", category: startManifest?.category ?? "Logic", label: null,
          },
        }
      : null;

    setNodes((nds) => (startNode ? [...nds, newNode, startNode] : [...nds, newNode]));
    setConfigs((c) => ({ ...c, [id]: defaultsFromSchema(manifest.configSchema), ...(startId ? { [startId]: {} } : {}) }));
    setInputs((c) => ({ ...c, [id]: defaultsFromSchema(manifest.inputSchema), ...(startId ? { [startId]: {} } : {}) }));
    setNodeLabels((l) => ({ ...l, [id]: "", ...(startId ? { [startId]: "" } : {}) }));
    setSelectedId(id);
    setUnsaved(true);

    // Auto-link from the previously-selected node into the new one. Only when:
    //   - this was a click-to-add (not a free-form drop)
    //   - there IS a previously-selected node
    //   - the new node accepts incoming edges (Trigger doesn't)
    //   - the source node can emit edges (Terminal can't)
    if (options.autoLink && selectedId && selectedId !== id && manifest.kind !== "Trigger") {
      const sourceNode = nodes.find((n) => n.id === selectedId);
      if (sourceNode && sourceNode.data.kind !== "Terminal") {
        // addEdge accepts a Connection (no id required) and returns the edge with a
        // generated id. Cast to Connection to satisfy the overload — sourceHandle /
        // targetHandle null is fine because our nodes only have one handle per side.
        setEdges((eds) => addEdge(
          { source: selectedId, target: id, sourceHandle: null, targetHandle: null, type: "default", animated: true } as Connection,
          eds,
        ));
      }
    }
  };

  const computeAddPosition = (_manifest: NodeManifestDto, screen?: { x: number; y: number }) => {
    if (screen) return screenToFlowPosition(screen);

    // Place to the RIGHT of the currently-selected node when one exists — the left→right "next step"
    // flow (Dify-style horizontal layout).
    if (selectedId) {
      const src = nodes.find((n) => n.id === selectedId);
      if (src) return { x: src.position.x + 320, y: src.position.y };
    }

    // Otherwise drop into the visible centre of the canvas. Convert by un-applying the
    // current viewport transform manually — useReactFlow().screenToFlowPosition needs
    // a real screen coord, which we don't have for a click-to-add from the palette.
    const rect = canvasRef.current?.getBoundingClientRect();
    if (!rect) return { x: 200, y: 200 };

    const { x, y, zoom } = getViewport();
    const offsetForNodeSize = 100;   // half of approximate node width so it lands centred
    return {
      x: (rect.width / 2 - x) / zoom - offsetForNodeSize,
      y: (rect.height / 2 - y) / zoom - 30,
    };
  };

  // ─── Palette drag-and-drop ──────────────────────────────────────────────────
  const onDragOver = (e: DragEvent) => {
    e.preventDefault();
    e.dataTransfer.dropEffect = "move";
  };
  const onDrop = (e: DragEvent) => {
    e.preventDefault();
    const typeKey = e.dataTransfer.getData("application/x-workflow-node");
    if (!typeKey) return;
    const manifest = manifestByType.get(typeKey);
    if (!manifest) return;

    // Same trigger-limit guard as click-to-add.
    if (isPaletteItemDisabled(manifest).disabled) return;

    addNodeFromManifest(manifest, { screen: { x: e.clientX, y: e.clientY } });
  };

  // ─── Click-to-add from palette ──────────────────────────────────────────────
  const onPaletteClick = (manifest: NodeManifestDto) => {
    // Defense-in-depth — the palette item is already disabled when the limit's hit,
    // but a keyboard-driven activate could still fire. Silently no-op rather than
    // creating an invalid second trigger.
    const { disabled } = isPaletteItemDisabled(manifest);
    if (disabled) return;

    addNodeFromManifest(manifest, { autoLink: true });
  };

  // ─── Inspector ──────────────────────────────────────────────────────────────
  const updateConfig = (id: string, next: Record<string, unknown>) => {
    setConfigs((c) => ({ ...c, [id]: next }));
    setUnsaved(true);
  };
  const updateInputs = (id: string, next: Record<string, unknown>) => {
    setInputs((c) => ({ ...c, [id]: next }));
    setUnsaved(true);
  };
  const updateLabel = (id: string, label: string) => {
    setNodeLabels((l) => ({ ...l, [id]: label }));
    setNodes((nds) => nds.map((n) => n.id === id ? { ...n, data: { ...n.data, label: label || null } } : n));
    setUnsaved(true);
  };
  const updateRetry = (id: string, next: RetryPolicy | null) => {
    // null clears the policy (remove the key) so the saved definition omits retry entirely.
    setRetries((r) => {
      const copy = { ...r };
      if (next) copy[id] = next; else delete copy[id];
      return copy;
    });
    setUnsaved(true);
  };

  // Mirror the workflow input fields onto the Manual start node's card data so the entry node
  // renders its inputs (Dify-style). Called from the inputs editor's onChange + the refetch
  // re-hydrate — never from an effect body, so it can't trigger cascading renders.
  const syncManualNodeInputFields = (fields: WorkflowVariable[]) => {
    setNodes((nds) => nds.map((n) =>
      manifestByType.get(n.data.typeKey)?.isManual ? { ...n, data: { ...n.data, inputFields: fields } } : n
    ));
  };

  // ─── Save ───────────────────────────────────────────────────────────────────
  const handleSave = async () => {
    const definition = rfToDefinition(nodes, edges, configs, inputs, nodeLabels, retries, workflowInputs, workflowOutputs);
    const activations = deriveActivations(definition, workflow.activations, manifestByType);
    try {
      await onSave({ name, description: workflow.description, definition, activations });
    } catch (e) {
      // Surface the failure instead of silently dropping the save — a rejected save (e.g. a
      // definition validation error like "exactly one trigger") otherwise looks like the edits
      // just "didn't stick", and the workflow stays on its last good version.
      await alert({
        title: "Couldn't save workflow",
        message: e instanceof ApiError ? e.message : e instanceof Error ? e.message : "Unexpected error while saving.",
        variant: "error",
      });
      throw e;   // let callers (Run) know the save failed so they don't run a stale version
    }
    setUnsaved(false);
    // No navigation on save — keep the user on the canvas. The "Unsaved changes" pill
    // disappears and the user can immediately keep editing. Matches Dify behaviour.
  };

  // ─── In-editor run + history (Dify-style — never leave the canvas) ────────────
  const startRun = async (payload?: Record<string, unknown>) => {
    // A manual run executes the LATEST SAVED version, so flush any pending edits first —
    // otherwise "Run" would silently execute a stale definition. The run mutation reads the
    // freshly-bumped version server-side.
    if (unsaved) await handleSave();
    const result = await runManually.mutateAsync({ workflowId: workflow.id, payload });
    setRunFormOpen(false);
    setViewerRunId(result.runId);
  };

  const handleRun = async () => {
    try {
      if (unsaved) await handleSave();
    } catch {
      return;   // save failed — don't run a half-saved definition
    }
    // Declared inputs → collect them first (same form as the runs page); else run immediately.
    if (workflowInputs.length > 0) { setRunFormOpen(true); return; }
    await startRun().catch(() => {});
  };

  // Block accidental navigation when there are unsaved changes.
  useEffect(() => {
    if (!unsaved) return;
    const handler = (e: BeforeUnloadEvent) => { e.preventDefault(); e.returnValue = ""; };
    window.addEventListener("beforeunload", handler);
    return () => window.removeEventListener("beforeunload", handler);
  }, [unsaved]);

  // Re-hydrate the variable-scope state when the workflow refetches with a fresher
  // lastModifiedDate (e.g. after Save → cache invalidate → refetch). useState's
  // initializer only fires on first mount, so without this effect the local scope
  // arrays stay locked to whatever they were on initial load. Guarded by `unsaved`:
  // if the user has pending local edits, we do NOT clobber them. We also skip the
  // very first match (same lastModifiedDate as the original mount).
  useEffect(() => {
    if (unsaved) return;
    const last = lastSyncedVersionRef.current;
    if (last.id === workflow.id && last.lastModifiedDate === workflow.lastModifiedDate) return;

    setWorkflowInputs(workflow.definition.inputs ?? []);
    setWorkflowOutputs(workflow.definition.outputs ?? []);
    syncManualNodeInputFields(workflow.definition.inputs ?? []);
    lastSyncedVersionRef.current = { id: workflow.id, lastModifiedDate: workflow.lastModifiedDate };
  }, [workflow, unsaved]);

  return (
    <div className="wf-editor">
      {/* Top bar */}
      <header className="wf-editor-bar">
        <button className="btn btn-ghost" onClick={onBackToList} title="Back to agents">
          <Ic.ArrowLeft size={13} />
        </button>
        <input
          className="wf-editor-name"
          value={name}
          onChange={(e) => { setName(e.target.value); setUnsaved(true); }}
          placeholder="Agent name"
        />

        <div className="wf-editor-bar-spacer" />
        {unsaved && <span className="wf-editor-unsaved">Unsaved changes</span>}

        {/* Right-side toolbar — three button groups separated by dividers so the operator
            can scan by purpose at a glance:
              · Group 1: scope-reading data (Variables / System / Environment)
              · Group 2: workflow IO contract (Inputs / Outputs)
              · Group 3: run actions (history / Save)
            Each group lives inside its own .wf-editor-toolbar-group; the .sep span draws
            the divider between them. Tooltips spell out the {{ref}} prefix for each scope. */}
        <div className="wf-editor-toolbar" role="toolbar" aria-label="Workflow scopes + actions">
          {/* Leftmost: run the workflow in-place (Dify-style) + open run history — both stay on
              the canvas via dialogs rather than navigating to the runs page. */}
          <div className="wf-editor-toolbar-group" aria-label="Run">
            <button
              className="wf-editor-toolbar-run"
              onClick={() => void handleRun()}
              disabled={runManually.isPending}
              title="Run this agent now"
            >
              <Ic.Play size={13} /> {runManually.isPending ? "Running…" : "Run"}
            </button>
            <button className="wf-editor-toolbar-btn" onClick={() => setHistoryOpen(true)} title="Run history">
              <Ic.Clock size={14} />
            </button>
          </div>

          <span className="wf-editor-toolbar-sep" aria-hidden="true" />

          <div className="wf-editor-toolbar-group" aria-label="Scope variables">
            {/* Left-to-right = scope widens: workflow → team → engine. */}
            <ToolbarButton
              icon={<Ic.Workflow size={14} />}
              label="Variables"
              count={workflowVariablesList.length}
              active={openVarsPanel === "variables"}
              onClick={() => setOpenVarsPanel((p) => p === "variables" ? null : "variables")}
              tooltip="This workflow only. {{wf.X}}"
            />
            <ToolbarButton
              icon={<Ic.Key size={14} />}
              label="Team"
              count={teamVariablesList.length}
              active={openVarsPanel === "team"}
              onClick={() => setOpenVarsPanel((p) => p === "team" ? null : "team")}
              tooltip="Shared across this team. {{team.X}}"
            />
            <ToolbarButton
              icon={<Ic.Box size={14} />}
              label="System"
              count={systemVariablesList.length}
              active={openVarsPanel === "system"}
              onClick={() => setOpenVarsPanel((p) => p === "system" ? null : "system")}
              tooltip="Engine-injected per run. {{sys.X}}"
            />
          </div>

          <span className="wf-editor-toolbar-sep" aria-hidden="true" />

          <div className="wf-editor-toolbar-group" aria-label="Workflow IO">
            {/* When a Manual start node exists it owns the input fields (edited on the node),
                so the redundant side-panel Inputs tab is hidden. Workflows without a manual
                trigger (e.g. event-driven ones that map an event payload onto inputs) keep it. */}
            {!hasManualTrigger && (
              <ToolbarButton
                icon={<Ic.Zap size={14} />}
                label="Inputs"
                count={workflowInputs.length}
                active={openVarsPanel === "inputs"}
                onClick={() => setOpenVarsPanel((p) => p === "inputs" ? null : "inputs")}
                tooltip="Workflow Inputs — per-run parameters the caller passes in. Reference via {{input.X}}."
              />
            )}
            <ToolbarButton
              icon={<Ic.CircleStop size={14} />}
              label="Outputs"
              count={workflowOutputs.length}
              active={openVarsPanel === "outputs"}
              onClick={() => setOpenVarsPanel((p) => p === "outputs" ? null : "outputs")}
              tooltip="Workflow Outputs — what this workflow returns on Success. Filled by the Terminal node's Inputs map."
            />
          </div>

          <span className="wf-editor-toolbar-sep" aria-hidden="true" />

          <div className="wf-editor-toolbar-group" aria-label="Save">
            <button
              className={`wf-editor-toolbar-publish ${unsaved ? "wf-editor-toolbar-publish-dirty" : ""}`}
              onClick={() => void handleSave().catch(() => {})}
              disabled={saving}
              title={unsaved ? "Save the current draft" : "All changes saved"}
            >
              {saving ? "Saving…" : "Save"}
            </button>
          </div>
        </div>
      </header>

      {/* Slide-over for the Variables / Inputs / Outputs / System panels. Closes on
          outside-click of the panel via the mask. The current toolbar selection drives
          which one is shown. System is read-only — onChange is still required by the
          component contract but the panel never invokes it. */}
      {openVarsPanel && (
        <div className="wf-editor-vars-mask" onClick={() => setOpenVarsPanel(null)}>
          <aside className="wf-editor-vars-drawer" onClick={(e) => e.stopPropagation()}>
            {/* wf + team scopes live in the unified variable table and persist immediately
                via API (the same data the Team Settings page edits — no two-source-of-truth
                risk). Inputs/Outputs/System flow through the workflow definition JSON + the
                editor's Save button. */}
            {openVarsPanel === "variables" && (
              <VariableTablePanel
                scope="Workflow"
                workflowId={workflow.id}
                refPrefix="wf"
                title="Variables"
                subtitle="This workflow only. {{wf.<name>}}"
                tip="Use Secret type for sensitive values."
                emptyHint="No variables yet."
              />
            )}
            {openVarsPanel === "team" && (
              <VariableTablePanel
                scope="Team"
                refPrefix="team"
                title="Team variables"
                subtitle="Shared across every workflow in this team. {{team.<name>}}"
                tip="Edits here affect every workflow in this team."
                emptyHint="No team variables yet."
              />
            )}
            {(openVarsPanel === "inputs" || openVarsPanel === "outputs" || openVarsPanel === "system") && (
              <WorkflowVariablesPanel
                kind={openVarsPanel}
                items={openVarsPanel === "inputs"  ? workflowInputs
                     : openVarsPanel === "outputs" ? workflowOutputs
                                                    : []}
                systemVariables={openVarsPanel === "system" ? systemVariablesList : undefined}
                onChange={(next) => {
                  if (openVarsPanel === "inputs") setWorkflowInputs(next);
                  else if (openVarsPanel === "outputs") setWorkflowOutputs(next);
                  // System: no-op — engine populates these at runtime, not edited here.
                  markVarsDirty();
                }}
              />
            )}
          </aside>
        </div>
      )}

      {/* Three-pane body */}
      <div className="wf-editor-body">
        {/* Left palette */}
        <aside className="wf-palette">
          <div className="wf-palette-h">Nodes</div>
          <div className="wf-palette-hint">Click to add · Drag for precise placement</div>
          {manifests.length === 0 && <div className="wf-palette-empty">No node types loaded.</div>}
          <PaletteSection title="Triggers"  manifests={manifests.filter((m) => m.kind === "Trigger")}  onAdd={onPaletteClick} disabledOf={isPaletteItemDisabled} />
          {/* "Loop" is a Step too; "flow.loop_start" is the loop's internal body-entry marker, added
              automatically with a Loop — never hand-dragged, so it's filtered out of the palette. */}
          <PaletteSection title="Steps"     manifests={manifests.filter((m) => (m.kind === "Regular" || isContainerKind(m.kind)) && !isBodyStartTypeKey(m.typeKey))}  onAdd={onPaletteClick} disabledOf={isPaletteItemDisabled} />
          <PaletteSection title="Endpoints" manifests={manifests.filter((m) => m.kind === "Terminal")} onAdd={onPaletteClick} disabledOf={isPaletteItemDisabled} />
        </aside>

        {/* Canvas */}
        <div ref={canvasRef} className="wf-canvas" onDragOver={onDragOver} onDrop={onDrop}>
          {/* Provider so each custom node can open the "add a node here" picker (React Flow propagates
              context into custom nodes rendered below it). */}
          <NodeAddContext.Provider value={onAddFrom}>
          <ReactFlow
            nodes={nodes}
            edges={edges}
            nodeTypes={NODE_TYPES}
            onNodesChange={onNodesChange}
            onEdgesChange={onEdgesChange}
            onConnect={onConnect}
            onNodeDrag={onNodeDrag}
            onNodeDragStop={onNodeDragStop}
            onNodeClick={(_, n) => setSelectedId(n.id)}
            onPaneClick={() => setSelectedId(null)}
            fitView
            // Live rule check: as the user drags a connection line, this runs every frame
            // and dims the line when the would-be edge violates a rule (self-loop, dup,
            // wrong handle direction, cycle). Snap-to-target is suppressed for invalid drops.
            isValidConnection={isValidConnection}
            // Connection ergonomics: larger snap radius so the user can drop the line
            // anywhere near a handle (not just exactly on it). Loose mode lets a source
            // handle connect to a target handle regardless of which side it's on.
            connectionRadius={40}
            connectionMode={ConnectionMode.Loose}
            // A loop container is a big node that sits UNDER its body steps (zIndex 0 vs the children's
            // zIndex 1) so the steps stay clickable and their handles grabbable. React Flow's default
            // "pop a selected node to z-index 1000" would shove a *selected* loop box on top of its own
            // children — swallowing every pointer event aimed at a child handle, so you couldn't draw an
            // edge inside the loop. Turning elevate-on-select off keeps the container below its body.
            elevateNodesOnSelect={false}
            connectionLineType={ConnectionLineType.Bezier}
            connectionLineStyle={{ stroke: "#D97757", strokeWidth: 2 }}
            defaultEdgeOptions={{
              type: "default",
              animated: true,
              markerEnd: { type: MarkerType.ArrowClosed, color: "#B5B0A2", width: 16, height: 16 },
            }}
            deleteKeyCode={["Backspace", "Delete"]}
            multiSelectionKeyCode={["Meta", "Control"]}
            proOptions={{ hideAttribution: true }}
          >
            <Background variant={BackgroundVariant.Dots} gap={20} size={1} color="#E6E1D5" />
            <Controls showInteractive={false} />
            <MiniMap pannable zoomable maskColor="rgba(248,246,238,0.7)" />
          </ReactFlow>
          </NodeAddContext.Provider>

          {nodes.length === 0 && <CanvasEmptyHint />}

          {/* Node picker opened by a node's "+" — pick a type and it's added to the right + auto-linked. */}
          {addMenu && (
            <NodeAddMenu
              at={addMenu.at}
              manifests={manifests}
              onPick={(m) => { addNodeFromManifest(m, { autoLink: true }); setAddMenu(null); }}
              onClose={() => setAddMenu(null)}
            />
          )}
        </div>

        {/* Right inspector */}
        <aside className="wf-inspector">
          {!selectedNode && <InspectorEmptyHint />}
          {selectedNode && selectedManifest && (
            <NodeInspector
              nodeId={selectedNode.id}
              manifest={selectedManifest}
              label={nodeLabels[selectedNode.id] ?? ""}
              config={configs[selectedNode.id] ?? {}}
              inputs={inputs[selectedNode.id] ?? {}}
              // Live definition snapshot — recomputed every render from React Flow state +
              // workflow Variables/Inputs/Outputs. Drives the autocomplete picker and the
              // upstream outputs hint card so suggestions stay accurate as the user edits.
              liveDefinition={rfToDefinition(nodes, edges, configs, inputs, nodeLabels, retries, workflowInputs, workflowOutputs)}
              manifestByType={manifestByType}
              // Live variable rows from the unified `variable` table. Threaded through to
              // introspectScope so the {{}} picker autocompletes real names (e.g. {{team.test}})
              // the moment the operator saves them via the side panel.
              workflowVariables={workflowVariablesList}
              teamVariables={teamVariablesList}
              // sys.* descriptors fetched from /api/workflows/system-variables — single source
              // of truth lives on the backend (SystemScopeKeys.Descriptors).
              systemVariables={systemVariablesList}
              // Project variables — one entry per project in the team. The picker
              // emits `project.{slug}.{name}` per real variable across all projects.
              projectVariables={projectVariablesForScope}
              // Manual Start node owns the workflow input fields — its inspector edits
              // `workflowInputs` directly (Dify-style "inputs on the start node").
              inputFields={workflowInputs}
              onInputFieldsChange={(next) => { setWorkflowInputs(next); syncManualNodeInputFields(next); markVarsDirty(); }}
              onLabelChange={(v) => updateLabel(selectedNode.id, v)}
              onConfigChange={(v) => updateConfig(selectedNode.id, v)}
              onInputsChange={(v) => updateInputs(selectedNode.id, v)}
              // The flow.subworkflow editor lists the team's workflows; exclude the one being edited.
              currentWorkflowId={workflow.id}
              retry={retries[selectedNode.id] ?? null}
              onRetryChange={(v) => updateRetry(selectedNode.id, v)}
              // Error routing reads/writes the node's `error` edge directly on the edges state.
              errorTarget={errorRouteTarget(edges, selectedNode.id)}
              // Offer only valid handler targets: not self, not a Trigger, and not one that would
              // close a cycle (mirrors isValidConnection, so the dropdown can't author a graph the
              // backend validator would reject at save).
              errorTargetOptions={nodes.filter((n) => n.id !== selectedNode.id && n.data.kind !== "Trigger" && !createsCycle(selectedNode.id, n.id, edges)).map((n) => ({ id: n.id, label: n.id }))}
              onErrorRouteChange={(targetId) => { setEdges((eds) => setErrorRoute(eds, selectedNode.id, targetId)); setUnsaved(true); }}
            />
          )}
        </aside>
      </div>

      {/* In-page run + history (rendered in-tree, inside .acs-root, so RunDetailView's
          .wf-* styles apply). The input form portals to <body> on its own. */}
      {runFormOpen && (
        <RunWorkflowModal
          workflowName={name}
          inputs={workflowInputs}
          pending={runManually.isPending}
          error={runManually.error instanceof ApiError ? runManually.error.message : null}
          onRun={(payload) => { void startRun(payload).catch(() => {}); }}
          onClose={() => setRunFormOpen(false)}
        />
      )}
      {viewerRunId && <RunViewerDialog runId={viewerRunId} onClose={() => setViewerRunId(null)} />}
      {historyOpen && (
        <RunHistoryDialog
          workflowId={workflow.id}
          onPick={(id) => { setHistoryOpen(false); setViewerRunId(id); }}
          onClose={() => setHistoryOpen(false)}
        />
      )}
    </div>
  );
}

// ─── Guidance overlays ────────────────────────────────────────────────────────

function CanvasEmptyHint() {
  return (
    <div className="wf-canvas-hint">
      <div className="wf-canvas-hint-card">
        <Ic.Workflow size={20} />
        <div className="wf-canvas-hint-h">Build your workflow</div>
        <ol className="wf-canvas-hint-steps">
          <li><strong>Click</strong> a node in the left palette to add it</li>
          <li><strong>Drag</strong> from the bottom dot of one node to the top dot of another to connect</li>
          <li><strong>Click</strong> any node to edit its config in the right panel</li>
        </ol>
        <div className="wf-canvas-hint-rules">
          <strong>Rules:</strong> one trigger per workflow · no cycles · triggers have no input · terminals have no output
        </div>
      </div>
    </div>
  );
}

function InspectorEmptyHint() {
  return (
    <div className="wf-inspector-empty">
      <Ic.Settings size={18} />
      <div className="wf-inspector-empty-h">No node selected</div>
      <ul className="wf-inspector-empty-list">
        <li>Click a node on the canvas to edit it</li>
        <li>Click a palette item to add a node</li>
        <li>Press <kbd>Delete</kbd> to remove a selected node or edge</li>
      </ul>
    </div>
  );
}

// ─── Palette ──────────────────────────────────────────────────────────────────

function PaletteSection({ title, manifests, onAdd, disabledOf }: {
  title: string;
  manifests: NodeManifestDto[];
  onAdd: (m: NodeManifestDto) => void;
  disabledOf: (m: NodeManifestDto) => { disabled: boolean; reason?: string };
}) {
  if (manifests.length === 0) return null;
  return (
    <div className="wf-palette-section">
      <div className="wf-palette-section-h">{title}</div>
      {manifests.map((m) => {
        const { disabled, reason } = disabledOf(m);
        return <PaletteItem key={m.typeKey} manifest={m} onAdd={onAdd} disabled={disabled} disabledReason={reason} />;
      })}
    </div>
  );
}

function PaletteItem({ manifest, onAdd, disabled, disabledReason }: {
  manifest: NodeManifestDto;
  onAdd: (m: NodeManifestDto) => void;
  disabled: boolean;
  disabledReason?: string;
}) {
  const baseTitle = manifest.description ?? `Click to add  ·  Drag to position\n${manifest.typeKey}`;
  const title = disabled && disabledReason ? disabledReason : baseTitle;

  return (
    <button
      type="button"
      className="wf-palette-item"
      data-disabled={disabled}
      // draggable=false on disabled items so the user can't bypass the click guard via drag.
      draggable={!disabled}
      disabled={disabled}
      onClick={() => onAdd(manifest)}
      onDragStart={(e) => {
        if (disabled) { e.preventDefault(); return; }
        e.dataTransfer.setData("application/x-workflow-node", manifest.typeKey);
        e.dataTransfer.effectAllowed = "move";
      }}
      title={title}
    >
      <span className="wf-palette-item-icon">{nodeIconFor(manifest)}</span>
      <span className="wf-palette-item-body">
        <span className="wf-palette-item-name">{manifest.displayName}</span>
        <span className="wf-palette-item-key">{manifest.typeKey}</span>
      </span>
      <span className="wf-palette-item-add" aria-hidden>{disabled ? "—" : "+"}</span>
    </button>
  );
}

// ─── Top-bar toolbar button ───────────────────────────────────────────────────

/**
 * Icon-first button for the top-bar workflow-scope toolbar. Counts render as a small
 * badge on the icon so the icon stays the primary affordance and the user can spot at a
 * glance which scopes have content without reading text labels. Tooltip carries the
 * concept explanation (and the Dify equivalence note for users coming from there).
 */
function ToolbarButton({ icon, label, count, active, onClick, tooltip }: {
  icon: React.ReactNode;
  label: string;
  // Count is optional — wf + team scopes pull from the API on-demand inside their panels,
  // so the editor doesn't know the count up-front to badge it.
  count?: number;
  active: boolean;
  onClick: () => void;
  tooltip: string;
}) {
  return (
    <button
      type="button"
      className="wf-editor-toolbar-btn wf-editor-toolbar-btn-scope"
      data-active={active}
      onClick={onClick}
      title={tooltip}
      aria-label={label}
      aria-pressed={active}
    >
      {icon}
      {count != null && count > 0 && <span className="wf-editor-toolbar-badge">{count}</span>}
    </button>
  );
}

// ─── Inspector ────────────────────────────────────────────────────────────────

function NodeInspector({
  nodeId,
  manifest,
  label,
  config,
  inputs,
  liveDefinition,
  manifestByType,
  workflowVariables,
  teamVariables,
  systemVariables,
  projectVariables,
  inputFields,
  onInputFieldsChange,
  onLabelChange,
  onConfigChange,
  onInputsChange,
  currentWorkflowId,
  retry,
  onRetryChange,
  errorTarget,
  errorTargetOptions,
  onErrorRouteChange,
}: {
  nodeId: string;
  manifest: NodeManifestDto;
  label: string;
  config: Record<string, unknown>;
  inputs: Record<string, unknown>;
  liveDefinition: WorkflowDefinition;
  manifestByType: Map<string, NodeManifestDto>;
  workflowVariables: import("@/api/variables").VariableSummary[];
  teamVariables: import("@/api/variables").VariableSummary[];
  systemVariables: import("@/api/workflows").SystemVariableDto[];
  projectVariables: ReadonlyArray<{ slug: string; variables: ReadonlyArray<import("@/api/variables").VariableSummary> }>;
  inputFields: WorkflowVariable[];
  onInputFieldsChange: (next: WorkflowVariable[]) => void;
  onLabelChange: (v: string) => void;
  onConfigChange: (v: Record<string, unknown>) => void;
  onInputsChange: (v: Record<string, unknown>) => void;
  currentWorkflowId: string;
  retry: RetryPolicy | null;
  onRetryChange: (v: RetryPolicy | null) => void;
  errorTarget: string | null;
  errorTargetOptions: { id: string; label: string }[];
  onErrorRouteChange: (targetId: string | null) => void;
}) {
  // Compute scope at THIS node's position. Re-runs every render — cheap, definition is
  // already in memory. Feeds the autocomplete picker + the "Provides" hint card.
  const suggestions = useMemo(() => introspectScope({
    definition: liveDefinition,
    currentNodeId: nodeId,
    manifestByType,
    workflowVariables,
    teamVariables,
    systemVariables,
    projectVariables,
  }), [liveDefinition, nodeId, manifestByType, workflowVariables, teamVariables, systemVariables, projectVariables]);

  const ownOutputs = useMemo(() => {
    const schema = manifest.outputSchema as { properties?: Record<string, { type?: string | string[]; description?: string }> } | undefined;
    const declared = schema?.properties
      ? Object.entries(schema.properties).map(([name, prop]) => ({
          name,
          type: Array.isArray(prop.type) ? prop.type.join("|") : prop.type,
          description: prop.description,
          refPath: `nodes.${nodeId}.outputs.${name}`,
        }))
      : [];

    // Every non-trigger node also emits the universal `error` output when it fails (Phase 2) —
    // list it so the author discovers it's referenceable from an error-branch handler.
    const errorOutputs = manifest.kind !== "Trigger"
      ? [
          { name: "error.message", type: "string", description: "Failure message — populated on the error branch", refPath: `nodes.${nodeId}.outputs.error.message` },
          { name: "error.node", type: "string", description: "The failing node's id", refPath: `nodes.${nodeId}.outputs.error.node` },
        ]
      : [];

    return [...declared, ...errorOutputs];
  }, [manifest, nodeId]);

  // Outputs section is collapsed by default — operator opens it on demand when wiring
  // a downstream {{ref}}. Most of the time the author is editing Config / Inputs and
  // doesn't need the output list cluttering the panel.
  const [outputsOpen, setOutputsOpen] = useState(false);

  return (
    <div className="wf-inspector-body">
      <header className="wf-inspector-h">
        {/* Lead with the human name (the editable Label below, falling back to the type's display name);
            the node id is the immutable reference key (used in {{nodes.<id>.outputs.…}}), shown muted. */}
        <div className="wf-inspector-h-name">{label || manifest.displayName}</div>
        <div className="wf-inspector-h-ref">{nodeId}</div>
        <code className="wf-inspector-h-key">{manifest.typeKey}</code>
      </header>

      <section className="wf-inspector-section">
        <label className="wf-form-row">
          <span className="wf-form-label">Label</span>
          <input
            className="wf-form-input"
            value={label}
            onChange={(e) => onLabelChange(e.target.value)}
            placeholder={manifest.displayName}
          />
        </label>
      </section>

      {/* Start from a template — a node that declares presets offers an intent-first chooser that fills
          Config + Inputs, so the author tweaks instead of assembling atoms. Generic: driven by the manifest. */}
      {manifest.presets != null && manifest.presets.length > 0 && (
        <section className="wf-inspector-section">
          <NodePresetPicker presets={manifest.presets} onApply={(p) => { onConfigChange(p.config); onInputsChange(p.inputs); }} />
        </section>
      )}

      {/* Manual Start node: its inspector IS the input-fields editor (Dify-style "inputs on the
          start node"). Its Config/Input schemas are empty, so there's nothing else to show. */}
      {manifest.isManual ? (
        <StartNodeInputsEditor inputs={inputFields} onChange={onInputFieldsChange} />
      ) : manifest.typeKey === "flow.subworkflow" ? (
        // Sub-workflow gets a dedicated editor: pick the child workflow, then map onto ITS declared
        // inputs (rendered via SchemaForm) — far better than the generic "workflowId + raw object".
        <SubworkflowEditor
          config={config}
          inputs={inputs}
          onConfigChange={onConfigChange}
          onInputsChange={onInputsChange}
          suggestions={suggestions}
          currentWorkflowId={currentWorkflowId}
        />
      ) : manifest.typeKey === "flow.loop" ? (
        // Loop gets Dify's settings panel (loop variables · termination · max iterations) instead of
        // the generic Config/Inputs forms; its body subgraph is authored on the canvas.
        <LoopEditor config={config} onConfigChange={onConfigChange} suggestions={suggestions} />
      ) : manifest.typeKey === "flow.loop_start" ? (
        // The loop body's entry marker — auto-added inside a Loop. Nothing to configure.
        <section className="wf-inspector-section">
          <p className="wf-retry-hint">Loop body entry — added automatically inside a Loop. Connect the loop's body steps from here; there's nothing to configure.</p>
        </section>
      ) : manifest.typeKey === "flow.try" ? (
        // The try/catch scope — no config; wrap steps inside and wire the catch handle to a handler.
        <section className="wf-inspector-section">
          <p className="wf-retry-hint">Try / catch scope — wrap steps inside. If any body step fails unhandled, the run takes the <code>catch</code> branch (carrying the failure) instead of failing. Nothing to configure.</p>
        </section>
      ) : manifest.typeKey === "flow.try_start" ? (
        // The try body's entry marker — auto-added inside a Try. Nothing to configure.
        <section className="wf-inspector-section">
          <p className="wf-retry-hint">Try body entry — added automatically inside a Try. Connect the try's body steps from here; there's nothing to configure.</p>
        </section>
      ) : (
        <>
          <section className="wf-inspector-section">
            <div className="wf-inspector-section-h">Config</div>
            <SchemaForm
              schema={manifest.configSchema}
              value={config}
              onChange={onConfigChange}
              variableSuggestions={suggestions}
            />
          </section>

          <section className="wf-inspector-section">
            <div className="wf-inspector-section-h">Inputs</div>
            {/* chat.post_message gets a custom Inputs editor: a single "Interaction type" picker
                (None / Buttons / Form / …) replaces the confusing "fill actions / form / component
                simultaneously" layout. The options are DATA-DRIVEN from the manifest's
                x-interactionField properties, so adding a new interaction kind requires only a
                manifest change — this component doesn't need to change. */}
            {manifest.typeKey === "chat.post_message" ? (
              <PostMessageInputsEditor
                nodeId={nodeId}
                inputs={inputs}
                onChange={onInputsChange}
                variableSuggestions={suggestions}
                inputSchema={manifest.inputSchema}
              />
            ) : (
              <SchemaForm
                schema={manifest.inputSchema}
                value={inputs}
                onChange={onInputsChange}
                templateHint
                variableSuggestions={suggestions}
              />
            )}
          </section>
        </>
      )}

      {/* Retry-on-failure — a cross-cutting engine setting, shown for any node that can fail.
          Triggers are the run's entry point (nothing to retry). A Loop has its own iteration
          semantics (the engine doesn't honour node-level retry on it), and loop_start is a passthrough
          marker — both are excluded. */}
      {manifest.kind !== "Trigger" && manifest.typeKey !== "flow.loop" && manifest.typeKey !== "flow.loop_start" && (
        <NodeRetryEditor value={retry} onChange={onRetryChange} />
      )}

      {/* Error routing — mirrors the canvas red `error` handle. Picking a target wires an error
          edge; the engine routes here on failure (after retries) instead of failing the run. Shown
          for regular steps AND a Loop (a body failure can route the loop down its error edge); never
          for the loop_start marker. */}
      {(manifest.kind === "Regular" || manifest.typeKey === "flow.loop") && manifest.typeKey !== "flow.loop_start" && (
        <section className="wf-inspector-section">
          <div className="wf-inspector-section-h">On failure</div>
          <select
            className="wf-form-input"
            value={errorTarget ?? ""}
            onChange={(e) => onErrorRouteChange(e.target.value || null)}
          >
            <option value="">Fail the run (default)</option>
            {errorTargetOptions.map((o) => (
              <option key={o.id} value={o.id}>Route to “{o.label}”</option>
            ))}
          </select>
          <p className="wf-retry-hint">
            If this node fails (after any retries), route the run to the chosen node instead of
            failing it. The failure is available there as <code>{`{{nodes.${nodeId}.outputs.error.message}}`}</code>.
          </p>
        </section>
      )}

      {/* Output section — what THIS node emits for downstream nodes to reference. Header
          acts as a toggle; the body (intro + per-output {{ref}} pill list) renders only
          when the operator opens it. Default-collapsed keeps the inspector quiet for the
          common case of editing Config / Inputs. */}
      {ownOutputs.length > 0 && (
        <section className="wf-inspector-section wf-inspector-section-collapsible" data-open={outputsOpen}>
          <button
            type="button"
            className="wf-inspector-section-toggle"
            onClick={() => setOutputsOpen((v) => !v)}
            aria-expanded={outputsOpen}
          >
            {outputsOpen ? <Ic.ChevronDown size={11} /> : <Ic.ChevronRight size={11} />}
            <span className="wf-inspector-section-h">Output</span>
            <span className="wf-inspector-section-count">{ownOutputs.length}</span>
          </button>

          {outputsOpen && (
            <div className="wf-outputs-hint">
              <div className="wf-outputs-hint-intro">Downstream nodes reference this node's outputs via:</div>
              <ul className="wf-outputs-hint-list">
                {ownOutputs.map((o) => (
                  <li key={o.name} className="wf-outputs-hint-row">
                    <code
                      className="wf-outputs-hint-path"
                      title="Click to copy"
                      onClick={() => navigator.clipboard?.writeText(`{{${o.refPath}}}`)}
                    >{`{{${o.refPath}}}`}</code>
                    {o.type && <span className="wf-outputs-hint-type">{o.type}</span>}
                    {o.description && <span className="wf-outputs-hint-desc">{o.description}</span>}
                  </li>
                ))}
              </ul>
            </div>
          )}
        </section>
      )}
    </div>
  );
}

// ─── React Flow ↔ WorkflowDefinition glue ─────────────────────────────────────

const NODE_TYPES = { wf: WorkflowNode };

/** A node's nesting depth in the loop hierarchy — parentId hops to a top-level node (0 = top-level, 1 = loop body, 2 = nested-loop body, …). Drives loop-nesting target selection + the depth-based zIndex restacking on reparent. */
function nodeDepth(id: string, nodes: Node[]): number {
  const byId = new Map(nodes.map((n) => [n.id, n]));
  let depth = 0;
  let parent = byId.get(id)?.parentId;
  while (parent) { depth++; parent = byId.get(parent)?.parentId; }
  return depth;
}

function definitionToRfEdges(def: WorkflowDefinition): Edge[] {
  return def.edges.map((e, idx) => {
    const isError = e.sourceHandle === ERROR_HANDLE;
    const isCatch = e.sourceHandle === CATCH_HANDLE;
    return {
      id: `e${idx}-${e.from}-${e.to}`,
      source: e.from,
      target: e.to,
      // Round-trip the source handle so error / catch (and any future named-handle) edges re-anchor to
      // the right port on load — a null handle stays the node's default output.
      sourceHandle: e.sourceHandle ?? undefined,
      type: "default",
      animated: isError || isCatch || undefined,
      label: e.condition ?? undefined,
      ...(isError ? { className: "wf-rf-edge-error" } : isCatch ? { className: "wf-rf-edge-catch" } : {}),
    };
  });
}

function rfToDefinition(
  nodes: Node<WorkflowNodeData>[],
  edges: Edge[],
  configs: Record<string, Record<string, unknown>>,
  inputs: Record<string, Record<string, unknown>>,
  labels: Record<string, string>,
  retries: Record<string, RetryPolicy>,
  workflowInputs: import("@/api/workflows").WorkflowVariable[] = [],
  workflowOutputs: import("@/api/workflows").WorkflowVariable[] = []
): WorkflowDefinition {
  const nodeDefs: NodeDefinition[] = nodes.map((n) => ({
    id: n.id,
    typeKey: n.data.typeKey,
    label: labels[n.id]?.length ? labels[n.id] : null,
    config: configs[n.id] ?? {},
    inputs: inputs[n.id] ?? {},
    position: { x: Math.round(n.position.x), y: Math.round(n.position.y) },
    // Persist an explicit container size only when the user resized the loop box (data.size set);
    // otherwise it's omitted so the loop keeps auto-sizing and the content hash is unchanged.
    ...(n.data.size ? { width: Math.round(n.data.size.width), height: Math.round(n.data.size.height) } : {}),
    // Omit retry entirely when absent so the saved definition (and its content hash) is
    // unchanged for nodes the operator never gave a policy.
    ...(retries[n.id] ? { retry: retries[n.id] } : {}),
    // A node nested inside a flow.loop container carries its parent (React Flow `parentId`).
    // Omitted when top-level so the hash is unchanged for every non-loop workflow.
    ...(n.parentId ? { parentId: n.parentId } : {}),
  }));

  const edgeDefs: EdgeDefinition[] = edges.map((e) => ({
    from: e.source,
    to: e.target,
    sourceHandle: e.sourceHandle ?? null,
    targetHandle: e.targetHandle ?? null,
    condition: typeof e.label === "string" && e.label.length > 0 ? e.label : null,
  }));

  return {
    // wf + team scope variables live in the unified `variable` table — their values are
    // not part of the workflow JSON.
    schemaVersion: 1,
    nodes: nodeDefs,
    edges: edgeDefs,
    inputs: workflowInputs,
    outputs: workflowOutputs,
  };
}

// ─── Helpers ──────────────────────────────────────────────────────────────────

function initialBag(def: WorkflowDefinition, key: "config" | "inputs"): Record<string, Record<string, unknown>> {
  const out: Record<string, Record<string, unknown>> = {};
  for (const n of def.nodes) {
    const v = key === "config" ? n.config : n.inputs;
    const raw = (typeof v === "object" && v !== null) ? v as Record<string, unknown> : {};
    // PR-trigger configs saved before #25 use the legacy `{ repositoryId, labels? }`
    // shape. The new schema declares `repositories: [...]` and the picker only
    // renders THAT property, so a legacy config would appear empty in the UI. Auto-
    // migrate at load time so the operator sees their existing filter as one row.
    // The matcher tolerates both shapes (PrTriggerMatcherFilter), so functional
    // behaviour is identical — this is purely a UI population fix.
    out[n.id] = key === "config" ? migrateLegacyPrTriggerConfig(raw) : raw;
  }
  return out;
}

function uniqueNodeId(typeKey: string, existing: Node[]): string {
  // Generate "<segment>" or "<segment>_2" etc. Segment = the last dot-separated piece
  // of typeKey (e.g. trigger.pr.opened → opened), lowercased + de-collided.
  const base = typeKey.split(".").pop() ?? "node";
  const used = new Set(existing.map((n) => n.id));
  if (!used.has(base)) return base;
  let i = 2;
  while (used.has(`${base}_${i}`)) i++;
  return `${base}_${i}`;
}

function defaultsFromSchema(schema: unknown): Record<string, unknown> {
  if (typeof schema !== "object" || schema == null) return {};
  const s = schema as { properties?: Record<string, { type?: string; default?: unknown; properties?: unknown }> };
  const out: Record<string, unknown> = {};
  if (!s.properties) return out;
  for (const [k, v] of Object.entries(s.properties)) {
    if (v && "default" in v && v.default !== undefined) { out[k] = v.default; continue; }

    // Recurse into a structured nested object so its sub-defaults seed too (e.g. resolve { mode:"first", count:2 }).
    if (v && v.type === "object" && v.properties) {
      const nested = defaultsFromSchema(v);
      if (Object.keys(nested).length > 0) out[k] = nested;
    }
  }
  return out;
}

/**
 * Would adding the edge (source → target) create a cycle? Cycle iff target can already
 * reach source via existing edges. Simple BFS forward from target — terminates because the
 * existing graph is a DAG (every previous addition went through the same check).
 */
function createsCycle(source: string, target: string, edges: Edge[]): boolean {
  const adjacency = new Map<string, string[]>();
  for (const e of edges) {
    const list = adjacency.get(e.source);
    if (list) list.push(e.target); else adjacency.set(e.source, [e.target]);
  }

  const visited = new Set<string>();
  const stack: string[] = [target];

  while (stack.length > 0) {
    const current = stack.pop()!;
    if (current === source) return true;
    if (visited.has(current)) continue;
    visited.add(current);

    const next = adjacency.get(current);
    if (next) for (const n of next) stack.push(n);
  }

  return false;
}
